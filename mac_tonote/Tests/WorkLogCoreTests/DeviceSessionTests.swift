import XCTest
@testable import WorkLogCore

final class DeviceSessionTests: XCTestCase {

    private let deviceId = "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE"
    private let sessionId = "11111111-2222-3333-4444-555555555555"

    private func remote(session: String, kicked: Bool, id: String? = nil) -> DeviceRecord {
        DeviceRecord(deviceId: id ?? deviceId,
                     deviceName: "刘松的 MacBook",
                     sessionId: session,
                     kicked: kicked)
    }

    func testSameSessionStaysWhenNotKicked() {
        let decision = DeviceRecord.decision(localDeviceId: deviceId,
                                             localSessionId: sessionId,
                                             remote: remote(session: sessionId, kicked: false))
        XCTAssertEqual(decision, .unchanged)
    }

    func testSameSessionIsKicked() {
        let decision = DeviceRecord.decision(localDeviceId: deviceId,
                                             localSessionId: sessionId,
                                             remote: remote(session: sessionId, kicked: true))
        XCTAssertEqual(decision, .kicked)
    }

    func testNewLoginReplacesAnOldKick() {
        let decision = DeviceRecord.decision(localDeviceId: deviceId,
                                             localSessionId: "new-session-id",
                                             remote: remote(session: sessionId, kicked: true))
        XCTAssertEqual(decision, .publish)
    }

    func testMissingRemoteFileIsPublished() {
        let decision = DeviceRecord.decision(localDeviceId: deviceId,
                                             localSessionId: sessionId,
                                             remote: nil)
        XCTAssertEqual(decision, .publish)
    }

    func testKickKeepsSessionId() {
        let updated = remote(session: sessionId, kicked: false).markedKicked()
        XCTAssertTrue(updated.kicked)
        XCTAssertEqual(updated.sessionId, sessionId)
        XCTAssertEqual(updated.deviceId, deviceId)
    }

    func testJSONRoundTripUsesCloudKeys() throws {
        let data = try remote(session: sessionId, kicked: true).encode()
        let text = String(decoding: data, as: UTF8.self)
        XCTAssertTrue(text.contains("\"device_id\""))
        XCTAssertTrue(text.contains("\"session_id\""))
        XCTAssertTrue(text.contains("\"kicked\" : true"))
        let back = try DeviceRecord.decode(data)
        XCTAssertEqual(back.deviceName, "刘松的 MacBook")
        XCTAssertEqual(back.kicked, true)
        XCTAssertEqual(DeviceRecord.decision(localDeviceId: deviceId,
                                             localSessionId: sessionId,
                                             remote: back), .kicked)
    }

    func testMissingKickedDefaultsToFalse() throws {
        let json = """
        {"device_id":"\(deviceId)","device_name":"Mac","session_id":"\(sessionId)"}
        """
        let record = try DeviceRecord.decode(Data(json.utf8))
        XCTAssertFalse(record.kicked)
    }

    func testIdentityKeepsDeviceIdAcrossLogins() {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("tonote-device-\(UUID().uuidString)", isDirectory: true)
        let store = DeviceIdentityStore(directory: directory)
        let first = store.loadOrCreate(deviceName: "旧名字")
        let second = store.updateSession("session-a", deviceName: "刘松的 MacBook")
        let third = store.updateSession("session-b", deviceName: "刘松的 MacBook")
        XCTAssertEqual(first.deviceId, second.deviceId)
        XCTAssertEqual(second.deviceId, third.deviceId)
        XCTAssertEqual(third.sessionId, "session-b")
        XCTAssertEqual(store.load()?.deviceId, first.deviceId)
    }

    func testRejectsUnsafeDeviceId() {
        XCTAssertTrue(DeviceRecord.isSafeDeviceId(deviceId))
        XCTAssertFalse(DeviceRecord.isSafeDeviceId("../manifest"))
        XCTAssertFalse(DeviceRecord.isSafeDeviceId("a/b"))
        XCTAssertFalse(DeviceRecord.isSafeDeviceId("short"))
    }
}
