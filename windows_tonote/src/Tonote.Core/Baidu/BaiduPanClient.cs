using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tonote.Core;

/// <summary>
/// 百度网盘开放平台客户端。与 Mac 版 BaiduPanClient 逐条对齐：
/// 授权码换 token（GET）、refresh_token 续期、xpan 列举 / 下载、
/// precreate → superfile2 → create 三步分片上传（4MB，rtype=3 覆盖写）。
/// </summary>
public sealed class BaiduPanClient
{
    private const string XpanFileUrl = "https://pan.baidu.com/rest/2.0/xpan/file";
    private const string XpanApiUrl = "https://pan.baidu.com/rest/2.0/xpan/nas";
    // 实测 xpan/nas?method=quota 会返回 errno=2 Param error（Mac 版沿用旧地址同样失效），
    // 配额改用 /api/quota，checkfree / checkexpire 为必填参数。
    private const string QuotaUrl = "https://pan.baidu.com/api/quota";
    private const string SuperFileUrl = "https://c.pcs.baidu.com/rest/2.0/pcs/superfile2";
    private const string UserAgent = "pan.baidu.com";

    private readonly HttpClient _http;
    private readonly BaiduPanTokenStore _tokens;

    /// <summary>列目录 / create 时记下 路径 → fs_id，供下载走 filemetas 兜底，避免重复列目录。</summary>
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, long> _fsIdCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>百度对部分应用禁止 method=download&amp;path（errno 20020）。命中一次后本进程直接走 filemetas。</summary>
    private bool _downloadByPathBlocked;

    public BaiduPanClient(string root, HttpClient? http = null)
    {
        _tokens = new BaiduPanTokenStore(root);
        // 单个请求最多 90 秒：代理隧道挂起时若沿用 10 分钟默认值，
        // 一轮同步会被一个卡住的请求整体拖死（真实网盘实测 9MB 三分片仅需 3.7s）。
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
    }

    public BaiduPanTokenStore Tokens => _tokens;

    public BaiduPanToken? CurrentToken => _tokens.Load();

    public void Logout() => _tokens.Clear();

    // MARK: - 授权（保持静态，登录页直接调用）

    /// <summary>用授权码换访问令牌。W-39：client_secret / code 走 POST body，不进 URL。</summary>
    public static async Task<BaiduPanToken> ExchangeCodeAsync(
        string code,
        HttpClient? http = null,
        CancellationToken cancellationToken = default)
    {
        if (!BaiduPanConfig.IsConfigured)
            throw new BaiduPanException("尚未配置 AppKey / AppSecret");

        var token = await RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = BaiduPanConfig.AppKey,
            ["client_secret"] = BaiduPanConfig.AppSecret,
            ["redirect_uri"] = BaiduPanConfig.RedirectUri,
        }, http, cancellationToken).ConfigureAwait(false);
        return token;
    }

    private static async Task<BaiduPanToken> RequestTokenAsync(
        Dictionary<string, string> parameters,
        HttpClient? http,
        CancellationToken cancellationToken)
    {
        var own = http == null;
        http ??= new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        try
        {
            // W-39：先 POST form body；仅当端点明确拒绝 POST 时才退回 GET（并记风险）。
            using var form = new FormUrlEncodedContent(parameters);
            using var response = await http.PostAsync(BaiduPanConfig.TokenUrl, form, cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed)
            {
                response.Dispose();
                var query = string.Join("&", parameters.Select(pair =>
                    $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
                var fallback = await http.GetAsync(BaiduPanConfig.TokenUrl + "?" + query, cancellationToken)
                    .ConfigureAwait(false);
                var fallbackBody = await ReadTextAsync(fallback.Content, cancellationToken).ConfigureAwait(false);
                return ParseToken(fallbackBody);
            }
            var body = await ReadTextAsync(response.Content, cancellationToken).ConfigureAwait(false);
            return ParseToken(body);
        }
        finally
        {
            if (own) http.Dispose();
        }
    }

    private static BaiduPanToken ParseToken(string body)
    {
        var parsed = TonoteJson.Deserialize<TokenResponse>(body)
            ?? throw new BaiduPanException("授权响应为空");
        if (!string.IsNullOrWhiteSpace(parsed.Error) || string.IsNullOrWhiteSpace(parsed.AccessToken))
        {
            throw new BaiduPanException(
                string.IsNullOrWhiteSpace(parsed.ErrorDescription)
                    ? parsed.Error ?? "授权失败"
                    : parsed.ErrorDescription);
        }
        return new BaiduPanToken
        {
            AccessToken = parsed.AccessToken,
            RefreshToken = parsed.RefreshToken ?? "",
            ExpiresIn = parsed.ExpiresIn,
            Scope = parsed.Scope,
            CreatedAt = DateTime.UtcNow,
        };
    }

    // MARK: - 令牌续期

    public async Task<BaiduPanToken> RefreshAsync(BaiduPanToken token,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token.RefreshToken))
            throw new BaiduPanException("本地无有效令牌，请重新登录授权");

        var fresh = await RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = token.RefreshToken,
            ["client_id"] = BaiduPanConfig.AppKey,
            ["client_secret"] = BaiduPanConfig.AppSecret,
        }, null, cancellationToken).ConfigureAwait(false);

        // 百度刷新时若未下发新 refresh_token，沿用旧值
        if (string.IsNullOrWhiteSpace(fresh.RefreshToken))
            fresh.RefreshToken = token.RefreshToken;
        if (string.IsNullOrWhiteSpace(fresh.Scope))
            fresh.Scope = token.Scope;
        _tokens.Save(fresh);
        return fresh;
    }

    /// <summary>获取有效令牌（提前 5 分钟过期则自动续期）。</summary>
    public async Task<BaiduPanToken> ValidTokenAsync(CancellationToken cancellationToken = default)
    {
        var token = CurrentToken ?? throw new BaiduPanException("本地无有效令牌，请重新登录授权");
        return token.IsExpired
            ? await RefreshAsync(token, cancellationToken).ConfigureAwait(false)
            : token;
    }

    // MARK: - 基础信息

    public async Task<BaiduPanUserInfo> UserInfoAsync(string accessToken,
        CancellationToken cancellationToken = default)
    {
        var url = XpanApi("uinfo", accessToken);
        using var response = await GetAsync(url, cancellationToken).ConfigureAwait(false);
        var body = await ReadTextAsync(response.Content, cancellationToken).ConfigureAwait(false);
        EnsureNoErrno(body, "获取用户信息失败");
        return JsonSerializer.Deserialize<BaiduPanUserInfo>(body, PlainOptions)
               ?? throw new BaiduPanException("百度网盘返回数据异常");
    }

    public async Task<BaiduPanQuota> QuotaAsync(string accessToken,
        CancellationToken cancellationToken = default)
    {
        var url = QuotaUrl + "?" + string.Join("&", new Dictionary<string, string>
        {
            ["access_token"] = accessToken,
            ["checkfree"] = "1",
            ["checkexpire"] = "1",
        }.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        using var response = await GetAsync(url, cancellationToken).ConfigureAwait(false);
        var body = await ReadTextAsync(response.Content, cancellationToken).ConfigureAwait(false);
        EnsureNoErrno(body, "获取网盘配额失败");
        return JsonSerializer.Deserialize<BaiduPanQuota>(body, PlainOptions)
               ?? throw new BaiduPanException("百度网盘返回数据异常");
    }

    /// <summary>百度部分接口用 errno 表达业务失败，HTTP 仍是 200，必须单独判。</summary>
    private static void EnsureNoErrno(string body, string prefix)
    {
        if (!TryParseJson(body, out var doc)) return;
        using (doc)
        {
            var errno = Int64Or(doc.RootElement, "errno");
            if (errno != null && errno != 0)
                throw new BaiduPanException($"{prefix}（errno={errno}）{Snippet(body)}");
        }
    }

    // MARK: - 目录列举 / 删除

    /// <summary>列出目录下的直接子项，分页拉取；目录不存在返回空列表语义由 notFound 表达。</summary>
    public async Task<List<BaiduPanListEntry>> ListFilesAsync(string directory, string accessToken,
        CancellationToken cancellationToken = default)
    {
        var all = new List<BaiduPanListEntry>();
        const int limit = 200;
        for (var start = 0; start < 5000; start += limit)
        {
            var url = XpanFile(new Dictionary<string, string>
            {
                ["method"] = "list",
                ["dir"] = directory,
                ["start"] = start.ToString(CultureInfo.InvariantCulture),
                ["limit"] = limit.ToString(CultureInfo.InvariantCulture),
                ["order"] = "name",
                ["access_token"] = accessToken,
            });
            using var response = await GetAsync(url, cancellationToken, withUserAgent: true).ConfigureAwait(false);
            var body = await ReadTextAsync(response.Content, cancellationToken).ConfigureAwait(false);
            using var doc = ParseJson(body, "列出目录失败");
            var root = doc.RootElement;
            var errno = Int64Or(root, "errno") ?? -1;
            if (errno == -9 || errno == 31066)
                throw new BaiduPanNotFoundException(directory);
            if (errno != 0)
                throw new BaiduPanException($"列出目录失败（errno={errno}）{Snippet(body)}");

            var page = new List<BaiduPanListEntry>();
            if (root.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    var parsed = JsonSerializer.Deserialize<BaiduPanListEntry>(item.GetRawText(), PlainOptions);
                    if (parsed == null) continue;
                    page.Add(parsed);
                    if (parsed.FsId.HasValue && !string.IsNullOrWhiteSpace(parsed.Path))
                        CacheFsId(parsed.FsId.Value, parsed.Path!);
                }
            }
            all.AddRange(page);
            if (page.Count < limit) break;
        }
        return all;
    }

    public async Task DeleteRemoteAsync(string path, string accessToken,
        CancellationToken cancellationToken = default)
    {
        var filelist = JsonSerializer.Serialize(new[] { path }, PlainOptions);
        var body = await PostFormAsync(XpanFile(new Dictionary<string, string>
        {
            ["method"] = "filemanager",
            ["opera"] = "delete",
        }), accessToken, new Dictionary<string, string>
        {
            ["async"] = "0",
            ["filelist"] = filelist,
        }, cancellationToken).ConfigureAwait(false);
        using var doc = ParseJson(body, "删除远端文件失败");
        var errno = Int64Or(doc.RootElement, "errno");
        // -9 / 31066 表示文件本就不存在，删除视为成功
        if (errno != null && errno != 0 && errno != -9 && errno != 31066)
            throw new BaiduPanException($"删除远端文件失败（errno={errno}）{Snippet(body)}");
    }

    /// <summary>确保远端应用目录存在（失败不致命，上传时会自动创建父目录）。</summary>
    public async Task EnsureRemoteDirAsync(string accessToken,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = XpanFile(new Dictionary<string, string>
            {
                ["method"] = "filemanager",
                ["opera"] = "create",
                ["access_token"] = accessToken,
            });
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["newpath"] = BaiduPanConfig.RemoteRoot,
            });
            using var response = await _http.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Mac 版同样吞掉这个错误
        }
    }

    // MARK: - 下载

    /// <summary>
    /// 下载远端文件。取直链顺序：先试 method=download&amp;path，被 errno 20020 拒绝后改走
    /// filemetas&amp;fsids&amp;dlink=1（先列父目录拿 fs_id）。三次重试 + 300ms×attempt 退避，
    /// 每次重新取链；直链 GET 偶发 404 也在重试范围内。
    /// 本方法只抛临时错误（<see cref="BaiduPanTransientException"/>），绝不抛 notFound——
    /// 文件是否存在由调用方依据「目录列表 + 是否同步过」判定，避免网络抖动 / 风控被当成
    /// 「云端无文件」而用本地旧版反向覆盖。
    /// </summary>
    public async Task<byte[]> DownloadDataAsync(string remotePath, string accessToken,
        CancellationToken cancellationToken = default)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(attempt * 300, cancellationToken).ConfigureAwait(false);
            var link = await ResolveDlinkAsync(remotePath, accessToken, attempt > 0, cancellationToken)
                .ConfigureAwait(false);
            if (link == null)
            {
                lastError = new BaiduPanTransientException("获取下载直链失败：" + remotePath);
                continue;
            }

            try
            {
                return await FetchDlinkAsync(link, accessToken, remotePath, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw lastError ?? new BaiduPanTransientException("下载失败：" + remotePath);
    }

    /// <summary>远端文件是否存在。目录不存在或列表里没有该文件返回 false；
    /// 列表失败（网络异常等）会抛错——绝不把「查不到」误判成「不存在」。</summary>
    public async Task<bool> RemoteFileExistsAsync(string remotePath, string accessToken,
        CancellationToken cancellationToken = default)
    {
        var slash = remotePath.LastIndexOf('/');
        var parent = slash <= 0 ? "/" : remotePath[..slash];
        List<BaiduPanListEntry> entries;
        try
        {
            entries = await ListFilesAsync(parent, accessToken, cancellationToken).ConfigureAwait(false);
        }
        catch (BaiduPanNotFoundException)
        {
            return false;
        }

        return entries.Any(entry =>
            string.Equals(entry.Path ?? parent + "/" + entry.Name, remotePath,
                StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>取下载直链。首试 method=download&amp;path，被拒后走 filemetas&amp;fsids&amp;dlink=1；
    /// 拿不到时再稍候强刷父目录重拿 fs_id（列表有延迟、filemetas 偶发漏项）。返回 null 表示临时失败。</summary>
    private async Task<string?> ResolveDlinkAsync(string remotePath, string accessToken, bool refresh,
        CancellationToken cancellationToken)
    {
        if (!refresh && !_downloadByPathBlocked)
        {
            var direct = await DlinkByPathAsync(remotePath, accessToken, cancellationToken)
                .ConfigureAwait(false);
            if (direct.blocked) _downloadByPathBlocked = true;
            if (direct.dlink != null) return direct.dlink;
        }

        long? fsId;
        if (refresh)
            fsId = await FindFsIdAsync(remotePath, accessToken, cancellationToken).ConfigureAwait(false);
        else if (CachedFsId(remotePath) is { } cached)
            fsId = cached;
        else
            fsId = await FindFsIdAsync(remotePath, accessToken, cancellationToken).ConfigureAwait(false);

        if (fsId != null)
        {
            var d = await DlinkByFsIdAsync(fsId.Value, accessToken, cancellationToken)
                .ConfigureAwait(false);
            if (d != null) return d;
        }

        await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        fsId = await FindFsIdAsync(remotePath, accessToken, cancellationToken).ConfigureAwait(false);
        if (fsId != null)
        {
            var d = await DlinkByFsIdAsync(fsId.Value, accessToken, cancellationToken)
                .ConfigureAwait(false);
            if (d != null) return d;
        }

        return null;
    }

    /// <summary>method=download&amp;path 取直链。返回 (dlink, 是否被拒)。被 errno 20020 拒绝后本进程改走 filemetas。
    /// 网络/解析失败一律吞掉返回 (null, false)，交给上层 3 次重试，绝不把抖动当业务失败。</summary>
    private async Task<(string? dlink, bool blocked)> DlinkByPathAsync(string remotePath, string accessToken,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = XpanFile(new Dictionary<string, string>
            {
                ["method"] = "download",
                ["access_token"] = accessToken,
                ["path"] = remotePath,
                ["threshold"] = "0",
            });
            using var response = await GetAsync(url, cancellationToken).ConfigureAwait(false);
            var body = await ReadTextAsync(response.Content, cancellationToken).ConfigureAwait(false);
            if (!TryParseJson(body, out var doc))
                return (null, false);
            using (doc)
            {
                var root = doc.RootElement;
                var errno = Int64Or(root, "errno") ?? -1;
                string? dlink = null;
                if (errno == 0 && root.TryGetProperty("list", out var list)
                               && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0)
                    dlink = StringOr(list[0], "dlink");
                if (!string.IsNullOrWhiteSpace(dlink))
                    return (dlink, false);
                return (null, errno != 0);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return (null, false);
        }
    }

    /// <summary>列父目录，按 path 精确匹配拿 fs_id（filemetas 只认 fsids 不认 path）。拿不到 / 列表失败返回 null。</summary>
    private async Task<long?> FindFsIdAsync(string remotePath, string accessToken,
        CancellationToken cancellationToken)
    {
        var slash = remotePath.LastIndexOf('/');
        if (slash <= 0) return null;
        var parent = remotePath[..slash];

        List<BaiduPanListEntry> entries;
        try
        {
            entries = await ListFilesAsync(parent, accessToken, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }

        foreach (var item in entries)
        {
            var path = item.Path ?? parent + "/" + item.Name;
            if (item.FsId.HasValue && !string.IsNullOrWhiteSpace(item.Path))
                CacheFsId(item.FsId.Value, item.Path!);
            if (string.Equals(path, remotePath, StringComparison.OrdinalIgnoreCase) && item.FsId.HasValue)
                return item.FsId.Value;
        }

        return null;
    }

    /// <summary>method=filemetas&amp;fsids=[id]&amp;dlink=1 → 下载直链。返回体是 { info: [ { dlink } ] }。
    /// 网络/解析失败一律吞掉返回 null，交给上层 3 次重试。</summary>
    private async Task<string?> DlinkByFsIdAsync(long fsId, string accessToken,
        CancellationToken cancellationToken)
    {
        try
        {
            var fsids = JsonSerializer.Serialize(new[] { fsId }, PlainOptions);
            var url = XpanFile(new Dictionary<string, string>
            {
                ["method"] = "filemetas",
                ["access_token"] = accessToken,
                ["fsids"] = fsids,
                ["dlink"] = "1",
            });
            using var response = await GetAsync(url, cancellationToken, withUserAgent: true).ConfigureAwait(false);
            var body = await ReadTextAsync(response.Content, cancellationToken).ConfigureAwait(false);
            if (!TryParseJson(body, out var doc)) return null;
            using (doc)
            {
                var root = doc.RootElement;
                if ((Int64Or(root, "errno") ?? -1) != 0) return null;
                if (!root.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Array
                                                              || info.GetArrayLength() == 0)
                    return null;
                return StringOr(info[0], "dlink");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>从直链拉取文件体（附 access_token 与 User-Agent，否则会被 403 拒绝）。</summary>
    private async Task<byte[]> FetchDlinkAsync(string link, string accessToken, string remotePath,
        CancellationToken cancellationToken)
    {
        var separator = link.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        var downloadUrl = link + separator + "access_token=" + Uri.EscapeDataString(accessToken);
        using var response = await GetAsync(downloadUrl, cancellationToken, withUserAgent: true)
            .ConfigureAwait(false);
        var data = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (data.Length == 0)
            throw new BaiduPanTransientException("下载内容为空：" + remotePath);
        return data;
    }

    private long? CachedFsId(string path)
    {
        lock (_cacheLock)
            return _fsIdCache.TryGetValue(path, out var value) ? value : null;
    }

    private void CacheFsId(long fsId, string path)
    {
        lock (_cacheLock)
            _fsIdCache[path] = fsId;
    }

    // MARK: - 上传（precreate → superfile2 → create）

    public async Task UploadDataAsync(byte[] data, string remotePath, string accessToken,
        CancellationToken cancellationToken = default)
    {
        var slices = UploadChunks.Slices(data);
        var md5List = slices.Select(slice =>
            Convert.ToHexString(MD5.HashData(slice)).ToLowerInvariant()).ToList();
        // block_list 是 JSON 字符串，需要整体百分号编码一次；其它表单字段按常规编码
        var blockJson = "[" + string.Join(",", md5List.Select(value => "\"" + value + "\"")) + "]";
        var blockList = Uri.EscapeDataString(blockJson);

        // 1) precreate：申请 uploadid，自动创建父目录
        var preBody = await PostFormAsync(XpanFile(new Dictionary<string, string>
        {
            ["method"] = "precreate",
        }), accessToken, new Dictionary<string, string>
        {
            ["path"] = remotePath,
            ["size"] = data.Length.ToString(CultureInfo.InvariantCulture),
            ["isdir"] = "0",
            ["autoinit"] = "1",
            ["block_list"] = blockList,
            ["rtype"] = "3",
        }, cancellationToken, preEncodedKeys: ["block_list"]).ConfigureAwait(false);

        using var preDoc = ParseJson(preBody, "precreate 失败");
        var pre = preDoc.RootElement;
        var preErrno = Int64Or(pre, "errno") ?? 0;
        var uploadId = FlexibleString(pre, "uploadid");
        var returnType = Int64Or(pre, "return_type") ?? 1;
        var requiredBlocks = IntList(pre, "block_list");
        if (string.IsNullOrWhiteSpace(uploadId) || preErrno != 0)
            throw new BaiduPanException($"precreate 失败（errno={preErrno}）{Snippet(preBody)}");

        // return_type == 2 表示云端已有相同分片（秒传），无需再上传分片
        if (returnType != 2)
        {
            var indexes = requiredBlocks.Count > 0
                ? requiredBlocks
                : Enumerable.Range(0, slices.Count).ToList();
            foreach (var index in indexes.Where(index => index >= 0 && index < slices.Count))
            {
                await UploadBlockAsync(slices[index], remotePath, uploadId!, index, accessToken,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        // 3) create：提交分片，完成文件（rtype=3 覆盖同名）
        var createBody = await PostFormAsync(XpanFile(new Dictionary<string, string>
        {
            ["method"] = "create",
        }), accessToken, new Dictionary<string, string>
        {
            ["path"] = remotePath,
            ["size"] = data.Length.ToString(CultureInfo.InvariantCulture),
            ["isdir"] = "0",
            ["uploadid"] = uploadId!,
            ["block_list"] = blockList,
            ["rtype"] = "3",
        }, cancellationToken, preEncodedKeys: ["block_list"]).ConfigureAwait(false);

        using var createDoc = ParseJson(createBody, "create 返回异常");
        var createRoot = createDoc.RootElement;
        var createErrno = Int64Or(createRoot, "errno") ?? 0;
        if (createErrno != 0)
            throw new BaiduPanException($"create 失败（errno={createErrno}）{Snippet(createBody)}");
        // create 返回 fs_id，缓存后 publish 的回读校验能立刻走 filemetas，不必等云端列表刷新
        var createdFsId = Int64Or(createRoot, "fs_id");
        if (createdFsId.HasValue && createdFsId.Value > 0)
            CacheFsId(createdFsId.Value, remotePath);
    }

    private async Task UploadBlockAsync(byte[] data, string remotePath, string uploadId,
        int partseq, string accessToken, CancellationToken cancellationToken)
    {
        var url = SuperFileUrl + "?" + string.Join("&", new Dictionary<string, string>
        {
            ["method"] = "upload",
            ["type"] = "tmpfile",
            ["path"] = remotePath,
            ["uploadid"] = uploadId,
            ["partseq"] = partseq.ToString(CultureInfo.InvariantCulture),
            ["access_token"] = accessToken,
        }.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

        var boundary = "----TonoteBoundary" + Guid.NewGuid().ToString("N");
        using var body = new MultipartFormDataContent(boundary);
        using var file = new ByteArrayContent(data);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        body.Add(file, "file", "blob");

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = body };
        request.Headers.UserAgent.ParseAdd(UserAgent);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseBody = await ReadTextAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new BaiduPanTransientException(
                $"上传分片失败（HTTP {(int)response.StatusCode}）{Snippet(responseBody)}");
        if (TryParseJson(responseBody, out var doc))
        {
            using (doc)
            {
                var errno = Int64Or(doc.RootElement, "errno");
                if (errno != null && errno != 0)
                    throw new BaiduPanTransientException($"上传分片失败（errno={errno}）{Snippet(responseBody)}");
            }
        }
    }

    // MARK: - 私有工具

    private static readonly JsonSerializerOptions PlainOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private static string XpanFile(Dictionary<string, string> query)
        => XpanFileUrl + "?" + string.Join("&", query.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

    private static string XpanApi(string method, string accessToken)
        => XpanApiUrl + "?method=" + Uri.EscapeDataString(method)
           + "&access_token=" + Uri.EscapeDataString(accessToken);

    private async Task<HttpResponseMessage> GetAsync(string url, CancellationToken cancellationToken,
        bool withUserAgent = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (withUserAgent) request.Headers.UserAgent.ParseAdd(UserAgent);
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var text = await ReadTextAsync(response.Content, cancellationToken).ConfigureAwait(false);
            response.Dispose();
            throw new BaiduPanTransientException($"请求失败（HTTP {(int)response.StatusCode}）{Snippet(text)}");
        }
        return response;
    }

    private async Task<string> PostFormAsync(string baseUrl, string accessToken,
        Dictionary<string, string> form, CancellationToken cancellationToken,
        string[]? preEncodedKeys = null)
    {
        var separator = baseUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        var url = baseUrl + separator + "access_token=" + Uri.EscapeDataString(accessToken);
        var encoded = form.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}=" +
            (preEncodedKeys?.Contains(pair.Key, StringComparer.Ordinal) == true
                ? pair.Value
                : Uri.EscapeDataString(pair.Value)));
        using var content = new StringContent(string.Join("&", encoded), Encoding.UTF8,
            "application/x-www-form-urlencoded");
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.UserAgent.ParseAdd(UserAgent);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await ReadTextAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new BaiduPanTransientException($"请求失败（HTTP {(int)response.StatusCode}）{Snippet(body)}");
        return body;
    }

    /// <summary>
    /// 百度部分接口（尤其 precreate / superfile2）会在 Content-Type 里带回未在 .NET 注册的字符集，
    /// ReadAsStringAsync 会直接抛 InvalidOperationException。统一按字节读取再按 UTF-8 解码，
    /// 与 Mac 版 URLSession 直接取 Data 的行为一致。
    /// </summary>
    private static async Task<string> ReadTextAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var bytes = await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }

    private static JsonDocument ParseJson(string body, string prefix)
    {
        if (!TryParseJson(body, out var doc))
            throw new BaiduPanException(prefix + Snippet(body));
        return doc;
    }

    private static bool TryParseJson(string body, out JsonDocument doc)
    {
        try
        {
            doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            doc = null!;
            return false;
        }
    }

    private static string Snippet(string? body)
        => string.IsNullOrWhiteSpace(body)
            ? ""
            : " · 响应：" + (body.Length <= 300 ? body : body[..300]);

    private static long? Int64Or(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String &&
            long.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return null;
    }

    private static string? StringOr(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>uploadid 有时是字符串、有时是数字。</summary>
    private static string? FlexibleString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static List<int> IntList(JsonElement element, string name)
    {
        var result = new List<int>();
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var number))
                result.Add(number);
            else if (item.ValueKind == JsonValueKind.String &&
                     int.TryParse(item.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                result.Add(parsed);
        }
        return result;
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("scope")]
        public string? Scope { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("error_description")]
        public string? ErrorDescription { get; set; }
    }
}
