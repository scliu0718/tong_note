using System.Net;
using System.Text;
using Tonote.Core;
using Xunit;

namespace Tonote.Core.Tests;

/// <summary>
/// 批次 0 故障注入回归网：验证同步三铁律——
/// ① 下载失败绝不能当「云端无文件」（本地旧数据不得覆盖云端）；
/// ② 云端存在性由「目录列表 + 是否同步过」判定；
/// ③ publish 的 create errno=0 即已写入，回读只做确认、不得 rev+1 重传；
/// 以及附件失败留下轮重试、错误按强类型分类（不靠中文子串）。
/// </summary>
public class FaultInjectionTests
{
    // MARK: - W-01 下载链路绝不抛 notFound / 不覆盖云端

    [Fact]
    public async Task Download404_NeverOverwritesCloud()
    {
        var id = Guid.NewGuid();
        var remoteUpdated = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        var remote = new Note { Id = id, Title = "云端最新", Body = "<p>云端内容</p>", Rev = 3, UpdatedAt = remoteUpdated, ContentFormat = NoteContentFormat.richText };

        var root = Temp();
        File.WriteAllText(Path.Combine(root, "notes.json"), TonoteJson.Serialize(new List<Note>
        {
            new Note { Id = id, Title = "本地旧版", Body = "<p>本地内容</p>", Rev = 2, UpdatedAt = remoteUpdated.AddMinutes(-10), ContentFormat = NoteContentFormat.richText },
        }));
        File.WriteAllText(Path.Combine(root, "note-sync.json"), TonoteJson.Serialize(
            new Dictionary<string, SyncCursor> { [Key(id)] = new SyncCursor { BaseRev = 2, BaseUpdatedAt = remoteUpdated.AddMinutes(-10), Dirty = true } }));

        var fake = new FakePan();
        fake.Seed(NotePath(id), Encoding.UTF8.GetBytes(TonoteJson.Serialize(remote)));
        fake.FailDownloadPaths.Add(NotePath(id));

        var (engine, _) = Build(root, fake);
        await engine.QuickSyncAsync();

        // 直链 404 是临时错误，绝不能退化成「云端无文件」而用本地旧版覆盖
        var cloud = TonoteJson.Deserialize<Note>(Encoding.UTF8.GetString(fake[NotePath(id)]!))!;
        Assert.Equal(3, cloud.Rev);
        Assert.Equal("云端最新", cloud.Title);
        Assert.Equal(0, fake.Creates(NotePath(id)));
    }

    [Fact]
    public async Task NetworkDown_NeverOverwritesCloud()
    {
        var id = Guid.NewGuid();
        var remote = new Note { Id = id, Title = "云端", Body = "<p>云端内容</p>", Rev = 5, UpdatedAt = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), ContentFormat = NoteContentFormat.richText };

        var root = Temp();
        File.WriteAllText(Path.Combine(root, "notes.json"), TonoteJson.Serialize(new List<Note>
        {
            new Note { Id = id, Title = "本地", Body = "<p>本地内容</p>", Rev = 4, UpdatedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), ContentFormat = NoteContentFormat.richText },
        }));
        File.WriteAllText(Path.Combine(root, "note-sync.json"), TonoteJson.Serialize(
            new Dictionary<string, SyncCursor> { [Key(id)] = new SyncCursor { BaseRev = 4, BaseUpdatedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), Dirty = true } }));

        var fake = new FakePan();
        fake.Seed(NotePath(id), Encoding.UTF8.GetBytes(TonoteJson.Serialize(remote)));
        fake.FailAll = true;

        var (engine, _) = Build(root, fake);
        await engine.QuickSyncAsync();

        var cloud = TonoteJson.Deserialize<Note>(Encoding.UTF8.GetString(fake[NotePath(id)]!))!;
        Assert.Equal(5, cloud.Rev);
        Assert.Equal(0, fake.Creates(NotePath(id)));
    }

    [Fact]
    public async Task Errno20020_FallsBackToFilemetas()
    {
        var id = Guid.NewGuid();
        var remote = new Note { Id = id, Title = "云端", Body = "<p>通过 filemetas 下载</p>", Rev = 1, UpdatedAt = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), ContentFormat = NoteContentFormat.richText };

        var fake = new FakePan();
        fake.Seed(NotePath(id), Encoding.UTF8.GetBytes(TonoteJson.Serialize(remote)));
        fake.Return20020 = true;

        var root = Temp();
        var (engine, notes) = Build(root, fake);
        await engine.QuickSyncAsync();

        var local = notes.Find(id);
        Assert.NotNull(local);
        Assert.Equal("云端", local!.Title);
        Assert.Equal("<p>通过 filemetas 下载</p>", local.Body);
        Assert.True(fake.FilemetasCount > 0, "errno 20020 后应降级走 filemetas 取直链");
    }

    [Fact]
    public async Task DownloadData_NeverThrowsNotFound()
    {
        var fake = new FakePan();
        var root = Temp();
        SeedToken(root);
        var client = new BaiduPanClient(root, new HttpClient(fake));

        // 云端没有这个文件：下载链路只抛临时错误，绝不抛 notFound（存在性由调用方按目录列表判定）
        var error = await Assert.ThrowsAsync<BaiduPanTransientException>(() =>
            client.DownloadDataAsync("/apps/tong_log/notes/" + Guid.NewGuid().ToString("D").ToUpperInvariant() + ".json", "token"));
        Assert.IsNotType<BaiduPanNotFoundException>(error);
    }

    // MARK: - W-02 云端存在性三分支

    [Fact]
    public async Task SyncedNoteMissingFromListing_SkipsInsteadOfUploading()
    {
        var id = Guid.NewGuid();
        var updated = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

        var root = Temp();
        File.WriteAllText(Path.Combine(root, "notes.json"), TonoteJson.Serialize(new List<Note>
        {
            new Note { Id = id, Title = "本地", Body = "<p>本地内容</p>", Rev = 2, UpdatedAt = updated, ContentFormat = NoteContentFormat.richText },
        }));
        File.WriteAllText(Path.Combine(root, "note-sync.json"), TonoteJson.Serialize(
            new Dictionary<string, SyncCursor> { [Key(id)] = new SyncCursor { BaseRev = 2, BaseUpdatedAt = updated, Dirty = true } }));

        // 云端目录里没有这个 id（可能刚被别端删除）：曾同步过 → 跳过本轮，绝不用本地旧版「复活」它
        var fake = new FakePan();
        var (engine, notes) = Build(root, fake);
        await engine.QuickSyncAsync();

        Assert.Equal(0, fake.Creates(NotePath(id)));
        Assert.Null(fake[NotePath(id)]);
        Assert.Equal("本地", notes.Find(id)!.Title);
    }

    // MARK: - W-03 回读只确认，不 rev+1 重传

    [Fact]
    public async Task ReadbackFailure_DoesNotReupload()
    {
        var id = Guid.NewGuid();

        var root = Temp();
        File.WriteAllText(Path.Combine(root, "notes.json"), TonoteJson.Serialize(new List<Note>
        {
            new Note { Id = id, Title = "新笔记", Body = "<p>内容</p>", Rev = 0, UpdatedAt = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), ContentFormat = NoteContentFormat.richText },
        }));
        File.WriteAllText(Path.Combine(root, "note-sync.json"), TonoteJson.Serialize(
            new Dictionary<string, SyncCursor> { [Key(id)] = new SyncCursor { Dirty = true } }));

        var fake = new FakePan();
        fake.FailDownloadPaths.Add(NotePath(id)); // create 成功、回读直链失败

        var (engine, notes) = Build(root, fake);
        await engine.QuickSyncAsync();

        // create errno=0 即已写入：回读失败只重试回读、不重传、不 rev+1
        Assert.Equal(1, fake.Creates(NotePath(id)));
        var cloud = TonoteJson.Deserialize<Note>(Encoding.UTF8.GetString(fake[NotePath(id)]!))!;
        Assert.Equal(1, cloud.Rev);
        Assert.False(notes.Cursor(id).Dirty, "已发布应清掉 dirty，留待下一轮校验");
    }

    // MARK: - W-04 附件失败留下轮重试

    [Fact]
    public async Task AttachmentDownloadFailure_MarksServerMtimeNull()
    {
        var id = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var rel = "attachments/" + Key(id) + "/" + Key(fileId) + ".png";
        var remote = new Note
        {
            Id = id,
            Title = "带图笔记",
            Body = "<p>文字</p><img src=\"" + rel + "\">",
            Rev = 1,
            UpdatedAt = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
            ContentFormat = NoteContentFormat.richText,
        };

        var fake = new FakePan();
        fake.Seed(NotePath(id), Encoding.UTF8.GetBytes(TonoteJson.Serialize(remote)));
        fake.Seed("/apps/tong_log/" + rel, new byte[] { 1, 2, 3, 4 });
        fake.FailDownloadPaths.Add("/apps/tong_log/" + rel); // 只让附件直链 404，笔记正文仍可下载

        var root = Temp();
        var (engine, notes) = Build(root, fake);
        await engine.QuickSyncAsync();

        // 笔记正文拉下来了，但附件没拉到 → complete=false → serverMtime=null，下一轮不再被 ShouldSkipFetch 跳过
        var local = notes.Find(id);
        Assert.NotNull(local);
        Assert.Equal("带图笔记", local!.Title);
        Assert.Null(notes.Cursor(id).ServerMtime);
        Assert.False(notes.Attachments.FileExists(rel));
    }

    // MARK: - W-05 强类型错误分类：永久错误不进入同轮重试

    [Fact]
    public async Task RateLimit_DoesNotRepeatedlyUpload()
    {
        var id = Guid.NewGuid();

        var root = Temp();
        File.WriteAllText(Path.Combine(root, "notes.json"), TonoteJson.Serialize(new List<Note>
        {
            new Note { Id = id, Title = "新笔记", Body = "<p>内容</p>", Rev = 0, UpdatedAt = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), ContentFormat = NoteContentFormat.richText },
        }));
        File.WriteAllText(Path.Combine(root, "note-sync.json"), TonoteJson.Serialize(
            new Dictionary<string, SyncCursor> { [Key(id)] = new SyncCursor { Dirty = true } }));

        var fake = new FakePan();
        fake.RateLimit31034 = true; // create 返回 errno 31034（频控）

        var (engine, notes) = Build(root, fake);
        await engine.QuickSyncAsync();

        // 频控是永久错误，同轮只尝试一次、不循环重传；本地保持 dirty，云端仍无文件
        Assert.Equal(1, fake.Creates(NotePath(id)));
        Assert.True(notes.Cursor(id).Dirty);
        Assert.Null(fake[NotePath(id)]);
    }

    // MARK: - 辅助

    private static string Key(Guid id) => id.ToString("D").ToUpperInvariant();

    private static string NotePath(Guid id) => "/apps/tong_log/notes/" + Key(id) + ".json";

    private static string Temp() => Directory.CreateTempSubdirectory("tonote-fault-").FullName;

    private static void SeedToken(string root)
    {
        new BaiduPanTokenStore(root).Save(new BaiduPanToken
        {
            AccessToken = "test-access-token",
            RefreshToken = "test-refresh-token",
            ExpiresIn = 2592000,
            CreatedAt = DateTime.UtcNow,
        });
    }

    private static (SyncEngine Engine, NoteStore Notes) Build(string root, FakePan fake)
    {
        SeedToken(root);
        // 基线：云端已有一份空笔记本清单与空旧清单，避免每轮为缺失文件走 3 次下载重试退避
        fake.Seed("/apps/tong_log/notebooks.json", Encoding.UTF8.GetBytes("{\"names\":[],\"removed\":[]}"));
        fake.Seed("/apps/tong_log/manifest.json", Encoding.UTF8.GetBytes("{\"notes\":[],\"todos\":[]}"));
        var notes = new NoteStore(root);
        var todos = new TodoStore(root);
        var notebooks = new NotebookStore(root);
        var client = new BaiduPanClient(root, new HttpClient(fake));
        var engine = new SyncEngine(notes, todos, notebooks, root, client);
        return (engine, notes);
    }

    /// <summary>内存假网盘：模拟 xpan list / download / filemetas / precreate / superfile2 / create 与直链 GET。</summary>
    private sealed class FakePan : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _fsIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _dirs = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _createCounts = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, byte[]> _pendingUploads = new(StringComparer.Ordinal);
        private long _nextFsId = 1000;
        private long _nextMtime = 1_700_000_000;
        private long _nextUploadId = 1;

        // 故障开关
        public bool FailAll;                          // 所有接口返回 502
        public bool FailDownload;                     // 直链 GET 返回 404
        public bool Return20020;                      // method=download 恒返回 errno 20020
        public bool RateLimit31034;                   // method=create 返回 errno 31034
        public readonly HashSet<string> FailDownloadPaths = new(StringComparer.OrdinalIgnoreCase);

        public int FilemetasCount { get; private set; }

        public int Creates(string path) => _createCounts.GetValueOrDefault(path);

        public byte[]? this[string path]
        {
            get => _files.TryGetValue(path, out var value) ? value : null;
        }

        public void Seed(string path, byte[] data)
        {
            _files[path] = data;
            var fsId = _nextFsId++;
            _fsIds[path] = fsId;
            AddDirs(path);
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var query = ParseQuery(uri.Query);

            if (uri.Host == "fake.local")
            {
                // 直链 GET
                var path = query.GetValueOrDefault("path") ?? "";
                if (FailAll || FailDownload || FailDownloadPaths.Contains(path))
                    return Status(HttpStatusCode.NotFound);
                if (!_files.TryGetValue(path, out var data))
                    return Status(HttpStatusCode.NotFound);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
            }

            if (FailAll)
                return Status(HttpStatusCode.BadGateway);

            var method = query.GetValueOrDefault("method") ?? "";
            switch (method)
            {
                case "list":
                    return List(query.GetValueOrDefault("dir") ?? "/");
                case "download":
                    return Download(query.GetValueOrDefault("path") ?? "");
                case "filemetas":
                    return Filemetas(query.GetValueOrDefault("fsids") ?? "");
                case "precreate":
                    return Json("{\"errno\":0,\"uploadid\":\"up-" + _nextUploadId++ + "\",\"return_type\":1,\"block_list\":[]}");
                case "upload":
                    return await UploadAsync(request, query, cancellationToken).ConfigureAwait(false);
                case "create":
                    return await CreateAsync(request, cancellationToken).ConfigureAwait(false);
                case "filemanager":
                    return Json("{\"errno\":0}");
                default:
                    return Status(HttpStatusCode.NotImplemented);
            }
        }

        private HttpResponseMessage List(string dir)
        {
            dir = dir.TrimEnd('/');
            if (!_dirs.Contains(dir) && dir.Length > 0)
                return Json("{\"errno\":-9}");
            var items = new List<string>();
            foreach (var (path, data) in _files)
            {
                var prefix = dir + "/";
                if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                var name = path[(prefix.Length)..];
                if (name.Contains('/')) continue; // 只列直接子项
                var mtime = _nextMtime++;
                items.Add("{\"path\":\"" + path + "\",\"server_filename\":\"" + name + "\",\"isdir\":0,\"server_mtime\":" + mtime + ",\"fs_id\":" + _fsIds[path] + ",\"size\":" + data.Length + "}");
            }
            return Json("{\"errno\":0,\"list\":[" + string.Join(",", items) + "]}");
        }

        private HttpResponseMessage Download(string path)
        {
            if (Return20020)
                return Json("{\"errno\":20020,\"list\":[]}");
            if (!_files.TryGetValue(path, out _))
                return Json("{\"errno\":0,\"list\":[]}");
            return Json("{\"errno\":0,\"list\":[{\"dlink\":\"https://fake.local/d?path=" + Uri.EscapeDataString(path) + "\"}]}");
        }

        private HttpResponseMessage Filemetas(string fsids)
        {
            FilemetasCount++;
            // fsids 形如 [1234]
            var inner = fsids.Trim('[', ']', ' ');
            if (!long.TryParse(inner, out var fsId))
                return Json("{\"errno\":0,\"info\":[]}");
            string? path = _fsIds.FirstOrDefault(pair => pair.Value == fsId).Key;
            if (string.IsNullOrEmpty(path))
                return Json("{\"errno\":0,\"info\":[]}");
            return Json("{\"errno\":0,\"info\":[{\"dlink\":\"https://fake.local/d?path=" + Uri.EscapeDataString(path) + "\"}]}");
        }

        private async Task<HttpResponseMessage> UploadAsync(HttpRequestMessage request,
            Dictionary<string, string> query, CancellationToken cancellationToken)
        {
            var uploadId = query.GetValueOrDefault("uploadid") ?? "";
            var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var file = ExtractMultipartFile(body, request.Content.Headers.ContentType?.ToString());
            if (file != null)
                _pendingUploads[uploadId] = file;
            return Json("{\"errno\":0}");
        }

        private async Task<HttpResponseMessage> CreateAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var form = ParseForm(body);
            var path = form.GetValueOrDefault("path") ?? "";
            var uploadId = form.GetValueOrDefault("uploadid") ?? "";

            _createCounts[path] = _createCounts.GetValueOrDefault(path) + 1;

            if (RateLimit31034)
                return Json("{\"errno\":31034}");

            // 从 superfile2 上传阶段拿回文件体（单分片场景直接落盘）
            var data = _pendingUploads.GetValueOrDefault(uploadId) ?? Array.Empty<byte>();
            _pendingUploads.Remove(uploadId);
            _files[path] = data;
            var fsId = _nextFsId++;
            _fsIds[path] = fsId;
            AddDirs(path);
            return Json("{\"errno\":0,\"fs_id\":" + fsId + "}");
        }

        /// <summary>从 multipart/form-data 里抽出 file 字段的字节（单分片场景，只有一个文件字段）。</summary>
        private static byte[]? ExtractMultipartFile(byte[] body, string? contentType)
        {
            if (string.IsNullOrEmpty(contentType)) return null;
            var idx = contentType.IndexOf("boundary=", StringComparison.Ordinal);
            if (idx < 0) return null;
            var boundary = "--" + contentType[(idx + "boundary=".Length)..].Trim('"').Trim();
            var marker = Encoding.ASCII.GetBytes(boundary);
            var sep = "\r\n\r\n"u8.ToArray();

            var headerEnd = IndexOf(body, sep, 0);
            if (headerEnd < 0) return null;
            var dataStart = headerEnd + sep.Length;

            var closeIdx = LastIndexOf(body, marker);
            if (closeIdx < dataStart) return null;
            var dataEnd = closeIdx - 2; // 去掉 --boundary 前的 \r\n
            if (dataEnd < dataStart) return Array.Empty<byte>();
            return body[dataStart..dataEnd];
        }

        private static int IndexOf(byte[] haystack, byte[] needle, int start)
        {
            for (var i = start; i + needle.Length <= haystack.Length; i++)
            {
                var match = true;
                for (var j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] == needle[j]) continue;
                    match = false;
                    break;
                }
                if (match) return i;
            }
            return -1;
        }

        private static int LastIndexOf(byte[] haystack, byte[] needle)
        {
            for (var i = haystack.Length - needle.Length; i >= 0; i--)
            {
                var match = true;
                for (var j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] == needle[j]) continue;
                    match = false;
                    break;
                }
                if (match) return i;
            }
            return -1;
        }

        private void AddDirs(string path)
        {
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var sb = new StringBuilder();
            for (var i = 0; i < parts.Length - 1; i++)
            {
                sb.Append('/').Append(parts[i]);
                _dirs.Add(sb.ToString());
            }
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>();
            var q = query.TrimStart('?');
            if (q.Length == 0) return result;
            foreach (var pair in q.Split('&'))
            {
                var eq = pair.IndexOf('=');
                if (eq < 0) { result[Uri.UnescapeDataString(pair)] = ""; continue; }
                result[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
            return result;
        }

        private static Dictionary<string, string> ParseForm(byte[] body)
        {
            var text = Encoding.UTF8.GetString(body);
            var result = new Dictionary<string, string>();
            foreach (var pair in text.Split('&'))
            {
                var eq = pair.IndexOf('=');
                if (eq < 0) continue;
                result[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
            return result;
        }

        private static HttpResponseMessage Json(string body)
            => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Status(HttpStatusCode code)
            => new(code) { Content = new StringContent("{\"errno\":-1}", Encoding.UTF8, "application/json") };
    }
}
