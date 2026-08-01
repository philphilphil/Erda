import Foundation

/// The **closed** set of error codes the bridge is allowed to emit (design dossier §2.3).
///
/// Every code maps to exactly one HTTP status. Several codes deliberately share a status
/// (three distinct 409s), which is fine — the invariant is that `code -> status` is a function,
/// so a reader of the wire format can never see the same code with two meanings.
public enum ApiError: String, Error, Sendable, Hashable, CaseIterable, Codable {
    case invalidRequest = "invalid_request"
    case unauthorized = "unauthorized"
    case notFound = "not_found"
    case methodNotAllowed = "method_not_allowed"
    case unsupportedMediaType = "unsupported_media_type"
    case unsupportedHttpVersion = "unsupported_http_version"
    case payloadTooLarge = "payload_too_large"
    case rateLimited = "rate_limited"
    case idempotencyKeyReuse = "idempotency_key_reuse"
    case requestInProgress = "request_in_progress"
    case noSuchList = "no_such_list"
    case listReadOnly = "list_read_only"
    case remindersUnavailable = "reminders_unavailable"
    case noSuchCalendar = "no_such_calendar"
    case ambiguousCalendar = "ambiguous_calendar"
    case calendarReadOnly = "calendar_read_only"
    case calendarUnavailable = "calendar_unavailable"
    case `internal` = "internal"

    /// The stable snake_case code that goes on the wire.
    public var code: String { rawValue }

    public var httpStatus: Int {
        switch self {
        case .invalidRequest: 400
        case .unauthorized: 401
        // The named list is a resource, and it is not there. Note this covers two cases: no list
        // matches the name, *and* more than one does (two accounts can both hold a list called
        // "Reminders"). Picking one of an ambiguous pair would be a guess, and a guess here writes
        // into the wrong list — so both answer the same way.
        //
        // `noSuchCalendar` deliberately does *not* fold ambiguity in the same way; see
        // `ambiguousCalendar` below.
        case .notFound, .noSuchList, .noSuchCalendar: 404
        case .methodNotAllowed: 405
        // The list exists but cannot take the reminder — read-only, or an account that does not
        // do reminders at all. A conflict with the state of the resource, not a bad request.
        //
        // A name matching *two* calendars is the same kind of conflict: the resource is
        // over-determined rather than absent, and the fix is to rename one of them. Calendars
        // split what lists fold because the two failures need different advice on the Erda side —
        // "check the name" versus "you have two calendars called that" — and a single code cannot
        // carry both.
        case .idempotencyKeyReuse, .requestInProgress, .listReadOnly,
             .ambiguousCalendar, .calendarReadOnly: 409
        case .payloadTooLarge: 413
        case .unsupportedMediaType: 415
        case .rateLimited: 429
        case .internal: 500
        case .remindersUnavailable, .calendarUnavailable: 503
        case .unsupportedHttpVersion: 505
        }
    }
}

/// The **entire** error response body. There is deliberately no `message` field: it is
/// structurally impossible for a path, an `NSError.localizedDescription` or any other
/// internal detail to reach the client. Detail goes to the local log, correlated by `requestId`.
public struct ApiErrorResponse: Sendable, Equatable, Codable {
    public let error: ApiError
    public let requestId: String

    public init(error: ApiError, requestId: String) {
        self.error = error
        self.requestId = requestId
    }
}
