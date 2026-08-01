import Foundation

/// The operations the audit log knows about. An enum, not a string, so the log can never gain a
/// field that a caller might fill with something else.
public enum AuditOperation: String, Sendable, Hashable, CaseIterable, Codable {
    case statusRead = "status.read"
    case remindersCreate = "reminders.create"
    case remindersList = "reminders.list"
    case remindersComplete = "reminders.complete"
    case calendarCreate = "calendar.create"
    case calendarList = "calendar.list"
    case tokenRotate = "token.rotate"
    /// A request rejected before it reached a route (bad version, unknown path, no credential).
    case unrouted = "unrouted"
}

/// Success, or the closed-set error code the request ended with.
public enum AuditResult: Sendable, Hashable {
    case ok
    case error(ApiError)

    public var code: String {
        switch self {
        case .ok: "ok"
        case .error(let apiError): apiError.code
        }
    }
}

/// One line of the audit log.
///
/// Every field is either an enum, an integer, a bool, a UUID, a `TokenId` (8 hex characters), a
/// `ListName` (a Reminders list title) or a `CalendarName` (a Calendar.app title) — the last two
/// both capped and with control characters refused. **There is no bare `String` property**, so a
/// title, a note, an event time, a file path, a token or a raw idempotency key has nowhere to go:
/// redaction is a property of the type, not of the discipline of whoever writes the call site.
///
/// `list` and `calendar` are the only fields carrying anything the user chose, and they carry the
/// *name of a collection*, never the contents of a reminder or an event. **No event title, note or
/// time is ever logged** — a calendar is the whole of what a calendar operation records, exactly as
/// a list is for a reminder one. Since the allowlist went away, those names are no longer drawn
/// from a short local table — which is why both types cap their length and refuse control
/// characters, so a line stays one line and stays greppable.
public struct AuditEvent: Sendable, Equatable {
    public let timestamp: Date
    public let requestId: UUID
    /// `nil` when the request never authenticated.
    public let tokenId: TokenId?
    public let operation: AuditOperation
    /// `nil` when the request named no list.
    public let list: ListName?
    /// `nil` when the request named no calendar. A separate field rather than a shared one,
    /// because a line that said `list: "Privat"` for a calendar operation would be actively
    /// misleading when both a list and a calendar of that name exist.
    public let calendar: CalendarName?
    public let result: AuditResult
    public let status: Int
    public let durationMs: Int
    public let replay: Bool

    public init(
        timestamp: Date,
        requestId: UUID,
        tokenId: TokenId?,
        operation: AuditOperation,
        list: ListName?,
        calendar: CalendarName? = nil,
        result: AuditResult,
        status: Int,
        durationMs: Int,
        replay: Bool = false
    ) {
        self.timestamp = timestamp
        self.requestId = requestId
        self.tokenId = tokenId
        self.operation = operation
        self.list = list
        self.calendar = calendar
        self.result = result
        self.status = status
        self.durationMs = durationMs
        self.replay = replay
    }

    /// One JSONL record, without the trailing newline — the sink appends that.
    public func jsonLine() throws -> String {
        // `JSONEncoder` is a non-`Sendable` class, so it is built per call rather than shared;
        // at one line per request that is free.
        let encoder = JSONEncoder()
        // Deterministic key order, and no `\/` escaping, so the file diffs and greps cleanly.
        encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
        let data = try encoder.encode(Wire(self))
        guard let line = String(data: data, encoding: .utf8) else { throw AuditEncodingError.notUTF8 }
        return line
    }

    /// The on-disk shape (design dossier §4.2). Kept private so the only way to produce a line is
    /// through `AuditEvent`.
    private struct Wire: Encodable {
        let ts: String
        let requestId: String
        let tokenId: String?
        let op: String
        let list: String?
        let calendar: String?
        let result: String
        let status: Int
        let durationMs: Int
        let replay: Bool

        init(_ event: AuditEvent) {
            self.ts = ISO8601.millisecondString(from: event.timestamp)
            self.requestId = event.requestId.uuidString.lowercased()
            self.tokenId = event.tokenId?.rawValue
            self.op = event.operation.rawValue
            self.list = event.list?.rawValue
            self.calendar = event.calendar?.rawValue
            self.result = event.result.code
            self.status = event.status
            self.durationMs = event.durationMs
            self.replay = event.replay
        }

        enum CodingKeys: String, CodingKey {
            case ts, requestId, tokenId, op, list, calendar, result, status, durationMs, replay
        }

        /// Written by hand so the optional fields emit an explicit `null` instead of vanishing.
        /// `encodeIfPresent` — what the synthesised conformance uses — would give lines with
        /// different key sets depending on whether a request authenticated, which is exactly the
        /// kind of shape drift that makes an audit log awkward to read under pressure.
        func encode(to encoder: any Encoder) throws {
            var container = encoder.container(keyedBy: CodingKeys.self)
            try container.encode(ts, forKey: .ts)
            try container.encode(requestId, forKey: .requestId)
            try container.encode(tokenId, forKey: .tokenId)
            try container.encode(op, forKey: .op)
            try container.encode(list, forKey: .list)
            try container.encode(calendar, forKey: .calendar)
            try container.encode(result, forKey: .result)
            try container.encode(status, forKey: .status)
            try container.encode(durationMs, forKey: .durationMs)
            try container.encode(replay, forKey: .replay)
        }
    }
}

public enum AuditEncodingError: Error, Equatable, Sendable {
    case notUTF8
}
