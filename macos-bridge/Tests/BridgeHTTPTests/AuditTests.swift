import BridgeCore
import Foundation
import NIOHTTP1
import Testing

@testable import BridgeHTTP

@Suite("Auditing")
struct AuditTests {
    @Test("a successful request is audited with its route and token")
    func auditsSuccess() async throws {
        let harness = try TestHarness()
        _ = await harness.responder.respond(to: harness.request(.POST, "/v1/reminders", body: createBody))

        let event = try #require(harness.audit.events.first)
        #expect(event.operation == .remindersCreate)
        #expect(event.result == .ok)
        #expect(event.status == 201)
        #expect(event.list == (try listName("Groceries")))
        #expect(event.tokenId != nil)
        #expect(event.replay == false)
    }

    /// Dossier §2.3 step 10: audit always runs, including on rejection at any earlier step.
    @Test("every rejection is audited too, whichever step rejected it")
    func auditsEveryRejection() async throws {
        let harness = try TestHarness()

        let cases: [(BridgeRequest, ApiError, AuditOperation)] = [
            // Step 2, before there is even a route.
            (harness.request(.GET, "/v1/status", version: .http1_0), .unsupportedHttpVersion, .unrouted),
            // Step 3.
            (harness.request(.GET, "/v1/nope"), .notFound, .unrouted),
            (harness.request(.PUT, "/v1/reminders"), .methodNotAllowed, .unrouted),
            // Step 4 — routed, so the operation is known even though it never ran.
            (harness.request(.GET, "/v1/status", authorized: false), .unauthorized, .statusRead),
            // Step 6.
            (harness.request(.POST, "/v1/reminders", body: createBody, contentType: "text/plain"),
             .unsupportedMediaType, .remindersCreate),
            // Step 7.
            (harness.request(.POST, "/v1/reminders", body: "not json"), .invalidRequest, .remindersCreate),
        ]

        for (request, expectedError, expectedOperation) in cases {
            harness.audit.reset()
            let response = await harness.responder.respond(to: request)

            let event = try #require(harness.audit.events.first, "nothing audited for \(request.uri)")
            #expect(harness.audit.events.count == 1)
            #expect(event.result == .error(expectedError))
            #expect(event.operation == expectedOperation)
            #expect(event.status == response.status)
            #expect(event.status == expectedError.httpStatus)
        }
    }

    @Test("an unauthenticated request audits a null token id rather than inventing one")
    func auditsMissingToken() async throws {
        let harness = try TestHarness()
        _ = await harness.responder.respond(to: harness.request(.GET, "/v1/status", authorized: false))

        let event = try #require(harness.audit.events.first)
        #expect(event.tokenId == nil)

        let parsed = try #require(
            try JSONSerialization.jsonObject(with: Data(try event.jsonLine().utf8)) as? [String: Any]
        )
        #expect(parsed["tokenId"] is NSNull)
    }

    @Test("a replay is marked as one")
    func auditsReplay() async throws {
        let harness = try TestHarness()
        let key = "replayed"
        _ = await harness.responder.respond(
            to: harness.request(.POST, "/v1/reminders", body: createBody, idempotencyKey: key)
        )
        harness.audit.reset()
        _ = await harness.responder.respond(
            to: harness.request(.POST, "/v1/reminders", body: createBody, idempotencyKey: key)
        )

        #expect(try #require(harness.audit.events.first).replay)
    }

    @Test("exactly one line per request")
    func onePerRequest() async throws {
        let harness = try TestHarness()
        for _ in 0..<5 {
            _ = await harness.responder.respond(to: harness.request(.GET, "/v1/status"))
        }
        #expect(harness.audit.events.count == 5)
    }

    /// Dossier §7.2's last row. `AuditEvent` has no bare `String` field — the only thing it takes
    /// from a request is the *list name*, which is bounded by `ListName` — so this is guarding a
    /// structural guarantee rather than hunting for a leak, which is why it is cheap to run over a
    /// lot of adversarial input.
    @Test("no title, note, path or token ever reaches the audit log")
    func linesCarryNoUserContent() async throws {
        let harness = try TestHarness()
        var generator = SeededGenerator(seed: 0x00D1_7000_0000_0001)

        var secrets: [String] = [
            "/Users/phil/Notes/1 Inbox/secret.md",
            harness.token,
            "erdab_9lQ2xR7vT4pN0aZ8sK1yH6bG3jW5cM2d",
            "\u{202E}gnirts ydeerg",
            "'; DROP TABLE reminder_map; --",
        ]
        for _ in 0..<60 {
            secrets.append(generator.unicodeString(maxLength: 40))
        }

        for secret in secrets {
            harness.audit.reset()
            // Refill both rate-limit buckets, so every draw actually reaches the create handler
            // instead of being turned away as a 429 with no title to leak in the first place.
            harness.clock.advance(by: 60)

            let title = String(secret.prefix(Limits.titleMaxLength)).trimmingCharacters(in: .whitespacesAndNewlines)
            guard !title.isEmpty else { continue }

            let body = try String(
                decoding: JSONSerialization.data(
                    withJSONObject: ["title": title, "notes": secret]
                ),
                as: UTF8.self
            )
            let response = await harness.responder.respond(
                to: harness.request(.POST, "/v1/reminders", body: body)
            )
            // The response is allowed to echo the title back; the audit log is not.
            #expect(response.status == 201 || response.status == 400)

            for line in try harness.audit.lines() {
                #expect(!line.contains(secret), "audit line leaked user content")
                #expect(!line.contains(title), "audit line leaked a title")
                #expect(!line.contains("/Users/"), "audit line leaked a path")
                #expect(!line.contains("erdab_"), "audit line leaked a token")
                #expect(!line.contains("DROP TABLE"), "audit line leaked a body")
            }
        }
    }
}

/// Deterministic, so a failure reproduces instead of appearing once a week.
struct SeededGenerator: RandomNumberGenerator {
    private var state: UInt64

    init(seed: UInt64) {
        self.state = seed == 0 ? 0x9E37_79B9_7F4A_7C15 : seed
    }

    mutating func next() -> UInt64 {
        state ^= state >> 12
        state ^= state << 25
        state ^= state >> 27
        return state &* 2_685_821_657_736_338_717
    }

    mutating func next(upperBound: UInt64) -> UInt64 {
        upperBound == 0 ? 0 : next() % upperBound
    }

    /// Draws from ranges that are awkward on purpose: combining marks, bidi controls, emoji,
    /// CJK, quotes and backslashes.
    mutating func unicodeString(maxLength: Int) -> String {
        let alphabets: [ClosedRange<UInt32>] = [
            0x20...0x7E,        // printable ASCII, including quotes and backslashes
            0x00A1...0x024F,    // Latin-1 supplement and extended
            0x0300...0x036F,    // combining marks
            0x0590...0x05FF,    // Hebrew (right-to-left)
            0x4E00...0x4EFF,    // CJK
            0x1F300...0x1F5FF,  // emoji
        ]
        let length = 1 + Int(next(upperBound: UInt64(max(1, maxLength))))
        var text = ""
        for _ in 0..<length {
            let alphabet = alphabets[Int(next(upperBound: UInt64(alphabets.count)))]
            let value = alphabet.lowerBound + UInt32(next(upperBound: UInt64(alphabet.count)))
            if let scalar = Unicode.Scalar(value) { text.unicodeScalars.append(scalar) }
        }
        return text
    }
}
