import Foundation
import Testing

@testable import BridgeCore

@Suite("Strict JSON decoding")
struct StrictDecodingTests {
    @Test("a well-formed body decodes, with the title trimmed")
    func decodesValidBody() throws {
        let request = try StrictJSON.decode(
            CreateReminderRequest.self,
            from: json(#"{"alias":"inbox","title":"  Buy milk  ","notes":"2%","dueAt":"2026-07-31T09:00:00+02:00","priority":3}"#)
        )

        #expect(request.alias.rawValue == "inbox")
        #expect(request.title == "Buy milk")
        #expect(request.notes == "2%")
        #expect(request.priority == 3)
        #expect(request.dueAt == Date(timeIntervalSince1970: 1_785_481_200))
    }

    @Test("only alias and title are required")
    func decodesMinimalBody() throws {
        let request = try StrictJSON.decode(
            CreateReminderRequest.self,
            from: json(#"{"alias":"inbox","title":"Buy milk"}"#)
        )

        #expect(request.notes == nil)
        #expect(request.dueAt == nil)
        #expect(request.priority == nil)
    }

    @Test("an unknown key is rejected rather than ignored")
    func rejectsUnknownKey() {
        #expect(throws: ApiError.invalidRequest) {
            try StrictJSON.decode(
                CreateReminderRequest.self,
                from: json(#"{"alias":"inbox","title":"Buy milk","calendarId":"ABC-123"}"#)
            )
        }
    }

    @Test("a near-miss key name is rejected, not silently dropped")
    func rejectsMisspelledKey() {
        #expect(throws: ApiError.invalidRequest) {
            try StrictJSON.decode(
                CreateReminderRequest.self,
                from: json(#"{"alias":"inbox","titel":"Buy milk"}"#)
            )
        }
    }

    @Test("a missing required key is rejected", arguments: [
        #"{"title":"Buy milk"}"#,
        #"{"alias":"inbox"}"#,
        #"{}"#,
    ])
    func rejectsMissingRequiredKey(body: String) {
        #expect(throws: ApiError.invalidRequest) {
            try StrictJSON.decode(CreateReminderRequest.self, from: json(body))
        }
    }

    @Test("a wrong type is rejected", arguments: [
        #"{"alias":"inbox","title":42}"#,
        #"{"alias":7,"title":"Buy milk"}"#,
        #"{"alias":"inbox","title":"Buy milk","priority":"3"}"#,
        #"{"alias":"inbox","title":"Buy milk","priority":1.5}"#,
        #"{"alias":"inbox","title":"Buy milk","notes":["a"]}"#,
        #"{"alias":"inbox","title":"Buy milk","dueAt":12345}"#,
        #"[]"#,
        #"not json at all"#,
    ])
    func rejectsWrongType(body: String) {
        #expect(throws: ApiError.invalidRequest) {
            try StrictJSON.decode(CreateReminderRequest.self, from: json(body))
        }
    }

    @Test("title length is enforced after trimming")
    func enforcesTitleLength() throws {
        let atCap = String(repeating: "a", count: Limits.titleMaxLength)
        let overCap = String(repeating: "a", count: Limits.titleMaxLength + 1)

        let accepted = try StrictJSON.decode(
            CreateReminderRequest.self,
            from: json(#"{"alias":"inbox","title":"\#(atCap)"}"#)
        )
        #expect(accepted.title.count == Limits.titleMaxLength)

        #expect(throws: ApiError.invalidRequest) {
            try StrictJSON.decode(
                CreateReminderRequest.self,
                from: json(#"{"alias":"inbox","title":"\#(overCap)"}"#)
            )
        }
        // Whitespace does not buy extra room, and a whitespace-only title is empty.
        #expect(throws: ApiError.invalidRequest) {
            try StrictJSON.decode(
                CreateReminderRequest.self,
                from: json(#"{"alias":"inbox","title":"   "}"#)
            )
        }
        #expect(throws: ApiError.invalidRequest) {
            try StrictJSON.decode(
                CreateReminderRequest.self,
                from: json(#"{"alias":"inbox","title":""}"#)
            )
        }
    }

    @Test("notes length is enforced")
    func enforcesNotesLength() throws {
        let atCap = String(repeating: "n", count: Limits.notesMaxLength)
        let overCap = String(repeating: "n", count: Limits.notesMaxLength + 1)

        let accepted = try StrictJSON.decode(
            CreateReminderRequest.self,
            from: json(#"{"alias":"inbox","title":"t","notes":"\#(atCap)"}"#)
        )
        #expect(accepted.notes?.count == Limits.notesMaxLength)

        #expect(throws: ApiError.invalidRequest) {
            try StrictJSON.decode(
                CreateReminderRequest.self,
                from: json(#"{"alias":"inbox","title":"t","notes":"\#(overCap)"}"#)
            )
        }
    }

    @Test("priority stays inside EventKit's 0…9", arguments: [-1, 10, 99, Int.max])
    func rejectsPriorityOutsideRange(priority: Int) {
        #expect(throws: ApiError.invalidRequest) {
            try StrictJSON.decode(
                CreateReminderRequest.self,
                from: json(#"{"alias":"inbox","title":"t","priority":\#(priority)}"#)
            )
        }
    }

    @Test("every priority EventKit accepts is accepted", arguments: 0...9)
    func acceptsValidPriority(priority: Int) throws {
        let request = try StrictJSON.decode(
            CreateReminderRequest.self,
            from: json(#"{"alias":"inbox","title":"t","priority":\#(priority)}"#)
        )
        #expect(request.priority == priority)
    }

    @Test("an invalid alias is rejected by the decoder", arguments: [
        "Inbox", "in box", "-inbox", "_inbox", "", "inbox!", "ínbox",
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",  // 33 characters
    ])
    func rejectsInvalidAlias(candidate: String) {
        #expect(throws: ApiError.invalidRequest) {
            try StrictJSON.decode(
                CreateReminderRequest.self,
                from: json(#"{"alias":"\#(candidate)","title":"t"}"#)
            )
        }
    }

    @Test("a timestamp without an explicit offset is rejected", arguments: [
        "2026-07-31T09:00:00",
        "2026-07-31 09:00:00",
        "2026-07-31",
        "2026-07-31T09:00:00.123",
        "31/07/2026 09:00",
        "",
        "Z",
    ])
    func rejectsOffsetlessTimestamp(candidate: String) {
        #expect(throws: ApiError.invalidRequest) {
            try StrictJSON.decode(
                CreateReminderRequest.self,
                from: json(#"{"alias":"inbox","title":"t","dueAt":"\#(candidate)"}"#)
            )
        }
    }

    @Test("every offset spelling ISO-8601 allows is accepted", arguments: [
        "2026-07-31T09:00:00Z",
        "2026-07-31T09:00:00+02:00",
        "2026-07-31T09:00:00-05:00",
        "2026-07-31T09:00:00+0200",
        "2026-07-31T09:00:00.123Z",
        "2026-07-31T09:00:00.123+02:00",
    ])
    func acceptsOffsetBearingTimestamp(candidate: String) throws {
        let request = try StrictJSON.decode(
            CreateReminderRequest.self,
            from: json(#"{"alias":"inbox","title":"t","dueAt":"\#(candidate)"}"#)
        )
        #expect(request.dueAt != nil)
    }

    @Test("an explicit null is treated as absent, not as a bad value")
    func treatsNullAsAbsent() throws {
        let request = try StrictJSON.decode(
            CreateReminderRequest.self,
            from: json(#"{"alias":"inbox","title":"t","notes":null,"dueAt":null,"priority":null}"#)
        )
        #expect(request.notes == nil)
        #expect(request.dueAt == nil)
        #expect(request.priority == nil)
    }

    @Test("responses render dates as UTC with an offset")
    func encodesDatesWithOffset() throws {
        let snapshot = ReminderSnapshot(
            id: try #require(BridgeID(rawValue: "rem_11111111-2222-3333-4444-555555555555")),
            alias: try alias("inbox"),
            title: "Buy milk",
            dueAt: Date(timeIntervalSince1970: 1_785_481_200)
        )
        let text = String(decoding: try ResponseJSON.encode(snapshot), as: UTF8.self)
        #expect(text.contains(#""dueAt":"2026-07-31T07:00:00Z""#))
    }

    @Test("a response round-trips through the strict decoder")
    func responseRoundTrips() throws {
        let snapshot = ReminderSnapshot(
            id: BridgeID.generate(),
            alias: try alias("inbox"),
            title: "Buy milk",
            notes: "2%",
            dueAt: Date(timeIntervalSince1970: 1_785_481_200),
            priority: 5,
            isCompleted: true,
            completedAt: Date(timeIntervalSince1970: 1_785_488_400)
        )
        let decoded = try StrictJSON.decode(ReminderSnapshot.self, from: try ResponseJSON.encode(snapshot))
        #expect(decoded == snapshot)
    }

    @Test("list limits are capped")
    func enforcesListLimit() throws {
        let atCap = try ListRemindersQuery(limit: Limits.listLimitMax)
        #expect(atCap.limit == Limits.listLimitMax)
        let defaulted = try ListRemindersQuery()
        #expect(defaulted.limit == Limits.listLimitDefault)
        #expect(defaulted.aliases.isEmpty)
        #expect(throws: ApiError.invalidRequest) { try ListRemindersQuery(limit: 0) }
        #expect(throws: ApiError.invalidRequest) { try ListRemindersQuery(limit: -1) }
        #expect(throws: ApiError.invalidRequest) { try ListRemindersQuery(limit: Limits.listLimitMax + 1) }
    }

    @Test("idempotency keys are printable ASCII within a length cap")
    func validatesIdempotencyKey() throws {
        let accepted = try Validate.idempotencyKey("6f0c1b6e-1f4a-4a9d-9f3e-1b2c3d4e5f60")
        #expect(accepted == "6f0c1b6e-1f4a-4a9d-9f3e-1b2c3d4e5f60")
        #expect(throws: ApiError.invalidRequest) { try Validate.idempotencyKey("") }
        #expect(throws: ApiError.invalidRequest) { try Validate.idempotencyKey("has space") }
        #expect(throws: ApiError.invalidRequest) { try Validate.idempotencyKey("has\nnewline") }
        #expect(throws: ApiError.invalidRequest) { try Validate.idempotencyKey("émoji") }
        #expect(throws: ApiError.invalidRequest) {
            try Validate.idempotencyKey(String(repeating: "k", count: Limits.idempotencyKeyMaxLength + 1))
        }
    }
}
