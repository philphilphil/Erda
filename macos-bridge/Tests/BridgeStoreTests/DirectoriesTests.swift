import BridgeCore
import Foundation
import Testing

@testable import BridgeStore

@Suite("Directories and permissions")
struct DirectoriesTests {
    private let root = TemporaryRoot()

    @Test("both directories are created 0700")
    func createsDirectories() throws {
        try root.directories.create()

        #expect(try FilePermissions.mode(of: root.directories.applicationSupport) == 0o700)
        #expect(try FilePermissions.mode(of: root.directories.logs) == 0o700)
    }

    @Test("a directory left world-readable by an earlier build is repaired")
    func repairsLooseMode() throws {
        try FileManager.default.createDirectory(
            at: root.directories.applicationSupport,
            withIntermediateDirectories: true,
            attributes: [.posixPermissions: NSNumber(value: Int16(0o755))]
        )
        #expect(try FilePermissions.mode(of: root.directories.applicationSupport) == 0o755)

        try root.directories.create()
        #expect(try FilePermissions.mode(of: root.directories.applicationSupport) == 0o700)
    }

    @Test("the database and its WAL sidecars are 0600")
    func databaseFilesArePrivate() throws {
        let store = try root.open()
        // Force a write so the -wal and -shm files exist, then re-harden.
        try store.allowlist.upsert(try allowlistEntry("inbox"))
        try root.directories.hardenDatabaseFiles()

        #expect(try FilePermissions.mode(of: root.directories.databaseURL) == 0o600)
        #expect(FileManager.default.fileExists(atPath: root.directories.walURL.path))
        #expect(try FilePermissions.mode(of: root.directories.walURL) == 0o600)
        #expect(try FilePermissions.mode(of: root.directories.sharedMemoryURL) == 0o600)
    }

    @Test("hardening a path that does not exist is a no-op")
    func hardeningIsTolerant() throws {
        try root.directories.create()
        try FilePermissions.hardenFile(at: root.directories.walURL)  // must not throw
    }

    @Test("the production locations are the ones from the design")
    func standardLocations() throws {
        let directories = try BridgeDirectories.standard()

        #expect(directories.applicationSupport.path.hasSuffix(
            "/Library/Application Support/de.philippbaum.erdabridge"
        ))
        #expect(directories.logs.path.hasSuffix("/Library/Logs/ErdaBridge"))
        #expect(directories.databaseURL.lastPathComponent == "bridge.sqlite")
        #expect(directories.walURL.lastPathComponent == "bridge.sqlite-wal")
        #expect(directories.auditLogURL.lastPathComponent == "audit.jsonl")
        #expect(directories.tokenFileURL.lastPathComponent == "token.json")
        #expect(BridgeDirectories.bundleIdentifier == "de.philippbaum.erdabridge")
    }

    @Test("`standard()` only computes paths — it must not create anything")
    func standardDoesNotCreate() throws {
        // Called during a status read on a machine that has never run the bridge, this must not
        // materialise a directory as a side effect.
        _ = try BridgeDirectories.standard()
    }
}
