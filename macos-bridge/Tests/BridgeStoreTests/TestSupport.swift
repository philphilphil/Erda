import BridgeCore
import Foundation
import Testing

@testable import BridgeStore

/// A throwaway `~/Library`-shaped pair of directories under the system temp directory.
///
/// Held as a stored property by each suite: swift-testing builds a fresh suite value per test,
/// so `deinit` removes the tree once that test finishes.
final class TemporaryRoot: @unchecked Sendable {
    let url: URL
    let directories: BridgeDirectories

    init() {
        url = FileManager.default.temporaryDirectory
            .appendingPathComponent("bridgestore-tests-\(UUID().uuidString)", isDirectory: true)
        directories = BridgeDirectories(
            applicationSupport: url.appendingPathComponent("support", isDirectory: true),
            logs: url.appendingPathComponent("logs", isDirectory: true)
        )
    }

    deinit {
        try? FileManager.default.removeItem(at: url)
    }

    func open(clock: any BridgeClock = SystemClock()) throws -> BridgeStoreHandle {
        try BridgeStoreHandle.open(directories: directories, clock: clock)
    }

    /// A second connection to the same file, for the cross-connection contention tests.
    func openRawConnection() throws -> SQLiteDB {
        try SQLiteDB(path: directories.databaseURL.path)
    }
}

func alias(_ raw: String, sourceLocation: SourceLocation = #_sourceLocation) throws -> Alias {
    try #require(Alias(rawValue: raw), sourceLocation: sourceLocation)
}

func allowlistEntry(
    _ raw: String,
    calendarId: String? = nil,
    state: AllowlistState = .ok,
    boundAt: Date = Date(timeIntervalSince1970: 1_780_000_000)
) throws -> AllowlistEntry {
    AllowlistEntry(
        alias: try alias(raw),
        calendarId: calendarId ?? "cal-\(raw)",
        titleAtBind: "List \(raw)",
        sourceAtBind: "iCloud",
        boundAt: boundAt,
        state: state
    )
}

func hash(_ seed: UInt8) -> [UInt8] {
    (0..<32).map { UInt8(($0 &* 31 &+ Int(seed)) % 251) }
}

func auditEvent(
    at timestamp: Date = Date(timeIntervalSince1970: 1_785_481_200.221),
    status: Int = 200
) -> AuditEvent {
    AuditEvent(
        timestamp: timestamp,
        requestId: UUID(uuidString: "6F0C1B6E-1F4A-4A9D-9F3E-1B2C3D4E5F60")!,
        tokenId: TokenId(rawValue: "a1b2c3d4"),
        operation: .remindersCreate,
        alias: Alias(rawValue: "inbox"),
        result: .ok,
        status: status,
        durationMs: 38,
        replay: false
    )
}
