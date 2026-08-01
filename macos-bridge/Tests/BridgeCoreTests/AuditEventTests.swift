import Foundation
import Testing

@testable import BridgeCore

@Suite("Audit events")
struct AuditEventTests {
    private let requestId = UUID(uuidString: "6F0C1B6E-1F4A-4A9D-9F3E-1B2C3D4E5F60")!
    private let timestamp = Date(timeIntervalSince1970: 1_785_481_200.221)

    private func event(
        operation: AuditOperation = .remindersCreate,
        list listValue: ListName? = nil,
        result: AuditResult = .ok,
        status: Int = 201,
        replay: Bool = false
    ) -> AuditEvent {
        AuditEvent(
            timestamp: timestamp,
            requestId: requestId,
            tokenId: TokenId(rawValue: "a1b2c3d4"),
            operation: operation,
            list: listValue,
            result: result,
            status: status,
            durationMs: 38,
            replay: replay
        )
    }

    @Test("a line carries exactly the documented keys")
    func lineHasFixedShape() throws {
        let line = try event(list: try listName("Groceries")).jsonLine()
        let parsed = try #require(
            try JSONSerialization.jsonObject(with: Data(line.utf8)) as? [String: Any]
        )

        #expect(Set(parsed.keys) == ["ts", "requestId", "tokenId", "op", "list", "result", "status", "durationMs", "replay"])
        #expect(parsed["ts"] as? String == "2026-07-31T07:00:00.221Z")
        #expect(parsed["requestId"] as? String == "6f0c1b6e-1f4a-4a9d-9f3e-1b2c3d4e5f60")
        #expect(parsed["tokenId"] as? String == "a1b2c3d4")
        #expect(parsed["op"] as? String == "reminders.create")
        #expect(parsed["list"] as? String == "Groceries")
        #expect(parsed["result"] as? String == "ok")
        #expect(parsed["status"] as? Int == 201)
        #expect(parsed["durationMs"] as? Int == 38)
        #expect(parsed["replay"] as? Bool == false)
    }

    @Test("a line is one line — JSONL stays parseable")
    func lineHasNoNewlines() throws {
        let line = try event(list: try listName("Groceries")).jsonLine()
        #expect(!line.contains("\n"))
        #expect(!line.contains("\r"))
    }

    @Test("a rejected request audits its error code")
    func recordsErrorCode() throws {
        let line = try event(operation: .unrouted, result: .error(.unauthorized), status: 401).jsonLine()
        let parsed = try #require(try JSONSerialization.jsonObject(with: Data(line.utf8)) as? [String: Any])
        #expect(parsed["result"] as? String == "unauthorized")
        #expect(parsed["op"] as? String == "unrouted")
        #expect(parsed["status"] as? Int == 401)
    }

    @Test("an unauthenticated request has a null token id, not a fabricated one")
    func nullsAbsentFields() throws {
        let bare = AuditEvent(
            timestamp: timestamp,
            requestId: requestId,
            tokenId: nil,
            operation: .unrouted,
            list: nil,
            result: .error(.unauthorized),
            status: 401,
            durationMs: 1
        )
        let parsed = try #require(
            try JSONSerialization.jsonObject(with: Data(try bare.jsonLine().utf8)) as? [String: Any]
        )
        #expect(parsed["tokenId"] is NSNull)
        #expect(parsed["list"] is NSNull)
    }

    @Test("replays are marked")
    func marksReplay() throws {
        let line = try event(replay: true).jsonLine()
        let parsed = try #require(try JSONSerialization.jsonObject(with: Data(line.utf8)) as? [String: Any])
        #expect(parsed["replay"] as? Bool == true)
    }

    /// The structural claim: an `AuditEvent` has no bare `String` field, so there is nowhere for a
    /// title, a note, a token or a path to be put — accidentally or otherwise. `list` is the one
    /// field carrying anything the user chose, and `ListName` bounds what it can carry.
    @Test("the type has no field that could hold user content")
    func typeCannotHoldUserContent() throws {
        let sample = event(list: try listName("Groceries"))
        let labels = Mirror(reflecting: sample).children.compactMap(\.label)
        #expect(labels == ["timestamp", "requestId", "tokenId", "operation", "list", "result", "status", "durationMs", "replay"])

        for child in Mirror(reflecting: sample).children {
            // `ListName` and `TokenId` are the only string-shaped fields, and both are validated
            // — capped, and with control characters refused. Nothing here is a bare `String`.
            #expect(!(child.value is String), "\(child.label ?? "?") is a free-form String")
        }
    }

    @Test("adversarial titles and notes cannot reach a line")
    func linesCarryNoUserContent() throws {
        let secrets = [
            "Buy milk 🥛",
            "/Users/phil/Notes/1 Inbox/secret.md",
            "erdab_9lQ2xR7vT4pN0aZ8sK1yH6bG3jW5cM2d",
            "\u{202E}gnirts ydeerg",
            String(repeating: "Ω", count: 64),
        ]

        // Everything a request could carry, pushed through the DTOs, then audited.
        var generator = SeededGenerator(seed: 0xA11D_17A5_0000_0001)
        for secret in secrets {
            let request = CreateReminderRequest(
                list: try listName("Groceries"),
                title: secret,
                notes: secret,
                dueAt: Date(timeIntervalSince1970: Double(generator.next(upperBound: 2_000_000_000)))
            )
            let command = request.command(id: BridgeID.generate())
            let line = try AuditEvent(
                timestamp: timestamp,
                requestId: requestId,
                tokenId: TokenId(rawValue: "a1b2c3d4"),
                operation: .remindersCreate,
                list: command.list,
                result: .ok,
                status: 201,
                durationMs: 12
            ).jsonLine()

            #expect(!line.contains(secret), "audit line leaked user content")
            #expect(!line.contains("/Users/"), "audit line leaked a path")
            #expect(!line.contains("erdab_"), "audit line leaked a token")
        }
    }

    @Test("the memory sink collects what it is given")
    func memorySinkCollects() throws {
        let sink = MemoryAuditSink()
        sink.record(event(list: try listName("Groceries")))
        sink.record(event(operation: .remindersList, result: .error(.rateLimited), status: 429))

        #expect(sink.events.count == 2)
        let lines = try sink.lines()
        #expect(lines.count == 2)
        sink.reset()
        #expect(sink.events.isEmpty)
    }

    @Test("every operation has a stable dotted name")
    func operationNames() {
        #expect(Set(AuditOperation.allCases.map(\.rawValue)) == [
            "status.read", "reminders.create", "reminders.list", "reminders.complete",
            "token.rotate", "unrouted",
        ])
    }
}
