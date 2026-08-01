import BridgeCore
import Foundation
import NIOHTTP1

/// One aggregated request, decoupled from the channel so the whole middleware chain can be
/// exercised without a socket.
public struct BridgeRequest: Sendable {
    public var method: HTTPMethod
    public var version: HTTPVersion
    /// The raw request target, exactly as received — never decoded, never normalised.
    public var uri: String
    public var headers: HTTPHeaders
    /// The raw body bytes. Idempotency hashes these, not a re-encoded object.
    public var body: [UInt8]
    public var requestId: UUID

    public init(
        method: HTTPMethod,
        version: HTTPVersion = .http1_1,
        uri: String,
        headers: HTTPHeaders = HTTPHeaders(),
        body: [UInt8] = [],
        requestId: UUID = UUID()
    ) {
        self.method = method
        self.version = version
        self.uri = uri
        self.headers = headers
        self.body = body
        self.requestId = requestId
    }
}

public struct BridgeResponse: Sendable {
    public var status: Int
    public var body: [UInt8]
    /// Anything beyond the four headers every response carries.
    public var extraHeaders: [(name: String, value: String)]

    public init(status: Int, body: [UInt8] = [], extraHeaders: [(name: String, value: String)] = []) {
        self.status = status
        self.body = body
        self.extraHeaders = extraHeaders
    }
}

/// An `ApiError` plus the headers its response needs — `Allow` on a 405, `Retry-After` on a 429.
///
/// The error body itself still carries only a code and a request id; these are protocol headers
/// the client needs in order to behave correctly, not diagnostic text.
struct HTTPFailure: Error {
    let apiError: ApiError
    let extraHeaders: [(name: String, value: String)]

    init(_ apiError: ApiError, extraHeaders: [(name: String, value: String)] = []) {
        self.apiError = apiError
        self.extraHeaders = extraHeaders
    }

    static func methodNotAllowed(allow: [String]) -> HTTPFailure {
        HTTPFailure(.methodNotAllowed, extraHeaders: [(name: "Allow", value: allow.joined(separator: ", "))])
    }

    static func rateLimited(retryAfterSeconds: Int) -> HTTPFailure {
        HTTPFailure(.rateLimited, extraHeaders: [(name: "Retry-After", value: String(retryAfterSeconds))])
    }

    static func remindersUnavailable() -> HTTPFailure {
        HTTPFailure(.remindersUnavailable, extraHeaders: [(name: "Retry-After", value: "60")])
    }
}

/// Mutable bookkeeping for the audit line, filled in as the chain makes progress.
///
/// A reference type so that what the chain learned before it threw — which route it was, which
/// token it authenticated — still reaches the audit sink. It never leaves the task that made it.
final class AuditTrace {
    var operation: AuditOperation = .unrouted
    var tokenId: TokenId?
    var alias: Alias?
    var replay = false
}
