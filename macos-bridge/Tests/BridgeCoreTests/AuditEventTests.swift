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
        calendar calendarValue: CalendarName? = nil,
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
            calendar: calendarValue,
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

        #expect(Set(parsed.keys) == [
            "ts", "requestId", "tokenId", "op", "list", "calendar", "result", "status",
            "durationMs", "replay",
        ])
        #expect(parsed["ts"] as? String == "2026-07-31T07:00:00.221Z")
        #expect(parsed["requestId"] as? String == "6f0c1b6e-1f4a-4a9d-9f3e-1b2c3d4e5f60")
        #expect(parsed["tokenId"] as? String == "a1b2c3d4")
        #expect(parsed["op"] as? String == "reminders.create")
        #expect(parsed["list"] as? String == "Groceries")
        #expect(parsed["calendar"] is NSNull)
        #expect(parsed["result"] as? String == "ok")
        #expect(parsed["status"] as? Int == 201)
        #expect(parsed["durationMs"] as? Int == 38)
        #expect(parsed["replay"] as? Bool == false)
    }

    /// A calendar operation records the calendar and **nothing else about the event** — no title,
    /// no notes, no start or end time. The type has nowhere to put them, which is the point.
    @Test("a calendar operation records the calendar name and nothing else")
    func calendarLineShape() throws {
        let line = try event(
            operation: .calendarCreate,
            calendar: try calendarName("Privat")
        ).jsonLine()
        let parsed = try #require(
            try JSONSerialization.jsonObject(with: Data(line.utf8)) as? [String: Any]
        )

        #expect(parsed["op"] as? String == "calendar.create")
        #expect(parsed["calendar"] as? String == "Privat")
        // The list field stays null: a calendar is not a list, and reporting one as the other
        // would be actively misleading on a Mac holding both a "Privat" list and calendar.
        #expect(parsed["list"] is NSNull)
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
            calendar: nil,
            result: .error(.unauthorized),
            status: 401,
            durationMs: 1
        )
        let parsed = try #require(
            try JSONSerialization.jsonObject(with: Data(try bare.jsonLine().utf8)) as? [String: Any]
        )
        #expect(parsed["tokenId"] is NSNull)
        #expect(parsed["list"] is NSNull)
        #expect(parsed["calendar"] is NSNull)
    }

    @Test("replays are marked")
    func marksReplay() throws {
        let line = try event(replay: true).jsonLine()
        let parsed = try #require(try JSONSerialization.jsonObject(with: Data(line.utf8)) as? [String: Any])
        #expect(parsed["replay"] as? Bool == true)
    }

    /// The structural claim: an `AuditEvent` has no bare `String` field, so there is nowhere for a
    /// title, a note, an event time, a token or a path to be put — accidentally or otherwise.
    /// `list` and `calendar` are the only fields carrying anything the user chose, and both types
    /// bound what they can carry.
    @Test("the type has no field that could hold user content")
    func typeCannotHoldUserContent() throws {
        let sample = event(list: try listName("Groceries"), calendar: try calendarName("Privat"))
        let labels = Mirror(reflecting: sample).children.compactMap(\.label)
        #expect(labels == [
            "timestamp", "requestId", "tokenId", "operation", "list", "calendar", "result",
            "status", "durationMs", "replay",
        ])

        for child in Mirror(reflecting: sample).children {
            // `ListName`, `CalendarName` and `TokenId` are the only string-shaped fields, and all
            // are validated — capped, and with control characters refused. Nothing here is a bare
            // `String`.
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
                title: secret,
                notes: secret,
                dueAt: Date(timeIntervalSince1970: Double(generator.next(upperBound: 2_000_000_000)))
            )
            let command = request.command(id: BridgeID.generate())
            // The command names no list — the write target is pinned locally — so the name on the
            // line comes from the reminder that was actually created, exactly as the responder takes
            // it. Everything else the command carries still has to vanish.
            let created = ReminderSnapshot(
                id: command.id,
                list: try listName("Groceries"),
                title: command.title,
                notes: command.notes,
                dueAt: command.dueAt,
                priority: command.priority
            )
            let line = try AuditEvent(
                timestamp: timestamp,
                requestId: requestId,
                tokenId: TokenId(rawValue: "a1b2c3d4"),
                operation: .remindersCreate,
                list: created.list,
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
            "calendar.create", "calendar.list", "token.rotate", "unrouted",
        ])
    }

    /// Everything a calendar request could carry, pushed through the DTOs and then audited. The
    /// title, the notes, the two timestamps and the zone all have to vanish; only the calendar
    /// name survives.
    @Test("adversarial event titles, notes and times cannot reach a line")
    func calendarLinesCarryNoUserContent() throws {
        let secrets = [
            "Dentist 🦷",
            "/Users/phil/Notes/1 Inbox/secret.md",
            "erdab_9lQ2xR7vT4pN0aZ8sK1yH6bG3jW5cM2d",
            "\u{202E}gnirts ydeerg",
            String(repeating: "Ω", count: 64),
        ]
        let start = Date(timeIntervalSince1970: 1_785_481_200)
        let end = start.addingTimeInterval(3600)

        for secret in secrets {
            let request = CreateCalendarEventRequest(
                title: secret,
                notes: secret,
                startAt: start,
                endAt: end,
                timeZone: TimeZone(identifier: "Europe/Berlin")
            )
            let command = request.command(defaultTimeZone: TimeZone(secondsFromGMT: 0)!)
            // The command names no calendar — the write target is pinned locally — so the name on
            // the line comes from the event that was actually created, exactly as the responder
            // takes it. Everything else the command carries still has to vanish.
            let created = CalendarEventSnapshot(
                calendar: try calendarName("Privat"),
                title: command.title,
                notes: command.notes,
                startAt: command.startAt,
                endAt: command.endAt,
                isAllDay: false,
                timeZone: command.timeZone.identifier
            )
            let line = try AuditEvent(
                timestamp: timestamp,
                requestId: requestId,
                tokenId: TokenId(rawValue: "a1b2c3d4"),
                operation: .calendarCreate,
                list: nil,
                calendar: created.calendar,
                result: .ok,
                status: 201,
                durationMs: 12
            ).jsonLine()

            #expect(!line.contains(secret), "audit line leaked user content")
            #expect(!line.contains("/Users/"), "audit line leaked a path")
            #expect(!line.contains("erdab_"), "audit line leaked a token")
            // The times are the specific thing the threat model names: an audit log that recorded
            // when Phil's appointments are would be a movement log.
            #expect(!line.contains("2026-07-31T15:00"), "audit line leaked an event time")
            #expect(!line.contains("Europe/Berlin"), "audit line leaked an event's time zone")
        }
    }
}
