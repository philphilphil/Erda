import BridgeCore
import Foundation

/// The checks that can only be made from **inside the signed bundle**.
///
/// The legacy keychain attaches an ACL to its item naming the trusted application by its code
/// signature. `swift test` runs under `xctest`, which is not our bundle, so a keychain round
/// trip there proves nothing about whether the real app can read its own token after a rebuild.
/// This runs from `ErdaBridge.app --selftest` instead.
///
/// It is safe to run against a live installation: the database and audit checks use a scratch
/// directory, and the keychain check uses its own service name so it can never read, overwrite
/// or delete the production token.
public enum StoreSelfTest {
    public static let keychainService = BridgeDirectories.bundleIdentifier + ".selftest"

    public struct Report: Sendable {
        public var lines: [String] = []
        public var failures = 0

        public var passed: Bool { failures == 0 }
        public var text: String { lines.joined(separator: "\n") }

        mutating func pass(_ label: String, _ detail: String = "") {
            lines.append("  ok    \(label)\(detail.isEmpty ? "" : " — \(detail)")")
        }

        mutating func fail(_ label: String, _ detail: String) {
            failures += 1
            lines.append("  FAIL  \(label) — \(detail)")
        }

        mutating func section(_ title: String) {
            lines.append(title)
        }

        mutating func check(_ label: String, _ body: () throws -> String) {
            do {
                pass(label, try body())
            } catch {
                fail(label, String(describing: error))
            }
        }
    }

    /// Answers the question `--selftest` cannot: does the keychain ACL survive a rebuild?
    ///
    /// The legacy keychain records a code requirement rather than a cdhash, so re-signing with
    /// the same identity and the same `--identifier` should keep the trust (dossier §5.3). Run
    /// `--selftest-persist write`, rebuild with `make bundle`, then `--selftest-persist read`:
    /// if the read succeeds with no modal prompt, the ACL survived.
    public static func runPersistenceProbe(phase: PersistencePhase) -> Report {
        var report = Report()
        report.lines.append("ErdaBridge keychain persistence probe — \(phase.rawValue)")
        report.lines.append("bundle: \(Bundle.main.bundleIdentifier ?? "<none — not running from the .app>")")
        report.lines.append("")

        let store = KeychainTokenStore(service: keychainService, account: "persist")
        let service = TokenService(store: store)

        switch phase {
        case .write:
            report.check("write") {
                try? store.delete()
                let generated = try service.rotate(now: Date())
                // The token id is enough to correlate the two phases and is not a secret.
                return "stored tokenId \(generated.material.tokenId)"
            }
        case .read:
            report.check("read back after re-sign") {
                guard let material = try store.load() else { throw SelfTestError.unexpected }
                return "read tokenId \(material.tokenId) — ACL survived the rebuild"
            }
        case .cleanup:
            report.check("cleanup") {
                try store.delete()
                return "removed"
            }
        }

        report.lines.append("")
        report.lines.append(report.failures == 0 ? "PASS" : "FAIL (\(report.failures) check(s))")
        return report
    }

    public enum PersistencePhase: String, Sendable, CaseIterable {
        case write, read, cleanup
    }

    public static func run() -> Report {
        var report = Report()
        report.lines.append("ErdaBridge store self-test")
        report.lines.append("bundle: \(Bundle.main.bundleIdentifier ?? "<none — not running from the .app>")")
        report.lines.append("")

        let scratch = FileManager.default.temporaryDirectory
            .appendingPathComponent("erdabridge-selftest-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: scratch) }

        let directories = BridgeDirectories(
            applicationSupport: scratch.appendingPathComponent("support", isDirectory: true),
            logs: scratch.appendingPathComponent("logs", isDirectory: true)
        )

        report.section("storage")
        runStorageChecks(directories, &report)

        report.lines.append("")
        report.section("token store — file")
        runTokenStoreChecks(
            store: FileTokenStore(url: directories.tokenFileURL),
            report: &report
        )

        report.lines.append("")
        report.section("token store — keychain (service \(keychainService))")
        runTokenStoreChecks(
            store: KeychainTokenStore(service: keychainService, account: "selftest"),
            report: &report
        )

        report.lines.append("")
        report.lines.append(report.failures == 0 ? "PASS" : "FAIL (\(report.failures) check(s))")
        return report
    }

    // MARK: - Storage

    private static func runStorageChecks(_ directories: BridgeDirectories, _ report: inout Report) {
        var handle: BridgeStoreHandle?
        report.check("open + migrate") {
            let opened = try BridgeStoreHandle.open(directories: directories)
            handle = opened
            return "schema version \(opened.schemaVersion)"
        }
        guard let handle else { return }
        defer { handle.close() }

        report.check("application support mode") {
            let mode = try FilePermissions.mode(of: directories.applicationSupport)
            guard mode == FilePermissions.directory else { throw SelfTestError.wrongMode(mode) }
            return String(format: "0%o", Int(mode))
        }
        report.check("database mode") {
            let mode = try FilePermissions.mode(of: directories.databaseURL)
            guard mode == FilePermissions.file else { throw SelfTestError.wrongMode(mode) }
            return String(format: "0%o", Int(mode))
        }
        report.check("allowlist round trip") {
            guard let alias = Alias(rawValue: "selftest") else { throw SelfTestError.unexpected }
            try handle.allowlist.upsert(
                AllowlistEntry(
                    alias: alias,
                    calendarId: "cal-selftest",
                    titleAtBind: "Self Test",
                    sourceAtBind: "iCloud",
                    boundAt: Date(),
                    state: .ok
                )
            )
            guard try handle.allowlist.entry(for: alias)?.calendarId == "cal-selftest" else {
                throw SelfTestError.unexpected
            }
            return "1 entry"
        }
        report.check("idempotency claim/replay") {
            let hash = Array(repeating: UInt8(7), count: 32)
            guard try handle.idempotency.claim(key: "selftest", requestHash: hash) == .proceed else {
                throw SelfTestError.unexpected
            }
            guard try handle.idempotency.claim(key: "selftest", requestHash: hash) == .conflictInProgress else {
                throw SelfTestError.unexpected
            }
            try handle.idempotency.complete(key: "selftest", status: 201, body: Array("{}".utf8))
            guard try handle.idempotency.claim(key: "selftest", requestHash: hash)
                == .replay(status: 201, body: Array("{}".utf8))
            else { throw SelfTestError.unexpected }
            return "proceed → in-progress → replay"
        }
        report.check("audit sink") {
            let sink = try RotatingJSONLAuditSink(directory: directories.logs)
            sink.record(
                AuditEvent(
                    timestamp: Date(),
                    requestId: UUID(),
                    tokenId: nil,
                    operation: .unrouted,
                    alias: nil,
                    result: .ok,
                    status: 200,
                    durationMs: 0
                )
            )
            sink.flush()
            guard sink.failureCount == 0 else { throw SelfTestError.unexpected }
            let mode = try FilePermissions.mode(of: sink.currentURL)
            guard mode == FilePermissions.file else { throw SelfTestError.wrongMode(mode) }
            sink.close()
            return String(format: "0%o", Int(mode))
        }
    }

    // MARK: - Token stores

    private static func runTokenStoreChecks(store: any TokenStore, report: inout Report) {
        // Leaves nothing behind either way.
        defer { try? store.delete() }

        let service = TokenService(store: store)
        var generated: GeneratedToken?

        report.check("generate + save") {
            try? store.delete()
            let token = try service.rotate(now: Date())
            generated = token
            return "tokenId \(token.material.tokenId)"
        }
        guard let generated else { return }

        report.check("load") {
            guard let loaded = try store.load() else { throw SelfTestError.unexpected }
            guard loaded == generated.material else { throw SelfTestError.unexpected }
            return "digest matches"
        }
        report.check("verify correct token") {
            guard let verifier = try service.currentVerifier() else { throw SelfTestError.unexpected }
            guard verifier.verify(presentedToken: generated.token) == generated.material.tokenId else {
                throw SelfTestError.unexpected
            }
            return "accepted"
        }
        report.check("reject wrong token") {
            guard let verifier = try service.currentVerifier() else { throw SelfTestError.unexpected }
            guard verifier.verify(presentedToken: generated.token + "x") == nil,
                  verifier.verify(presentedToken: "") == nil
            else { throw SelfTestError.unexpected }
            return "rejected"
        }
        report.check("rotate invalidates the old token") {
            let rotated = try service.rotate(now: Date())
            guard let verifier = try service.currentVerifier() else { throw SelfTestError.unexpected }
            guard verifier.verify(presentedToken: generated.token) == nil else {
                throw SelfTestError.unexpected
            }
            guard verifier.verify(presentedToken: rotated.token) == rotated.material.tokenId else {
                throw SelfTestError.unexpected
            }
            return "old 401s, new accepted"
        }
        report.check("delete") {
            try store.delete()
            guard try store.load() == nil else { throw SelfTestError.unexpected }
            return "gone"
        }
    }
}

enum SelfTestError: Error, CustomStringConvertible {
    case wrongMode(Int16)
    case unexpected

    var description: String {
        switch self {
        case .wrongMode(let mode): "unexpected mode \(String(format: "0%o", Int(mode)))"
        case .unexpected: "unexpected result"
        }
    }
}
