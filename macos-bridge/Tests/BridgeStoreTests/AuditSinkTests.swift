import BridgeCore
import Foundation
import Testing

@testable import BridgeStore

@Suite("Rotating JSONL audit sink")
struct AuditSinkTests {
    private let root = TemporaryRoot()

    private func lineLength() throws -> Int {
        try auditEvent().jsonLine().utf8.count + 1  // + newline
    }

    private func contents(_ url: URL) throws -> [String] {
        try String(contentsOf: url, encoding: .utf8)
            .split(separator: "\n", omittingEmptySubsequences: true)
            .map(String.init)
    }

    @Test("lines land in the live file, one JSON object per line")
    func writesLines() throws {
        let sink = try RotatingJSONLAuditSink(directory: root.directories.logs)
        sink.record(auditEvent())
        sink.record(auditEvent(status: 429))
        sink.flush()

        let lines = try contents(sink.currentURL)
        #expect(lines.count == 2)
        for line in lines {
            let parsed = try JSONSerialization.jsonObject(with: Data(line.utf8)) as? [String: Any]
            #expect(parsed?["op"] as? String == "reminders.create")
        }
        #expect(sink.failureCount == 0)
    }

    @Test("the log directory is 0700 and the file 0600")
    func setsPermissions() throws {
        let sink = try RotatingJSONLAuditSink(directory: root.directories.logs)
        sink.record(auditEvent())
        sink.flush()

        #expect(try FilePermissions.mode(of: root.directories.logs) == FilePermissions.directory)
        #expect(try FilePermissions.mode(of: sink.currentURL) == FilePermissions.file)
    }

    @Test("rotation happens on the line that would cross the cap, not before it")
    func rotatesAtTheBoundary() throws {
        let length = try lineLength()
        let sink = try RotatingJSONLAuditSink(
            directory: root.directories.logs,
            maxBytes: length * 3,
            keptFiles: 5
        )

        for _ in 0..<3 { sink.record(auditEvent()) }
        sink.flush()
        #expect(sink.currentByteCount == length * 3)
        #expect(!FileManager.default.fileExists(atPath: sink.archiveURL(1).path))
        #expect(try contents(sink.currentURL).count == 3)

        // The fourth line is the one that no longer fits.
        sink.record(auditEvent())
        sink.flush()
        #expect(sink.currentByteCount == length)
        #expect(try contents(sink.archiveURL(1)).count == 3)
        #expect(try contents(sink.currentURL).count == 1)
    }

    @Test("a single oversized line is still written rather than being dropped")
    func writesOversizedLineIntoAFreshFile() throws {
        let length = try lineLength()
        let sink = try RotatingJSONLAuditSink(
            directory: root.directories.logs,
            maxBytes: length - 1,
            keptFiles: 2
        )
        sink.record(auditEvent())
        sink.record(auditEvent())
        sink.flush()

        // Each line exceeds the cap on its own, so every line rotates — but nothing is lost.
        #expect(try contents(sink.currentURL).count == 1)
        #expect(try contents(sink.archiveURL(1)).count == 1)
        #expect(sink.failureCount == 0)
    }

    @Test("only `keptFiles` files survive; the oldest falls off the end")
    func keepsAFixedNumberOfFiles() throws {
        let length = try lineLength()
        let sink = try RotatingJSONLAuditSink(
            directory: root.directories.logs,
            maxBytes: length,
            keptFiles: 3
        )
        for _ in 0..<6 { sink.record(auditEvent()) }
        sink.flush()

        let files = try FileManager.default
            .contentsOfDirectory(atPath: root.directories.logs.path)
            .sorted()
        #expect(files == ["audit.1.jsonl", "audit.2.jsonl", "audit.jsonl"])
        #expect(!FileManager.default.fileExists(atPath: sink.archiveURL(3).path))
    }

    @Test("archives keep 0600 after being renamed")
    func archivesStayPrivate() throws {
        let length = try lineLength()
        let sink = try RotatingJSONLAuditSink(directory: root.directories.logs, maxBytes: length, keptFiles: 3)
        for _ in 0..<3 { sink.record(auditEvent()) }
        sink.flush()

        #expect(try FilePermissions.mode(of: sink.archiveURL(1)) == FilePermissions.file)
        #expect(try FilePermissions.mode(of: sink.currentURL) == FilePermissions.file)
    }

    @Test("reopening appends rather than truncating")
    func appendsOnReopen() throws {
        let first = try RotatingJSONLAuditSink(directory: root.directories.logs)
        first.record(auditEvent())
        first.close()

        let second = try RotatingJSONLAuditSink(directory: root.directories.logs)
        second.record(auditEvent())
        second.flush()

        #expect(try contents(second.currentURL).count == 2)
    }

    @Test("a broken sink records the failure instead of throwing into the request path")
    func neverThrows() throws {
        let sink = try RotatingJSONLAuditSink(directory: root.directories.logs)
        sink.close()

        sink.record(auditEvent())  // must not crash and must not throw
        #expect(sink.failureCount == 1)
        #expect(sink.lastError != nil)
    }

    @Test("the production defaults are 5 MiB across 5 files")
    func productionDefaults() {
        #expect(RotatingJSONLAuditSink.defaultMaxBytes == 5 * 1024 * 1024)
        #expect(RotatingJSONLAuditSink.defaultKeptFiles == 5)
    }

    @Test("what reaches disk still carries no user content")
    func linesCarryNoUserContent() throws {
        let sink = try RotatingJSONLAuditSink(directory: root.directories.logs)
        sink.record(auditEvent())
        sink.flush()

        let text = try String(contentsOf: sink.currentURL, encoding: .utf8)
        #expect(!text.contains("/Users/"))
        #expect(!text.contains("erdab_"))
        #expect(!text.contains("Buy milk"))
    }
}
