import Foundation
import CommonCrypto

public enum BaiduPanError: LocalizedError {
    case notConfigured
    case invalidResponse
    case server(message: String)
    case noToken
    case notFound

    public var errorDescription: String? {
        switch self {
        case .notConfigured:
            return "尚未配置百度网盘 AppKey / AppSecret，请先在 BaiduPanConfig 中填写"
        case .invalidResponse:
            return "百度网盘返回数据异常"
        case .server(let m):
            return "百度网盘接口错误：\(m)"
        case .noToken:
            return "本地无有效令牌，请重新登录授权"
        case .notFound:
            return "远端文件不存在"
        }
    }
}

/// 百度网盘开放平台客户端：授权、令牌交换/续期、基础信息查询。
public final class BaiduPanClient {
    public static let shared = BaiduPanClient()

    public let tokenStore = BaiduPanTokenStore()
    private let session = URLSession.shared

    /// 列目录时顺手记下 路径 → fs_id，供下载走 filemetas 兜底，避免重复列目录。
    private let cacheLock = NSLock()
    private var fsIdCache: [String: Int64] = [:]

    /// 百度对部分应用禁止 xpan/file?method=download&path=（errno 20020
    /// 「该路径不在允许的访问范围内」）。命中一次后本进程直接走 filemetas，
    /// 不再每篇笔记都先发一个必然失败的请求。
    private var downloadByPathBlocked = false

    public init() {}

    public var currentToken: BaiduPanToken? { tokenStore.load() }

    // MARK: 授权

    /// 构造授权页 URL（授权码模式）。
    /// - Parameter display: 授权页展示方式：`popup`（内嵌弹窗）/ `page`（完整网页，含扫码登录）。
    public func authorizationURL(state: String, display: String = "popup") -> URL {
        var comps = URLComponents(url: BaiduPanConfig.authorizeURL, resolvingAgainstBaseURL: false)!
        comps.queryItems = [
            URLQueryItem(name: "response_type", value: "code"),
            URLQueryItem(name: "client_id", value: BaiduPanConfig.appKey),
            URLQueryItem(name: "redirect_uri", value: BaiduPanConfig.redirectURI),
            URLQueryItem(name: "scope", value: BaiduPanConfig.scope),
            URLQueryItem(name: "display", value: display),
            URLQueryItem(name: "state", value: state),
        ]
        return comps.url!
    }

    // MARK: 令牌

    /// 用授权码换取令牌并落盘。
    public func exchangeCode(_ code: String) async throws -> BaiduPanToken {
        var comps = URLComponents(url: BaiduPanConfig.tokenURL, resolvingAgainstBaseURL: false)!
        comps.queryItems = [
            URLQueryItem(name: "grant_type", value: "authorization_code"),
            URLQueryItem(name: "code", value: code),
            URLQueryItem(name: "client_id", value: BaiduPanConfig.appKey),
            URLQueryItem(name: "client_secret", value: BaiduPanConfig.appSecret),
            URLQueryItem(name: "redirect_uri", value: BaiduPanConfig.redirectURI),
        ]
        let raw = try await get(comps.url!)
        let resp = try JSONDecoder().decode(BaiduPanTokenResponse.self, from: raw)
        if let err = resp.error {
            throw BaiduPanError.server(message: resp.error_description ?? err)
        }
        let token = BaiduPanToken(accessToken: resp.access_token,
                                  refreshToken: resp.refresh_token,
                                  expiresIn: resp.expires_in,
                                  scope: resp.scope,
                                  createdAt: Date())
        tokenStore.save(token)
        return token
    }

    /// 用 refresh_token 续期并落盘。
    public func refresh(_ token: BaiduPanToken) async throws -> BaiduPanToken {
        guard !token.refreshToken.isEmpty else { throw BaiduPanError.noToken }
        var comps = URLComponents(url: BaiduPanConfig.tokenURL, resolvingAgainstBaseURL: false)!
        comps.queryItems = [
            URLQueryItem(name: "grant_type", value: "refresh_token"),
            URLQueryItem(name: "refresh_token", value: token.refreshToken),
            URLQueryItem(name: "client_id", value: BaiduPanConfig.appKey),
            URLQueryItem(name: "client_secret", value: BaiduPanConfig.appSecret),
        ]
        let raw = try await get(comps.url!)
        let resp = try JSONDecoder().decode(BaiduPanTokenResponse.self, from: raw)
        if let err = resp.error {
            throw BaiduPanError.server(message: resp.error_description ?? err)
        }
        // 百度刷新时若未下发新 refresh_token，沿用旧值
        let newRefresh = resp.refresh_token.isEmpty ? token.refreshToken : resp.refresh_token
        let newToken = BaiduPanToken(accessToken: resp.access_token,
                                     refreshToken: newRefresh,
                                     expiresIn: resp.expires_in,
                                     scope: resp.scope ?? token.scope,
                                     createdAt: Date())
        tokenStore.save(newToken)
        return newToken
    }

    /// 获取有效令牌（过期则自动续期）。
    public func validToken() async throws -> BaiduPanToken {
        guard let token = currentToken else { throw BaiduPanError.noToken }
        if token.isExpired {
            return try await refresh(token)
        }
        return token
    }

    // MARK: 基础信息

    /// 网盘用户信息。
    public func userInfo(accessToken: String) async throws -> BaiduPanUserInfo {
        let url = xpanURL(method: "uinfo", accessToken: accessToken)
        let (data, _) = try await session.data(from: url)
        return try JSONDecoder().decode(BaiduPanUserInfo.self, from: data)
    }

    /// 网盘配额。
    public func quota(accessToken: String) async throws -> BaiduPanQuota {
        let url = xpanURL(method: "quota", accessToken: accessToken)
        let (data, _) = try await session.data(from: url)
        return try JSONDecoder().decode(BaiduPanQuota.self, from: data)
    }

    // MARK: 退出

    public func logout() {
        tokenStore.clear()
    }

    /// 列出目录下的直接子项。目录不存在时抛 `notFound`。
    public func listFiles(directory: String, accessToken: String) async throws -> [BaiduPanListEntry] {
        var all: [BaiduPanListEntry] = []
        var start = 0
        let limit = 200
        while start < 5000 {
            let page = try await listFilePage(directory: directory,
                                              accessToken: accessToken,
                                              start: start,
                                              limit: limit)
            all.append(contentsOf: page)
            if page.count < limit { break }
            start += limit
        }
        return all
    }

    public func deleteRemote(path: String, accessToken: String) async throws {
        let listData = try JSONSerialization.data(withJSONObject: [path])
        let filelist = String(data: listData, encoding: .utf8) ?? "[]"
        let created = try await postForm(
            "https://pan.baidu.com/rest/2.0/xpan/file",
            accessToken: accessToken,
            query: [
                URLQueryItem(name: "method", value: "filemanager"),
                URLQueryItem(name: "opera", value: "delete"),
            ],
            form: [
                "async": "0",
                "filelist": filelist,
            ]
        )
        if let obj = try? JSONSerialization.jsonObject(with: created) as? [String: Any],
           let errno = obj["errno"] as? Int, errno != 0, errno != -9, errno != 31066 {
            throw BaiduPanError.server(message: "删除远端文件失败（errno=\(errno)）")
        }
    }

    private func listFilePage(directory: String, accessToken: String,
                              start: Int, limit: Int) async throws -> [BaiduPanListEntry] {
        var comps = URLComponents(url: URL(string: "https://pan.baidu.com/rest/2.0/xpan/file")!,
                                  resolvingAgainstBaseURL: false)!
        comps.queryItems = [
            URLQueryItem(name: "method", value: "list"),
            URLQueryItem(name: "dir", value: directory),
            URLQueryItem(name: "start", value: "\(start)"),
            URLQueryItem(name: "limit", value: "\(limit)"),
            URLQueryItem(name: "order", value: "name"),
            URLQueryItem(name: "access_token", value: accessToken),
        ]
        var request = URLRequest(url: comps.url!)
        request.setValue("pan.baidu.com", forHTTPHeaderField: "User-Agent")
        let (data, _) = try await session.data(for: request)
        struct ListResp: Codable {
            let errno: Int?
            let list: [BaiduPanListEntry]?
        }
        guard let resp = try? JSONDecoder().decode(ListResp.self, from: data) else {
            throw BaiduPanError.server(message: "列出目录失败\(Self.bodySnippet(data))")
        }
        let errno = resp.errno ?? -1
        if errno == -9 || errno == 31066 {
            throw BaiduPanError.notFound
        }
        guard errno == 0 else {
            throw BaiduPanError.server(message: "列出目录失败（errno=\(errno)）\(Self.bodySnippet(data))")
        }
        let list = resp.list ?? []
        for entry in list {
            if let fsId = entry.fsId, let p = entry.path {
                cacheFsId(fsId, path: p)
            }
        }
        return list
    }

    // MARK: 文件读写（PCS 个人云存储）

    /// 确保远端应用目录存在（失败不致命，上传时会按需创建父目录）。
    public func ensureRemoteDir(accessToken: String) async {
        guard let url = URL(string: "https://pan.baidu.com/rest/2.0/xpan/file") else { return }
        var comps = URLComponents(url: url, resolvingAgainstBaseURL: false)!
        comps.queryItems = [
            URLQueryItem(name: "method", value: "filemanager"),
            URLQueryItem(name: "opera", value: "create"),
            URLQueryItem(name: "access_token", value: accessToken),
        ]
        var req = URLRequest(url: comps.url!)
        req.httpMethod = "POST"
        req.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        req.httpBody = "newpath=\(BaiduPanConfig.remoteRoot)".data(using: .utf8)
        _ = try? await session.data(for: req)
    }

    /// 下载远端文件，返回 Data。
    ///
    /// 百度对部分开放平台应用禁止 `method=download&path`（恒返回 errno 20020
    /// 「该路径不在允许的访问范围内」），因此取直链的顺序是：先试 download&path，
    /// 被拒后改用 `filemetas&fsids&dlink=1`（先列父目录拿 fs_id）。
    /// 三次重试 + 退避，每次重新取链；直链 GET 偶发 404 也在重试范围内。
    ///
    /// 注意：本方法只抛「临时错误」，**绝不抛 notFound**。文件是否存在由调用方依据
    /// 「目录列表 + 是否同步过」判断——绝不能用「列目录查不到」推断不存在（断网时同样查不到），
    /// 避免网络抖动/风控被当成「云端无文件」而用本地旧版反向覆盖。
    public func downloadData(_ remotePath: String, accessToken: String) async throws -> Data {
        var lastError: Error?
        for attempt in 0..<3 {
            if attempt > 0 {
                try? await Task.sleep(nanoseconds: UInt64(attempt) * 300_000_000)
            }
            guard let link = await resolveDlink(remotePath, accessToken: accessToken, refresh: attempt > 0) else {
                lastError = BaiduPanError.server(message: "获取下载直链失败：\(remotePath)")
                continue
            }
            do {
                return try await fetchDlink(link, accessToken: accessToken, path: remotePath)
            } catch {
                lastError = error
            }
        }
        throw lastError ?? BaiduPanError.server(message: "下载失败：\(remotePath)")
    }

    /// 远端文件是否存在。目录不存在或列表里没有该文件返回 false；
    /// 列表失败（网络异常等）会抛错——绝不把「查不到」误判成「不存在」。
    public func remoteFileExists(_ path: String, accessToken: String) async throws -> Bool {
        let dir = (path as NSString).deletingLastPathComponent
        let entries: [BaiduPanListEntry]
        do {
            entries = try await listFiles(directory: dir, accessToken: accessToken)
        } catch BaiduPanError.notFound {
            return false
        }
        return entries.contains { ($0.path ?? "").caseInsensitiveCompare(path) == .orderedSame }
    }

    /// 取下载直链。首试 method=download&path，被拒后走 filemetas&fsids&dlink=1；
    /// 拿不到时再稍候强刷父目录重拿 fs_id（列表有延迟、filemetas 偶发漏项）。返回 nil 表示临时失败。
    private func resolveDlink(_ path: String, accessToken: String, refresh: Bool) async -> String? {
        if !refresh && !downloadByPathBlocked {
            let direct = await dlinkByPath(path, accessToken: accessToken)
            if direct.blocked { downloadByPathBlocked = true }
            if let d = direct.dlink { return d }
        }
        var fsId: Int64?
        if refresh {
            fsId = await findFsId(path, accessToken: accessToken)
        } else if let cached = cachedFsId(path) {
            fsId = cached
        } else {
            fsId = await findFsId(path, accessToken: accessToken)
        }
        if let fsId, let d = await dlinkByFsId(fsId, accessToken: accessToken) { return d }
        try? await Task.sleep(nanoseconds: 250_000_000)
        fsId = await findFsId(path, accessToken: accessToken)
        if let fsId, let d = await dlinkByFsId(fsId, accessToken: accessToken) { return d }
        return nil
    }

    /// method=download&path 取直链。返回 (dlink, 是否被拒)。被 errno 20020 拒绝后本进程改走 filemetas。
    private func dlinkByPath(_ path: String, accessToken: String) async -> (dlink: String?, blocked: Bool) {
        var dc = URLComponents(url: URL(string: "https://pan.baidu.com/rest/2.0/xpan/file")!, resolvingAgainstBaseURL: false)!
        dc.queryItems = [
            URLQueryItem(name: "method", value: "download"),
            URLQueryItem(name: "access_token", value: accessToken),
            URLQueryItem(name: "path", value: path),
            URLQueryItem(name: "threshold", value: "0"),
        ]
        struct DlMeta: Codable {
            let errno: Int?
            let list: [DlItem]?
            struct DlItem: Codable { let dlink: String? }
        }
        guard let (metaData, _) = try? await session.data(from: dc.url!),
              let meta = try? JSONDecoder().decode(DlMeta.self, from: metaData) else {
            return (nil, false)
        }
        if let d = meta.list?.first?.dlink, !d.isEmpty {
            return (d, false)
        }
        return (nil, (meta.errno ?? 0) != 0)
    }

    /// 从直链拉取文件体（附 access_token 与 User-Agent，否则会被 403 拒绝）。
    private func fetchDlink(_ link: String, accessToken: String, path: String) async throws -> Data {
        guard var dl = URLComponents(string: link) else { throw BaiduPanError.invalidResponse }
        var q = dl.queryItems ?? []
        q.append(URLQueryItem(name: "access_token", value: accessToken))
        dl.queryItems = q
        var dlReq = URLRequest(url: dl.url!)
        dlReq.setValue("pan.baidu.com", forHTTPHeaderField: "User-Agent")
        let (fileData, resp) = try await session.data(for: dlReq)
        if let http = resp as? HTTPURLResponse, http.statusCode >= 400 {
            throw BaiduPanError.server(message: "下载文件失败（HTTP \(http.statusCode)）\(Self.bodySnippet(fileData))")
        }
        if fileData.isEmpty {
            throw BaiduPanError.server(message: "下载内容为空：\(path)")
        }
        return fileData
    }

    /// 列父目录，按 path 精确匹配拿 fs_id（filemetas 只认 fsids 不认 path）。拿不到/列表失败返回 nil。
    private func findFsId(_ path: String, accessToken: String) async -> Int64? {
        let dir = (path as NSString).deletingLastPathComponent
        guard !dir.isEmpty else { return nil }
        var start = 0
        while start < 5000 {
            let page: [BaiduPanListEntry]
            do {
                page = try await listFilePage(directory: dir, accessToken: accessToken, start: start, limit: 200)
            } catch {
                return nil
            }
            var hit: Int64? = nil
            for f in page {
                if let fsId = f.fsId, let p = f.path {
                    cacheFsId(fsId, path: p)
                    if p.caseInsensitiveCompare(path) == .orderedSame { hit = fsId }
                }
            }
            if let hit { return hit }
            if page.count < 200 { return nil }
            start += page.count
        }
        return nil
    }

    /// method=filemetas&fsids=[id]&dlink=1 → 下载直链。返回体是 { info: [ { dlink } ] }。
    private func dlinkByFsId(_ fsId: Int64, accessToken: String) async -> String? {
        var comps = URLComponents(url: URL(string: "https://pan.baidu.com/rest/2.0/xpan/file")!, resolvingAgainstBaseURL: false)!
        comps.queryItems = [
            URLQueryItem(name: "method", value: "filemetas"),
            URLQueryItem(name: "access_token", value: accessToken),
            URLQueryItem(name: "fsids", value: "[\(fsId)]"),
            URLQueryItem(name: "dlink", value: "1"),
        ]
        var req = URLRequest(url: comps.url!)
        req.setValue("pan.baidu.com", forHTTPHeaderField: "User-Agent")
        guard let (data, _) = try? await session.data(for: req),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let errno = obj["errno"] as? Int, errno == 0,
              let info = obj["info"] as? [[String: Any]],
              let first = info.first,
              let dlink = first["dlink"] as? String, !dlink.isEmpty else {
            return nil
        }
        return dlink
    }

    /// 云端文件名大小写可能与本地 id 不一致，查缓存时忽略大小写。
    private func cachedFsId(_ path: String) -> Int64? {
        cacheLock.lock(); defer { cacheLock.unlock() }
        if let v = fsIdCache[path] { return v }
        return fsIdCache.first { $0.key.caseInsensitiveCompare(path) == .orderedSame }?.value
    }

    private func cacheFsId(_ fsId: Int64, path: String) {
        cacheLock.lock(); defer { cacheLock.unlock() }
        fsIdCache[path] = fsId
    }

    /// 上传文件到远端（覆盖写）。开放平台标准三步：precreate → 上传分片 → create。
    public func uploadData(_ data: Data, to remotePath: String, accessToken: String) async throws {
        let slices = UploadChunks.slices(data)
        let md5List = slices.map(\.md5Hex)
        let blockJSON = "[" + md5List.map { "\"\($0)\"" }.joined(separator: ",") + "]"
        let blockList = blockJSON.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? blockJSON

        // 1) precreate：申请 uploadid，自动创建父目录
        let precreate = try await postForm(
            "https://pan.baidu.com/rest/2.0/xpan/file",
            accessToken: accessToken,
            query: [URLQueryItem(name: "method", value: "precreate")],
            form: [
                "path": remotePath,
                "size": "\(data.count)",
                "isdir": "0",
                "autoinit": "1",
                "block_list": blockList,
                "rtype": "3",
            ]
        )
        struct PreResp: Decodable {
            let uploadid: String?
            let returnType: Int?
            let blockList: [Int]?
            let errno: Int?

            enum CodingKeys: String, CodingKey {
                case uploadid
                case returnType = "return_type"
                case blockList = "block_list"
                case errno
            }

            init(from decoder: Decoder) throws {
                let c = try decoder.container(keyedBy: CodingKeys.self)
                // uploadid 百度有时返回字符串、有时是数字，做兼容
                if let s = try? c.decode(String.self, forKey: .uploadid) {
                    uploadid = s
                } else if let i = try? c.decode(Int64.self, forKey: .uploadid) {
                    uploadid = String(i)
                } else {
                    uploadid = nil
                }
                returnType = try? c.decode(Int.self, forKey: .returnType)
                if let numbers = try? c.decode([Int].self, forKey: .blockList) {
                    blockList = numbers
                } else if let strings = try? c.decode([String].self, forKey: .blockList) {
                    blockList = strings.compactMap(Int.init)
                } else {
                    blockList = nil
                }
                errno = try? c.decode(Int.self, forKey: .errno)
            }
        }

        let decodePre = { try? JSONDecoder().decode(PreResp.self, from: precreate) }
        // 成功响应里 errno 可能缺省；只要拿到 uploadid 且 errno 未明确报错即视为成功
        guard let pre = decodePre(), let uploadID = pre.uploadid, (pre.errno ?? 0) == 0 else {
            let errno = decodePre()?.errno ?? -1
            throw BaiduPanError.server(message: "precreate 失败（errno=\(errno)）\(Self.bodySnippet(precreate))")
        }

        // return_type == 2 表示云端已有相同分片（秒传），无需再上传分片
        let needBlockUpload = (pre.returnType ?? 1) != 2
        if needBlockUpload {
            let indexes = (pre.blockList?.isEmpty == false) ? (pre.blockList ?? []) : Array(slices.indices)
            for index in indexes where slices.indices.contains(index) {
                try await uploadBlock(slices[index],
                                      remotePath: remotePath,
                                      uploadID: uploadID,
                                      partseq: index,
                                      accessToken: accessToken)
            }
        }

        // 3) create：提交分片，完成文件（rtype=3 覆盖同名）
        let created = try await postForm(
            "https://pan.baidu.com/rest/2.0/xpan/file",
            accessToken: accessToken,
            query: [URLQueryItem(name: "method", value: "create")],
            form: [
                "path": remotePath,
                "size": "\(data.count)",
                "isdir": "0",
                "uploadid": uploadID,
                "block_list": blockList,
                "rtype": "3",
            ]
        )
        guard let obj = try? JSONSerialization.jsonObject(with: created) as? [String: Any] else {
            throw BaiduPanError.server(message: "create 返回异常\(Self.bodySnippet(created))")
        }
        if let errno = obj["errno"] as? Int, errno != 0 {
            throw BaiduPanError.server(message: "create 失败（errno=\(errno)）\(Self.bodySnippet(created))")
        }
        // 该接口会 HTTP 200 但返回体带 error_code/error_msg；不校验会拼出不完整文件
        if obj["error_code"] != nil || obj["error_msg"] != nil {
            throw BaiduPanError.server(message: "create 被拒\(Self.bodySnippet(created))")
        }
        // create 返回 fs_id，缓存后 publish 的回读校验能立刻走 filemetas，不必等云端列表刷新
        if let fsId = (obj["fs_id"] as? NSNumber)?.int64Value, fsId != 0 {
            cacheFsId(fsId, path: remotePath)
        } else if let str = obj["fs_id"] as? String, let fsId = Int64(str), fsId != 0 {
            cacheFsId(fsId, path: remotePath)
        }
    }

    // MARK: 私有

    /// 截取响应体前 300 字符，便于在错误信息中看到服务端原始返回。
    private static func bodySnippet(_ data: Data) -> String {
        guard let text = String(data: data.prefix(300), encoding: .utf8), !text.isEmpty else {
            return ""
        }
        return " · 响应：\(text)"
    }

    /// 以 application/x-www-form-urlencoded 发送 POST 并返回响应体。
    private func postForm(_ base: String, accessToken: String,
                          query: [URLQueryItem], form: [String: String]) async throws -> Data {
        var comps = URLComponents(url: URL(string: base)!, resolvingAgainstBaseURL: false)!
        comps.queryItems = query + [URLQueryItem(name: "access_token", value: accessToken)]
        var req = URLRequest(url: comps.url!)
        req.httpMethod = "POST"
        req.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        req.setValue("pan.baidu.com", forHTTPHeaderField: "User-Agent")
        req.httpBody = form.map { "\($0.key)=\($0.value)" }.joined(separator: "&").data(using: .utf8)
        let (data, resp) = try await session.data(for: req)
        if let http = resp as? HTTPURLResponse, http.statusCode >= 300 {
            throw BaiduPanError.server(message: "请求失败（HTTP \(http.statusCode)）\(Self.bodySnippet(data))")
        }
        return data
    }

    /// 上传单个分片到 `superfile2`（开放平台分片上传专用端点）。
    private func uploadBlock(_ data: Data, remotePath: String,
                             uploadID: String, partseq: Int, accessToken: String) async throws {
        guard let url = URL(string: "https://c.pcs.baidu.com/rest/2.0/pcs/superfile2") else {
            throw BaiduPanError.invalidResponse
        }
        var comps = URLComponents(url: url, resolvingAgainstBaseURL: false)!
        comps.queryItems = [
            URLQueryItem(name: "method", value: "upload"),
            URLQueryItem(name: "type", value: "tmpfile"),
            URLQueryItem(name: "path", value: remotePath),
            URLQueryItem(name: "uploadid", value: uploadID),
            URLQueryItem(name: "partseq", value: "\(partseq)"),
            URLQueryItem(name: "access_token", value: accessToken),
        ]
        let boundary = "----TonoteBoundary\(UUID().uuidString)"
        var req = URLRequest(url: comps.url!)
        req.httpMethod = "POST"
        req.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")
        req.setValue("pan.baidu.com", forHTTPHeaderField: "User-Agent")
        var body = Data()
        body.append("--\(boundary)\r\n".data(using: .utf8)!)
        body.append("Content-Disposition: form-data; name=\"file\"; filename=\"blob\"\r\n".data(using: .utf8)!)
        body.append("Content-Type: application/octet-stream\r\n\r\n".data(using: .utf8)!)
        body.append(data)
        body.append("\r\n--\(boundary)--\r\n".data(using: .utf8)!)
        req.httpBody = body
        let (respData, resp) = try await session.data(for: req)
        if let http = resp as? HTTPURLResponse, http.statusCode >= 300 {
            throw BaiduPanError.server(message: "上传分片失败（HTTP \(http.statusCode)）\(Self.bodySnippet(respData))")
        }
        if let obj = try? JSONSerialization.jsonObject(with: respData) as? [String: Any] {
            // 该接口会 HTTP 200 但返回体带 error_code/error_msg；不校验会让 create 拼出不完整文件，
            // 另一端下载后 JSON 解析失败，表现为「同步成功但笔记空」。
            if obj["error_code"] != nil || obj["error_msg"] != nil {
                throw BaiduPanError.server(message: "上传分片被拒\(Self.bodySnippet(respData))")
            }
            if let errno = obj["errno"] as? Int, errno != 0 {
                throw BaiduPanError.server(message: "上传分片失败（errno=\(errno)）\(Self.bodySnippet(respData))")
            }
        }
    }

    // MARK: 私有

    private func get(_ url: URL) async throws -> Data {
        let (data, _) = try await session.data(from: url)
        return data
    }

    private func xpanURL(method: String, accessToken: String) -> URL {
        var comps = URLComponents(url: BaiduPanConfig.xpanAPI, resolvingAgainstBaseURL: false)!
        comps.queryItems = [
            URLQueryItem(name: "method", value: method),
            URLQueryItem(name: "access_token", value: accessToken),
        ]
        return comps.url!
    }
}

// MARK: - MD5（分片上传 block_list 需要）
extension Data {
    var md5Hex: String {
        var digest = [UInt8](repeating: 0, count: Int(CC_MD5_DIGEST_LENGTH))
        withUnsafeBytes { raw in
            _ = CC_MD5(raw.baseAddress, CC_LONG(count), &digest)
        }
        return digest.map { String(format: "%02x", $0) }.joined()
    }
}
