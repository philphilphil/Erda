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
    case aliasUnknown = "alias_unknown"
    case aliasBroken = "alias_broken"
    case remindersUnavailable = "reminders_unavailable"
    case `internal` = "internal"

    /// The stable snake_case code that goes on the wire.
    public var code: String { rawValue }

    public var httpStatus: Int {
        switch self {
        case .invalidRequest: 400
        // An unknown alias is a bad *field value* in a request the client authored, not a
        // missing resource — 404 would additionally imply the URL was wrong.
        case .aliasUnknown: 400
        case .unauthorized: 401
        case .notFound: 404
        case .methodNotAllowed: 405
        case .idempotencyKeyReuse, .requestInProgress, .aliasBroken: 409
        case .payloadTooLarge: 413
        case .unsupportedMediaType: 415
        case .rateLimited: 429
        case .internal: 500
        case .remindersUnavailable: 503
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
