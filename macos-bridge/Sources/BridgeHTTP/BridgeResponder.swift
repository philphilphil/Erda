import BridgeCore
import Foundation
import NIOHTTP1

/// The request pipeline from dossier §2.3, in that order:
///
/// ```
/// protocol gate → route → auth → rate limit → content negotiation
///               → strict decode → idempotency → domain → audit
/// ```
///
/// (Connection admission is step 1 and happens a layer up, in `BridgeHTTPServer`, because it is
/// a property of the connection rather than of the request.)
///
/// Cheapest and most categorical rejections come first. **Auth before rate limit is deliberate**:
/// the limiter is keyed on `tokenId`, so it cannot run until there is one, and there is exactly
/// one legitimate client. Unauthenticated flooding is bounded by the steps above, which allocate
/// nothing.
///
/// **Audit runs on every path**, including every rejection — that is why `respond` catches
/// rather than propagating.
public struct BridgeResponder: Sendable {
    private let services: BridgeServices

    public init(services: BridgeServices) {
        self.services = services
    }

    public func respond(to request: BridgeRequest) async -> BridgeResponse {
        let started = services.clock.now
        let trace = AuditTrace()

        let response: BridgeResponse
        let result: AuditResult
        do {
            response = try await handle(request, trace: trace)
            result = .ok
        } catch let failure as HTTPFailure {
            response = errorResponse(failure.apiError, requestId: request.requestId, extraHeaders: failure.extraHeaders)
            result = .error(failure.apiError)
        } catch let error as ApiError {
            response = errorResponse(error, requestId: request.requestId)
            result = .error(error)
        } catch {
            // Anything unexpected collapses to a bare 500. Whatever this error's description
            // says stays on this side of the wire.
            response = errorResponse(.internal, requestId: request.requestId)
            result = .error(.internal)
        }

        services.audit.record(
            AuditEvent(
                timestamp: started,
                requestId: request.requestId,
                tokenId: trace.tokenId,
                operation: trace.operation,
                list: trace.list,
                result: result,
                status: response.status,
                durationMs: Int((services.clock.now.timeIntervalSince(started) * 1000).rounded()),
                replay: trace.replay
            )
        )

        return response
    }

    // MARK: - The chain

    private func handle(_ request: BridgeRequest, trace: AuditTrace) async throws -> BridgeResponse {
        try protocolGate(request)

        let route = try Router.route(method: request.method, uri: request.uri)
        trace.operation = route.auditOperation

        let tokenId = try await authenticate(request)
        trace.tokenId = tokenId

        try await rateLimit(tokenId: tokenId, route: route)
        try contentNegotiation(request, route: route)

        switch route {
        case .status:
            return try await statusResponse()
        case .listReminders(let query):
            return try await listResponse(query)
        case .createReminder:
            return try await withIdempotency(request, trace: trace) {
                try await createResponse(request, trace: trace)
            }
        case .completeReminder(let id):
            return try await withIdempotency(request, trace: trace) {
                try await completeResponse(id: id)
            }
        }
    }

    /// Step 2. HTTP/1.1 only, and no upgrade of any kind — this process speaks exactly one
    /// protocol, and a successful upgrade would hand the socket to code that does not exist.
    private func protocolGate(_ request: BridgeRequest) throws {
        guard request.version == .http1_1 else { throw ApiError.unsupportedHttpVersion }
        guard !request.headers.contains(name: "Upgrade") else { throw ApiError.invalidRequest }

        for value in request.headers[canonicalForm: "Connection"] where value.lowercased() == "upgrade" {
            throw ApiError.invalidRequest
        }
    }

    /// Step 4. Applies to `/v1/status` too: there is no unauthenticated surface at all, so an
    /// unauthenticated scan cannot even learn whether the bridge is healthy.
    private func authenticate(_ request: BridgeRequest) async throws -> TokenId {
        guard let verifier = await services.tokenVerifier() else { throw ApiError.unauthorized }
        guard let presented = TokenVerifier.bearerToken(from: request.headers.first(name: "Authorization")) else {
            throw ApiError.unauthorized
        }
        guard let tokenId = verifier.verify(presentedToken: presented) else { throw ApiError.unauthorized }
        return tokenId
    }

    /// Step 5.
    private func rateLimit(tokenId: TokenId, route: Route) async throws {
        let decision = await services.rateLimiter.admit(tokenId: tokenId, class: route.rateLimitClass)
        guard decision.allowed else {
            throw HTTPFailure.rateLimited(retryAfterSeconds: decision.retryAfterSeconds)
        }
    }

    /// Step 6.
    private func contentNegotiation(_ request: BridgeRequest, route: Route) throws {
        let contentType = request.headers.first(name: "Content-Type")

        switch route {
        case .status, .listReminders:
            // A GET with a body is a client bug worth naming rather than ignoring; ignoring it
            // is how a request gets interpreted differently by two hops.
            guard request.body.isEmpty else { throw ApiError.invalidRequest }

        case .createReminder:
            guard let contentType, Self.isJSON(contentType) else { throw ApiError.unsupportedMediaType }
            guard !request.body.isEmpty else { throw ApiError.invalidRequest }

        case .completeReminder:
            // Completing carries no payload. A `Content-Type` is tolerated (some clients always
            // send one) but must still be JSON; a non-empty body is refused, because whatever it
            // contained would be silently ignored.
            if let contentType, !Self.isJSON(contentType) { throw ApiError.unsupportedMediaType }
            guard request.body.isEmpty else { throw ApiError.invalidRequest }
        }
    }

    /// `application/json`, with `; charset=utf-8` and friends tolerated.
    static func isJSON(_ headerValue: String) -> Bool {
        let mediaType = headerValue.split(separator: ";", maxSplits: 1).first ?? ""
        return mediaType.trimmingCharacters(in: .whitespaces).lowercased() == "application/json"
    }

    /// Step 8. Mutations must carry an `Idempotency-Key`; without one a retry after a timeout
    /// would silently create a second reminder.
    private func withIdempotency(
        _ request: BridgeRequest,
        trace: AuditTrace,
        _ body: () async throws -> BridgeResponse
    ) async throws -> BridgeResponse {
        guard let rawKey = request.headers.first(name: "Idempotency-Key") else {
            throw ApiError.invalidRequest
        }
        let key = try Validate.idempotencyKey(rawKey)
        let digest = services.hasher.sha256(
            RequestHash.preimage(method: request.method.rawValue, path: request.uri, body: request.body)
        )

        let outcome: IdempotencyOutcome
        do {
            outcome = try services.idempotency.claim(key: key, requestHash: digest)
        } catch {
            throw ApiError.internal
        }

        switch outcome {
        case .conflictKeyReuse, .conflictInProgress:
            throw outcome.apiError ?? .internal

        case .replay(let status, let storedBody):
            trace.replay = true
            return BridgeResponse(
                status: status,
                body: storedBody,
                extraHeaders: [(name: "Idempotency-Replayed", value: "true")]
            )

        case .proceed:
            do {
                let response = try await body()
                // A failure to record the outcome must not fail a request that already
                // succeeded; the cost is that a retry re-runs instead of replaying.
                try? services.idempotency.complete(key: key, status: response.status, body: response.body)
                return response
            } catch {
                // Failures are never cached: replaying a 500 for 24 hours would turn one
                // transient hiccup into a day-long outage for that key.
                _ = try? services.idempotency.abandon(key: key)
                throw error
            }
        }
    }

    // MARK: - Step 9: the domain

    private func statusResponse() async throws -> BridgeResponse {
        let payload = StatusResponse(
            availability: await services.reminders.availability(),
            // The names a caller may address. It has to be able to learn them: with no allowlist
            // and no aliases, the name in Reminders.app is the only handle there is.
            lists: await services.reminders.availableLists()
        )
        // Status reports unavailability in its body rather than as a 503: a monitoring client
        // needs to be able to ask "are you well?" and get an answer, not an error.
        return BridgeResponse(status: 200, body: try encode(payload))
    }

    private func listResponse(_ query: ListRemindersQuery) async throws -> BridgeResponse {
        try await requireAvailable()
        let reminders = try await services.reminders.list(query)
        // Wrapped in an object at the HTTP edge only — the service seam still speaks in arrays.
        return BridgeResponse(status: 200, body: try encode(ListRemindersResponse(items: reminders)))
    }

    private func createResponse(_ request: BridgeRequest, trace: AuditTrace) async throws -> BridgeResponse {
        // Step 7: strict decode. Unknown keys, over-length fields, a naive timestamp and a
        // priority outside 0…9 all fail here, before anything reaches EventKit.
        let decoded = try StrictJSON.decode(CreateReminderRequest.self, from: Data(request.body))
        trace.list = decoded.list

        try await requireAvailable()
        let snapshot = try await services.reminders.create(decoded.command(id: BridgeID.generate()))
        return BridgeResponse(status: 201, body: try encode(snapshot))
    }

    private func completeResponse(id: BridgeID) async throws -> BridgeResponse {
        try await requireAvailable()
        let outcome = try await services.reminders.complete(id: id)
        return BridgeResponse(status: 200, body: try encode(outcome))
    }

    /// Revoked access is a 503 with a `Retry-After` — never a 500 and never a stack trace.
    private func requireAvailable() async throws {
        if await services.reminders.availability() != .ok {
            throw HTTPFailure.remindersUnavailable()
        }
    }

    private func encode(_ value: some Encodable) throws -> [UInt8] {
        do {
            return [UInt8](try ResponseJSON.encode(value))
        } catch {
            throw ApiError.internal
        }
    }

    // MARK: - Errors

    private func errorResponse(
        _ error: ApiError,
        requestId: UUID,
        extraHeaders: [(name: String, value: String)] = []
    ) -> BridgeResponse {
        let payload = ApiErrorResponse(error: error, requestId: requestId.uuidString.lowercased())
        // If even this fails to encode there is nothing useful left to say, so the body is empty
        // rather than a second attempt that could fail the same way.
        let body = (try? ResponseJSON.encode(payload)).map { [UInt8]($0) } ?? []
        return BridgeResponse(status: error.httpStatus, body: body, extraHeaders: extraHeaders)
    }
}
