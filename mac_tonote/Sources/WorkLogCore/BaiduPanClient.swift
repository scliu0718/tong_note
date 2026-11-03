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

    public init() {}

    public var currentToken: BaiduPanToken? { tokenStore.load() }

    // MARK: 授权

    /// 构造授权页 URL（授权码模式）。
    public func authorizationURL(state: String) -> URL {
        var comps = URLComponents(url: BaiduPanConfig.authorizeURL, resolvingAgainstBaseURL: false)!
        comps.queryItems = [
            URLQueryItem(name: "response_type", value: "code"),
            URLQueryItem(name: "client_id", value: BaiduPanConfig.appKey),
            URLQueryItem(name: "redirect_uri", value: BaiduPanConfig.redirectURI),
            URLQueryItem(name: "scope", value: BaiduPanConfig.scope),
            URLQueryItem(name: "display", value: "popup"),
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

    /// 下载远端文件，返回 Data；远端不存在时抛 `notFound`。
    /// 采用开放平台 xpan `method=download` 取直链再拉取（老版 pcs 接口对开放平台应用会 403）。
    public func downloadData(_ remotePath: String, accessToken: String) async throws -> Data {
        var dc = URLComponents(url: URL(string: "https://pan.baidu.com/rest/2.0/xpan/file")!, resolvingAgainstBaseURL: false)!
        dc.queryItems = [
            URLQueryItem(name: "method", value: "download"),
            URLQueryItem(name: "access_token", value: accessToken),
            URLQueryItem(name: "path", value: remotePath),
            URLQueryItem(name: "threshold", value: "0"),
        ]
        let (metaData, _) = try await session.data(from: dc.url!)
        // 解析直链：{ "errno":0, "list":[ { "dlink":"https://...pcs.baidu.com/..." } ] }
        struct DlMeta: Codable {
            let errno: Int?
            let list: [DlItem]?
            struct DlItem: Codable { let dlink: String? }
        }
        guard let meta = try? JSONDecoder().decode(DlMeta.self, from: metaData),
              meta.errno ?? -1 == 0,
              let dlink = meta.list?.first?.dlink, !dlink.isEmpty else {
            // 拿不到直链（多为文件不存在）→ 视为无远端文件
            throw BaiduPanError.notFound
        }
        guard var dl = URLComponents(string: dlink) else { throw BaiduPanError.invalidResponse }
        var q = dl.queryItems ?? []
        q.append(URLQueryItem(name: "access_token", value: accessToken))
        dl.queryItems = q
        let (fileData, resp) = try await session.data(from: dl.url!)
        if let http = resp as? HTTPURLResponse, http.statusCode >= 400 {
            throw BaiduPanError.server(message: "下载文件失败（HTTP \(http.statusCode)）")
        }
        return fileData
    }

    /// 上传文件到远端（覆盖写）。开放平台标准三步：precreate → 上传分片 → create。
    public func uploadData(_ data: Data, to remotePath: String, accessToken: String) async throws {
        let md5 = data.md5Hex
        let blockList = "[\"\(md5)\"]".addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? "[\"\(md5)\"]"

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
        struct PreResp: Codable {
            let uploadid: String?
            let errno: Int?
        }
        guard let pre = try? JSONDecoder().decode(PreResp.self, from: precreate),
              let uploadID = pre.uploadid, pre.errno ?? -1 == 0 else {
            let errno = (try? JSONDecoder().decode(PreResp.self, from: precreate).errno) ?? -1
            throw BaiduPanError.server(message: "precreate 失败（errno=\(errno)）")
        }

        // 2) 上传分片到 c.pcs.baidu.com（type=tmpfile）
        guard let blockURL = URL(string: "https://c.pcs.baidu.com/rest/2.0/pcs/file") else {
            throw BaiduPanError.invalidResponse
        }
        var bc = URLComponents(url: blockURL, resolvingAgainstBaseURL: false)!
        bc.queryItems = [
            URLQueryItem(name: "method", value: "upload"),
            URLQueryItem(name: "type", value: "tmpfile"),
            URLQueryItem(name: "path", value: remotePath),
            URLQueryItem(name: "uploadid", value: uploadID),
            URLQueryItem(name: "partseq", value: "0"),
            URLQueryItem(name: "access_token", value: accessToken),
        ]
        let boundary = "----TonoteBoundary\(UUID().uuidString)"
        var upReq = URLRequest(url: bc.url!)
        upReq.httpMethod = "POST"
        upReq.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")
        var body = Data()
        body.append("--\(boundary)\r\n".data(using: .utf8)!)
        body.append("Content-Disposition: form-data; name=\"file\"; filename=\"blob\"\r\n".data(using: .utf8)!)
        body.append("Content-Type: application/octet-stream\r\n\r\n".data(using: .utf8)!)
        body.append(data)
        body.append("\r\n--\(boundary)--\r\n".data(using: .utf8)!)
        upReq.httpBody = body
        let (upData, upResp) = try await session.data(for: upReq)
        if let http = upResp as? HTTPURLResponse, http.statusCode >= 300 {
            throw BaiduPanError.server(message: "上传分片失败（HTTP \(http.statusCode)）")
        }
        if let obj = try? JSONSerialization.jsonObject(with: upData) as? [String: Any],
           let errno = obj["errno"] as? Int, errno != 0 {
            throw BaiduPanError.server(message: "上传分片失败（errno=\(errno)）")
        }

        // 3) create：提交分片，完成文件（rtype=3 覆盖同名）
        _ = try await postForm(
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
    }

    // MARK: 私有

    /// 以 application/x-www-form-urlencoded 发送 POST 并返回响应体。
    private func postForm(_ base: String, accessToken: String,
                          query: [URLQueryItem], form: [String: String]) async throws -> Data {
        var comps = URLComponents(url: URL(string: base)!, resolvingAgainstBaseURL: false)!
        comps.queryItems = query + [URLQueryItem(name: "access_token", value: accessToken)]
        var req = URLRequest(url: comps.url!)
        req.httpMethod = "POST"
        req.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        req.httpBody = form.map { "\($0.key)=\($0.value)" }.joined(separator: "&").data(using: .utf8)
        let (data, resp) = try await session.data(for: req)
        if let http = resp as? HTTPURLResponse, http.statusCode >= 300 {
            throw BaiduPanError.server(message: "请求失败（HTTP \(http.statusCode)）")
        }
        return data
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
