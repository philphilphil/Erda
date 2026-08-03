import BridgeCore
import Foundation
import NIOHTTP1
import Testing

@testable import BridgeHTTP

@Suite("Responder — the §2.3 chain")
struct ResponderTests {
    // MARK: - Happy paths

    @Test("GET /v1/status reports availability and the addressable list names")
    func status() async throws {
        let harness = try TestHarness()
        let response = await harness.responder.respond(to: harness.request(.GET, "/v1/status"))

        #expect(response.status == 200)
        let payload = try harness.json(response)
        #expect(payload["availability"] as? String == "ok")
        #expect(payload["lists"] as? [String] == ["Groceries", "Work"])
    }

    @Test("POST /v1/reminders creates and returns 201 with a bridge id")
    func create() async throws {
        let harness = try TestHarness()
        let response = await harness.responder.respond(
            to: harness.request(.POST, "/v1/reminders", body: createBody)
        )

        #expect(response.status == 201)
        let payload = try harness.json(response)
        #expect((payload["id"] as? String)?.hasPrefix("rem_") == true)
        #expect(payload["title"] as? String == "Buy milk")
        #expect(payload["list"] as? String == "Groceries")
        #expect(payload["isCompleted"] as? Bool == false)
    }

    @Test("GET /v1/reminders lists what was created")
    func list() async throws {
        let harness = try TestHarness()
        _ = await harness.responder.respond(to: harness.request(.POST, "/v1/reminders", body: createBody))

        let response = await harness.responder.respond(to: harness.request(.GET, "/v1/reminders"))
        #expect(response.status == 200)
        #expect(try harness.jsonItems(response).count == 1)
    }

    @Test("POST /v1/reminders/{id}/complete completes, and completing twice is a no-op")
    func complete() async throws {
        let harness = try TestHarness()
        let created = await harness.responder.respond(
            to: harness.request(.POST, "/v1/reminders", body: createBody)
        )
        let id = try #require(try harness.json(created)["id"] as? String)

        let first = await harness.responder.respond(
            to: harness.request(.POST, "/v1/reminders/\(id)/complete", contentType: nil)
        )
        #expect(first.status == 200)
        #expect(try harness.json(first)["alreadyCompleted"] as? Bool == false)

        let second = await harness.responder.respond(
            to: harness.request(.POST, "/v1/reminders/\(id)/complete", contentType: nil)
        )
        #expect(second.status == 200)
        #expect(try harness.json(second)["alreadyCompleted"] as? Bool == true)
    }

    // MARK: - Step 2: protocol gate

    @Test("HTTP/1.0 is 505")
    func rejectsHTTP10() async throws {
        let harness = try TestHarness()
        let response = await harness.responder.respond(
            to: harness.request(.GET, "/v1/status", version: .http1_0)
        )
        #expect(response.status == 505)
        #expect(try harness.errorCode(response) == "unsupported_http_version")
    }

    @Test("an upgrade attempt is 400", arguments: [
        [("Upgrade", "websocket"), ("Connection", "Upgrade")],
        [("Upgrade", "h2c")],
        [("Connection", "keep-alive, Upgrade")],
    ])
    func rejectsUpgrade(headers: [(String, String)]) async throws {
        let harness = try TestHarness()
        let response = await harness.responder.respond(
            to: harness.request(.GET, "/v1/status", extraHeaders: headers)
        )
        #expect(response.status == 400)
        #expect(try harness.errorCode(response) == "invalid_request")
    }

    // MARK: - Step 4: auth

    @Test("every route needs a bearer token, including status", arguments: [
        "/v1/status", "/v1/reminders",
    ])
    func requiresAuth(uri: String) async throws {
        let harness = try TestHarness()
        let response = await harness.responder.respond(
            to: harness.request(.GET, uri, authorized: false)
        )
        #expect(response.status == 401)
        #expect(try harness.errorCode(response) == "unauthorized")
    }

    @Test("a wrong or malformed credential is 401", arguments: [
        "Bearer wrong", "Bearer ", "Basic abc", "abc", "",
    ])
    func rejectsBadCredential(header: String) async throws {
        let harness = try TestHarness()
        let response = await harness.responder.respond(
            to: harness.request(.GET, "/v1/status", authorized: false, extraHeaders: [("Authorization", header)])
        )
        #expect(response.status == 401)
    }

    @Test("with no token generated the bridge refuses everything rather than running open")
    func failsClosedWithoutAToken() async throws {
        let harness = try TestHarness(tokenPresent: false)
        let response = await harness.responder.respond(to: harness.request(.GET, "/v1/status"))
        #expect(response.status == 401)
    }

    @Test("auth runs before the rate limiter, so unauthenticated traffic cannot exhaust a bucket")
    func authPrecedesRateLimit() async throws {
        let harness = try TestHarness(rateLimiterCapacities: (global: 2, mutation: 1))

        for _ in 0..<10 {
            let response = await harness.responder.respond(
                to: harness.request(.GET, "/v1/status", authorized: false)
            )
            #expect(response.status == 401)
        }

        // The budget is untouched, because there was never a token id to charge.
        let authorized = await harness.responder.respond(to: harness.request(.GET, "/v1/status"))
        #expect(authorized.status == 200)
    }

    // MARK: - Step 5: rate limit

    @Test("over the budget is a 429 with Retry-After")
    func rateLimits() async throws {
        let harness = try TestHarness(rateLimiterCapacities: (global: 2, mutation: 1))

        for _ in 0..<2 {
            #expect(await harness.responder.respond(to: harness.request(.GET, "/v1/status")).status == 200)
        }

        let limited = await harness.responder.respond(to: harness.request(.GET, "/v1/status"))
        #expect(limited.status == 429)
        #expect(try harness.errorCode(limited) == "rate_limited")
        #expect(harness.header(limited, "Retry-After") == "30")

        harness.clock.advance(by: 30)
        #expect(await harness.responder.respond(to: harness.request(.GET, "/v1/status")).status == 200)
    }

    @Test("mutations draw on their own, tighter bucket")
    func mutationBucketIsSeparate() async throws {
        let harness = try TestHarness(rateLimiterCapacities: (global: 10, mutation: 1))

        #expect(await harness.responder.respond(
            to: harness.request(.POST, "/v1/reminders", body: createBody)
        ).status == 201)

        let limited = await harness.responder.respond(
            to: harness.request(.POST, "/v1/reminders", body: createBody)
        )
        #expect(limited.status == 429)

        // Reads still work: the rejected mutation did not spend a global token either.
        #expect(await harness.responder.respond(to: harness.request(.GET, "/v1/status")).status == 200)
    }

    // MARK: - Step 6: content negotiation

    @Test("a mutation without application/json is 415", arguments: [
        "text/plain", "application/xml", "application/json-patch+json", "",
    ])
    func rejectsNonJSON(contentType: String) async throws {
        let harness = try TestHarness()
        let response = await harness.responder.respond(
            to: harness.request(.POST, "/v1/reminders", body: createBody, contentType: contentType)
        )
        #expect(response.status == 415)
        #expect(try harness.errorCode(response) == "unsupported_media_type")
    }

    @Test("a charset parameter is tolerated", arguments: [
        "application/json; charset=utf-8", "application/json;charset=UTF-8", "APPLICATION/JSON",
    ])
    func toleratesCharset(contentType: String) async throws {
        let harness = try TestHarness()
        let response = await harness.responder.respond(
            to: harness.request(.POST, "/v1/reminders", body: createBody, contentType: contentType)
        )
        #expect(response.status == 201)
    }

    @Test("a create with no body at all is 415, not a confusing 400")
    func rejectsMissingBody() async throws {
        let harness = try TestHarness()
        let response = await harness.responder.respond(to: harness.request(.POST, "/v1/reminders"))
        #expect(response.status == 415)
    }

    @Test("a GET carrying a body is 400 rather than silently ignored")
    func rejectsBodyOnGet() async throws {
        let harness = try TestHarness()
        let response = await harness.responder.respond(
            to: harness.request(.GET, "/v1/status", body: createBody)
        )
        #expect(response.status == 400)
    }

    @Test("complete refuses a body, whose contents would otherwise be ignored")
    func completeRefusesABody() async throws {
        let harness = try TestHarness()
        let response = await harness.responder.respond(
            to: harness.request(.POST, "/v1/reminders/\(BridgeID.generate().rawValue)/complete", body: "{}")
        )
        #expect(response.status == 400)
    }

    // MARK: - Step 7: strict decode

    @Test("the body is decoded strictly", arguments: [
        #"{"title":"t","calendarId":"ABC"}"#,                // unknown key
        #"{}"#,                                              // missing title
        #"{"title":42}"#,                                    // wrong type
        #"{"list":"Groceries","title":"t"}"#,                // a list key, which this route no longer takes
        #"{"title":"t","priority":10}"#,                     // priority out of range
        #"{"title":"t","dueAt":"2026-07-31T09:00:00"}"#,     // no offset
        #"not json"#,
    ])
    func decodesStrictly(body: String) async throws {
        let harness = try TestHarness()
        let response = await harness.responder.respond(
            to: harness.request(.POST, "/v1/reminders", body: body)
        )
        #expect(response.status == 400)
        #expect(try harness.errorCode(response) == "invalid_request")
    }

    // MARK: - Step 8: idempotency

    @Test("a mutation without an Idempotency-Key is 400")
    func requiresIdempotencyKey() async throws {
        let harness = try TestHarness()
        let response = await harness.responder.respond(
            to: harness.request(.POST, "/v1/reminders", body: createBody, idempotencyKey: nil)
        )
        #expect(response.status == 400)
    }

    @Test("the same key and body replays the stored response byte for byte")
    func replays() async throws {
        let harness = try TestHarness()
        let key = "retry-me"

        let first = await harness.responder.respond(
            to: harness.request(.POST, "/v1/reminders", body: createBody, idempotencyKey: key)
        )
        let second = await harness.responder.respond(
            to: harness.request(.POST, "/v1/reminders", body: createBody, idempotencyKey: key)
        )

        #expect(first.status == 201)
        #expect(second.status == 201)
        #expect(second.body == first.body)
        #expect(harness.header(second, "Idempotency-Replayed") == "true")

        // Exactly one reminder exists — which is the entire point.
        let listed = await harness.responder.respond(to: harness.request(.GET, "/v1/reminders"))
        #expect(try harness.jsonItems(listed).count == 1)
    }

    @Test("the same key with a different body is 409 idempotency_key_reuse")
    func rejectsKeyReuse() async throws {
        let harness = try TestHarness()
        let key = "reused"

        _ = await harness.responder.respond(
            to: harness.request(.POST, "/v1/reminders", body: createBody, idempotencyKey: key)
        )
        let response = await harness.responder.respond(
            to: harness.request(
                .POST,
                "/v1/reminders",
                body: #"{"title":"Something else"}"#,
                idempotencyKey: key
            )
        )
        #expect(response.status == 409)
        #expect(try harness.errorCode(response) == "idempotency_key_reuse")
    }

    @Test("a failed handler releases the key so the retry can proceed")
    func failureFreesTheKey() async throws {
        let harness = try TestHarness()
        let key = "will-fail"
        await harness.reminders.setForcedError(.internal)

        let failed = await harness.responder.respond(
            to: harness.request(.POST, "/v1/reminders", body: createBody, idempotencyKey: key)
        )
        #expect(failed.status == 500)
        #expect(harness.idempotency.record(for: key) == nil)

        await harness.reminders.setForcedError(nil)
        let retried = await harness.responder.respond(
            to: harness.request(.POST, "/v1/reminders", body: createBody, idempotencyKey: key)
        )
        #expect(retried.status == 201)
    }

    @Test("a malformed idempotency key is 400", arguments: ["", "has space", "has\nnewline"])
    func rejectsMalformedKey(key: String) async throws {
        let harness = try TestHarness()
        let response = await harness.responder.respond(
            to: harness.request(.POST, "/v1/reminders", body: createBody, idempotencyKey: key)
        )
        #expect(response.status == 400)
    }

    // MARK: - Step 9: the domain, failing closed

    @Test("with no list pinned, a create is a 503 that names the fix, never a default")
    func unpinnedCreateFailsClosed() async throws {
        let harness = try TestHarness(writeList: nil)
        let response = await harness.responder.respond(
            to: harness.request(.POST, "/v1/reminders", body: createBody)
        )
        #expect(response.status == 503)
        #expect(try harness.errorCode(response) == "list_not_configured")

        // Nothing was written into the lists that do exist.
        let listed = await harness.responder.respond(to: harness.request(.GET, "/v1/reminders"))
        #expect(try harness.jsonItems(listed).isEmpty)
    }

    @Test("a read-only pinned list is a 409 that names the conflict")
    func readOnlyListIs409() async throws {
        let harness = try TestHarness(
            lists: ["Groceries", "Shared"],
            writeList: "Shared",
            readOnly: ["Shared"]
        )
        let response = await harness.responder.respond(
            to: harness.request(.POST, "/v1/reminders", body: createBody)
        )
        #expect(response.status == 409)
        #expect(try harness.errorCode(response) == "list_read_only")
    }

    /// Filtering by a name that matches nothing must fail rather than quietly widening to
    /// everything — which is what an empty filter means now.
    @Test("listing a name that matches no list is refused, not silently widened")
    func unknownListOnListFailsClosed() async throws {
        let harness = try TestHarness()
        _ = await harness.responder.respond(to: harness.request(.POST, "/v1/reminders", body: createBody))

        let response = await harness.responder.respond(
            to: harness.request(.GET, "/v1/reminders?list=Personal")
        )
        #expect(response.status == 404)
        #expect(try harness.errorCode(response) == "no_such_list")
    }

    @Test("an unknown reminder id is 404")
    func unknownIdIs404() async throws {
        let harness = try TestHarness()
        let response = await harness.responder.respond(
            to: harness.request(
                .POST,
                "/v1/reminders/\(BridgeID.generate().rawValue)/complete",
                contentType: nil
            )
        )
        #expect(response.status == 404)
    }

    @Test("revoked access is 503 with Retry-After, never a 500")
    func unavailableIs503() async throws {
        let harness = try TestHarness()
        await harness.reminders.setAvailability(.unauthorized)

        let response = await harness.responder.respond(to: harness.request(.GET, "/v1/reminders"))
        #expect(response.status == 503)
        #expect(try harness.errorCode(response) == "reminders_unavailable")
        #expect(harness.header(response, "Retry-After") == "60")

        // Status still answers, so a monitor can see *why* rather than getting an error.
        let status = await harness.responder.respond(to: harness.request(.GET, "/v1/status"))
        #expect(status.status == 200)
        #expect(try harness.json(status)["availability"] as? String == "unauthorized")
        // …and it says so with an empty list of names, rather than pretending it still knows any.
        #expect(try #require(try harness.json(status)["lists"] as? [Any]).isEmpty)
    }

    // MARK: - Error shape

    @Test("no response body ever carries a message field")
    func errorsCarryNoMessage() async throws {
        let harness = try TestHarness()
        let requests: [BridgeRequest] = [
            harness.request(.GET, "/v1/nope"),
            harness.request(.GET, "/v1/status", authorized: false),
            harness.request(.POST, "/v1/status"),
            harness.request(.GET, "/v1/status", version: .http1_0),
            harness.request(.POST, "/v1/reminders", body: "not json"),
            harness.request(.POST, "/v1/reminders", body: createBody, contentType: "text/plain"),
        ]

        for request in requests {
            let response = await harness.responder.respond(to: request)
            #expect(response.status >= 400)
            let payload = try harness.json(response)
            #expect(Set(payload.keys) == ["error", "requestId"], "unexpected keys \(payload.keys)")
            #expect(ApiError(rawValue: try #require(payload["error"] as? String)) != nil)
        }
    }

    @Test("an internal failure is a bare 500 that says nothing about itself")
    func internalErrorsAreOpaque() async throws {
        let harness = try TestHarness()
        await harness.reminders.setForcedError(.internal)

        let response = await harness.responder.respond(to: harness.request(.GET, "/v1/reminders"))
        #expect(response.status == 500)
        #expect(try harness.errorCode(response) == "internal")
    }
}
