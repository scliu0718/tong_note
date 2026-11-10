import Foundation

public extension Notification.Name {
    /// 云端设备文件标明本机这次登录已下线。收到后应清除本地令牌并回到登录页。
    static let tonoteDeviceKicked = Notification.Name("tonote.deviceSession.kicked")
}

public enum DeviceSessionError: Error, Equatable {
    case kicked
}

extension DeviceSessionError: LocalizedError {
    public var errorDescription: String? {
        switch self {
        case .kicked:
            return "本机已在其他设备被下线，请重新登录"
        }
    }
}

/// 读完本机设备文件后该怎么做。只有「本次 session 被标下线」才退出登录。
public enum DeviceSessionDecision: Equatable {
    /// 云端记录就是这次登录，且没有下线标记。不要回写，避免冲掉刚被别的设备写上的标记。
    case unchanged
    /// 还没有云端文件，或云端是上一次登录。写回当前这次登录。
    case publish
    case kicked
}

/// `/apps/tong_log/devices/<设备编号>.json`
public struct DeviceRecord: Codable, Equatable, Identifiable, Sendable {
    public var deviceId: String
    public var deviceName: String
    public var sessionId: String
    public var kicked: Bool

    public var id: String { deviceId }

    public init(deviceId: String, deviceName: String, sessionId: String, kicked: Bool) {
        self.deviceId = deviceId
        self.deviceName = deviceName
        self.sessionId = sessionId
        self.kicked = kicked
    }

    enum CodingKeys: String, CodingKey {
        case deviceId = "device_id"
        case deviceName = "device_name"
        case sessionId = "session_id"
        case kicked
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        deviceId = try c.decode(String.self, forKey: .deviceId)
        deviceName = try c.decodeIfPresent(String.self, forKey: .deviceName) ?? ""
        sessionId = try c.decode(String.self, forKey: .sessionId)
        kicked = try c.decodeIfPresent(Bool.self, forKey: .kicked) ?? false
    }

    public func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(deviceId, forKey: .deviceId)
        try c.encode(deviceName, forKey: .deviceName)
        try c.encode(sessionId, forKey: .sessionId)
        try c.encode(kicked, forKey: .kicked)
    }

    /// 下线只改标记，登录编号保持不变。对方重新登录会换成新编号并把标记改回 false。
    public func markedKicked() -> DeviceRecord {
        DeviceRecord(deviceId: deviceId, deviceName: deviceName, sessionId: sessionId, kicked: true)
    }

    public static func decision(localDeviceId: String,
                                localSessionId: String,
                                remote: DeviceRecord?) -> DeviceSessionDecision {
        guard let remote, remote.deviceId == localDeviceId else { return .publish }
        if remote.kicked && remote.sessionId == localSessionId { return .kicked }
        if !remote.kicked && remote.sessionId == localSessionId { return .unchanged }
        return .publish
    }

    public static func isSafeDeviceId(_ id: String) -> Bool {
        id.range(of: #"^[A-Za-z0-9-]{8,80}$"#, options: .regularExpression) != nil
    }

    public func encode() throws -> Data {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        return try encoder.encode(self)
    }

    public static func decode(_ data: Data) throws -> DeviceRecord {
        try JSONDecoder().decode(DeviceRecord.self, from: data)
    }
}

public struct LocalDeviceIdentity: Codable, Equatable, Sendable {
    public var deviceId: String
    public var sessionId: String?
    public var deviceName: String

    public init(deviceId: String, sessionId: String?, deviceName: String) {
        self.deviceId = deviceId
        self.sessionId = sessionId
        self.deviceName = deviceName
    }

    enum CodingKeys: String, CodingKey {
        case deviceId = "device_id"
        case sessionId = "session_id"
        case deviceName = "device_name"
    }
}

/// 本机设备编号与当前登录编号。设备编号生成后不变；退出登录不删除它。
public final class DeviceIdentityStore: @unchecked Sendable {
    private let fileURL: URL
    private let lock = NSLock()

    public init(directory: URL) {
        let fm = FileManager.default
        try? fm.createDirectory(at: directory, withIntermediateDirectories: true)
        fileURL = directory.appendingPathComponent("device.json")
    }

    public static func applicationSupport() -> DeviceIdentityStore {
        let dir = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first!
            .appendingPathComponent("tonote", isDirectory: true)
        return DeviceIdentityStore(directory: dir)
    }

    public func load() -> LocalDeviceIdentity? {
        lock.lock()
        defer { lock.unlock() }
        guard let data = try? Data(contentsOf: fileURL) else { return nil }
        return try? JSONDecoder().decode(LocalDeviceIdentity.self, from: data)
    }

    public func loadOrCreate(deviceName: String) -> LocalDeviceIdentity {
        if let existing = load(), DeviceRecord.isSafeDeviceId(existing.deviceId) {
            return existing
        }
        let created = LocalDeviceIdentity(deviceId: UUID().uuidString,
                                          sessionId: nil,
                                          deviceName: deviceName)
        save(created)
        return created
    }

    /// 开始一次新的登录。设备编号不变。
    @discardableResult
    public func updateSession(_ sessionId: String, deviceName: String) -> LocalDeviceIdentity {
        var identity = loadOrCreate(deviceName: deviceName)
        identity.sessionId = sessionId
        identity.deviceName = deviceName
        save(identity)
        return identity
    }

    public func save(_ identity: LocalDeviceIdentity) {
        lock.lock()
        defer { lock.unlock() }
        guard let data = try? JSONEncoder().encode(identity) else { return }
        try? data.write(to: fileURL, options: .atomic)
    }
}

/// 云端设备名单。每台设备只写自己的 JSON；下线是把对方文件的 `kicked` 改成 true。
public final class DeviceRegistry: @unchecked Sendable {
    public static let shared = DeviceRegistry()

    private let store: DeviceIdentityStore
    private let client: BaiduPanClient

    public init(store: DeviceIdentityStore = .applicationSupport(),
                client: BaiduPanClient = .shared) {
        self.store = store
        self.client = client
    }

    public var currentDeviceId: String {
        store.loadOrCreate(deviceName: Self.currentDeviceName()).deviceId
    }

    /// 设备列表还没从云端读到本机时，用本地登记补一行。
    public func localSnapshot() -> DeviceRecord {
        let identity = store.loadOrCreate(deviceName: Self.currentDeviceName())
        return DeviceRecord(deviceId: identity.deviceId,
                            deviceName: identity.deviceName,
                            sessionId: identity.sessionId ?? "",
                            kicked: false)
    }

    /// 授权成功后调用：换一个新的登录编号，并覆盖本机那份设备文件。
    public func beginLogin() async throws {
        let token = try await client.validToken()
        let identity = store.updateSession(UUID().uuidString, deviceName: Self.currentDeviceName())
        guard let sessionId = identity.sessionId else { return }
        let record = DeviceRecord(deviceId: identity.deviceId,
                                  deviceName: identity.deviceName,
                                  sessionId: sessionId,
                                  kicked: false)
        try await upload(record, accessToken: token.accessToken)
    }

    /// 打开应用或上传前调用。网络失败会抛错；文件不存在则补登记。确认被下线时发通知并抛 `kicked`。
    public func ensureNotKicked() async throws {
        let token = try await client.validToken()
        try await ensureNotKicked(accessToken: token.accessToken)
    }

    public func ensureNotKicked(accessToken: String) async throws {
        var identity = store.loadOrCreate(deviceName: Self.currentDeviceName())
        if identity.sessionId == nil {
            identity = store.updateSession(UUID().uuidString, deviceName: identity.deviceName)
        }
        guard let sessionId = identity.sessionId else { return }

        let remote: DeviceRecord?
        do {
            let data = try await client.downloadData(Self.filePath(deviceId: identity.deviceId),
                                                     accessToken: accessToken)
            remote = try DeviceRecord.decode(data)
        } catch BaiduPanError.notFound {
            remote = nil
        }

        switch DeviceRecord.decision(localDeviceId: identity.deviceId,
                                     localSessionId: sessionId,
                                     remote: remote) {
        case .unchanged:
            return
        case .publish:
            let record = DeviceRecord(deviceId: identity.deviceId,
                                      deviceName: identity.deviceName,
                                      sessionId: sessionId,
                                      kicked: false)
            try await upload(record, accessToken: accessToken)
        case .kicked:
            NotificationCenter.default.post(name: .tonoteDeviceKicked, object: nil)
            throw DeviceSessionError.kicked
        }
    }

    public func listDevices() async throws -> [DeviceRecord] {
        let token = try await client.validToken()
        let entries: [BaiduPanListEntry]
        do {
            entries = try await client.listFiles(directory: Self.devicesDirectory,
                                                 accessToken: token.accessToken)
        } catch BaiduPanError.notFound {
            return []
        }
        var records: [DeviceRecord] = []
        for entry in entries {
            let name = entry.serverFilename ?? ""
            guard entry.isdir != 1, name.hasSuffix(".json") else { continue }
            let path = entry.path ?? (Self.devicesDirectory + "/" + name)
            do {
                let data = try await client.downloadData(path, accessToken: token.accessToken)
                let record = try DeviceRecord.decode(data)
                guard DeviceRecord.isSafeDeviceId(record.deviceId) else { continue }
                records.append(record)
            } catch BaiduPanError.notFound {
                continue
            }
        }
        let mine = currentDeviceId
        return records.sorted { lhs, rhs in
            if lhs.deviceId == mine { return true }
            if rhs.deviceId == mine { return false }
            return lhs.deviceName.localizedCompare(rhs.deviceName) == .orderedAscending
        }
    }

    /// 把另一台设备的文件标成下线。不改它的登录编号，也不写本机文件。
    public func kick(deviceId: String) async throws {
        guard DeviceRecord.isSafeDeviceId(deviceId) else {
            throw BaiduPanError.server(message: "设备编号无效")
        }
        let mine = store.loadOrCreate(deviceName: Self.currentDeviceName())
        guard deviceId != mine.deviceId else { return }
        let token = try await client.validToken()
        let data = try await client.downloadData(Self.filePath(deviceId: deviceId),
                                                 accessToken: token.accessToken)
        let remote = try DeviceRecord.decode(data)
        guard remote.deviceId == deviceId else {
            throw BaiduPanError.server(message: "设备文件与编号不一致")
        }
        guard !remote.kicked else { return }
        try await upload(remote.markedKicked(), accessToken: token.accessToken)
    }

    public static func filePath(deviceId: String) -> String {
        devicesDirectory + "/" + deviceId + ".json"
    }

    public static var devicesDirectory: String {
        BaiduPanConfig.remoteRoot + "/devices"
    }

    private func upload(_ record: DeviceRecord, accessToken: String) async throws {
        try await client.uploadData(try record.encode(),
                                    to: Self.filePath(deviceId: record.deviceId),
                                    accessToken: accessToken)
    }

    private static func currentDeviceName() -> String {
        let host = Host.current()
        let name = (host.localizedName ?? host.name ?? "Mac")
            .trimmingCharacters(in: .whitespacesAndNewlines)
        return name.isEmpty ? "Mac" : name
    }
}
