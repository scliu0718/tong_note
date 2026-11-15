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

    /// <summary>用授权码换访问令牌。请求形式与 Mac 版一致，走 GET。</summary>
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
        var query = string.Join("&", parameters.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        var url = $"{BaiduPanConfig.TokenUrl}?{query}";

        var own = http == null;
        http ??= new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        try
        {
            using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            var body = await ReadTextAsync(response.Content, cancellationToken).ConfigureAwait(false);
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
        finally
        {
            if (own) http.Dispose();
        }
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
                    if (parsed != null) page.Add(parsed);
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
    /// 下载远端文件。先按 Mac 版语义走 xpan method=download 取 dlink；
    /// 但百度开放平台对第三方应用目录（含 /apps/tong_log 生产根）统一返回
    /// errno 20020「该路径不在允许的访问范围内」，此时降级为
    /// list 取 fs_id → filemetas?dlink=1 取直链。远端确实不存在时抛
    /// <see cref="BaiduPanNotFoundException"/>；直链瞬时失效会先重取一次直链重试，
    /// 避免把云端实际存在的文件误判成不存在。
    /// </summary>
    public Task<byte[]> DownloadDataAsync(string remotePath, string accessToken,
        CancellationToken cancellationToken = default)
        => DownloadDataAsync(remotePath, accessToken, 0, cancellationToken);

    private async Task<byte[]> DownloadDataAsync(string remotePath, string accessToken,
        int attempt, CancellationToken cancellationToken)
    {
        var metaUrl = XpanFile(new Dictionary<string, string>
        {
            ["method"] = "download",
            ["access_token"] = accessToken,
            ["path"] = remotePath,
            ["threshold"] = "0",
        });
        using var metaResponse = await GetAsync(metaUrl, cancellationToken).ConfigureAwait(false);
        var metaBody = await ReadTextAsync(metaResponse.Content, cancellationToken).ConfigureAwait(false);
        if (!TryParseJson(metaBody, out var metaDoc))
            throw new BaiduPanException("查询云端文件失败" + Snippet(metaBody));

        string? dlink;
        using (metaDoc)
        {
            var root = metaDoc.RootElement;
            var errno = Int64Or(root, "errno") ?? -1;
            dlink = null;
            if (errno == 0 && root.TryGetProperty("list", out var list)
                           && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0)
                dlink = StringOr(list[0], "dlink");
        }

        if (string.IsNullOrWhiteSpace(dlink))
            dlink = await DlinkFromFilemetasAsync(remotePath, accessToken, cancellationToken)
                .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(dlink))
            throw new BaiduPanNotFoundException(remotePath);

        var separator = dlink.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        var downloadUrl = dlink + separator + "access_token=" + Uri.EscapeDataString(accessToken);
        try
        {
            using var response = await GetAsync(downloadUrl, cancellationToken, withUserAgent: true)
                .ConfigureAwait(false);
            var data = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new BaiduPanException(
                    $"下载文件失败（HTTP {(int)response.StatusCode}）{Snippet(Encoding.UTF8.GetString(data))}");
            return data;
        }
        catch (BaiduPanException ex) when (IsRemoteFileGone(ex))
        {
            // 直链偶发返回 404 + errno 31066（dlink 失效或文件已被别的端删除）。
            // 先重取一次直链再试：否则「同步层把 NotFound 当作云端没有这篇」会把
            // 实际存在的笔记误判成缺失，进而走新增/覆盖分支。两次都 404 才判 NotFound。
            if (attempt == 0)
            {
                await Task.Delay(400, cancellationToken).ConfigureAwait(false);
                return await DownloadDataAsync(remotePath, accessToken, 1, cancellationToken)
                    .ConfigureAwait(false);
            }
            throw new BaiduPanNotFoundException(remotePath);
        }
    }

    private static bool IsRemoteFileGone(BaiduPanException ex)
        => ex is not BaiduPanNotFoundException
           && ex.Message.Contains("HTTP 404", StringComparison.Ordinal)
           && (ex.Message.Contains("31066", StringComparison.Ordinal)
               || ex.Message.Contains("file does not exist", StringComparison.OrdinalIgnoreCase));

    /// <summary>errno 20020 降级：按文件名在父目录找 fs_id，再用 filemetas 换 dlink。</summary>
    private async Task<string?> DlinkFromFilemetasAsync(string remotePath, string accessToken,
        CancellationToken cancellationToken)
    {
        var slash = remotePath.LastIndexOf('/');
        if (slash <= 0) return null;
        var parent = remotePath[..slash];
        var name = remotePath[(slash + 1)..];

        List<BaiduPanListEntry> entries;
        try
        {
            entries = await ListFilesAsync(parent, accessToken, cancellationToken).ConfigureAwait(false);
        }
        catch (BaiduPanNotFoundException)
        {
            return null;
        }

        var hit = entries.FirstOrDefault(item => item.IsDir == 0 && item.FsId.HasValue
            && string.Equals(item.Name, name, StringComparison.Ordinal));
        if (hit?.FsId == null) return null;

        var fsids = JsonSerializer.Serialize(new[] { hit.FsId.Value }, PlainOptions);
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
        var createErrno = Int64Or(createDoc.RootElement, "errno") ?? 0;
        if (createErrno != 0)
            throw new BaiduPanException($"create 失败（errno={createErrno}）{Snippet(createBody)}");
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
            throw new BaiduPanException(
                $"上传分片失败（HTTP {(int)response.StatusCode}）{Snippet(responseBody)}");
        if (TryParseJson(responseBody, out var doc))
        {
            using (doc)
            {
                var errno = Int64Or(doc.RootElement, "errno");
                if (errno != null && errno != 0)
                    throw new BaiduPanException($"上传分片失败（errno={errno}）{Snippet(responseBody)}");
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
            throw new BaiduPanException($"请求失败（HTTP {(int)response.StatusCode}）{Snippet(text)}");
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
            throw new BaiduPanException($"请求失败（HTTP {(int)response.StatusCode}）{Snippet(body)}");
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
