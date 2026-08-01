import Foundation
import Testing

@testable import BridgeCore

@Suite("Idempotency decision")
struct IdempotencyTests {
    private let key = "6f0c1b6e-1f4a-4a9d-9f3e-1b2c3d4e5f60"
    private let hash: [UInt8] = (0..<32).map { UInt8($0) }
    private let otherHash: [UInt8] = (0..<32).map { UInt8(31 - $0) }
    private let now = Date(timeIntervalSince1970: 1_780_000_000)

    private func record(hash: [UInt8], status: Int?, body: [UInt8]?, age: TimeInterval = 0) -> IdempotencyRecord {
        IdempotencyRecord(
            key: key,
            requestHash: hash,
            status: status,
            responseBody: body,
            createdAt: now.addingTimeInterval(-age)
        )
    }

    @Test("an unclaimed key proceeds")
    func newKeyProceeds() {
        #expect(Idempotency.decide(stored: nil, requestHash: hash) == .proceed)
    }

    @Test("the same key and body after completion replays the stored response")
    func replaysCompletedRequest() {
        let body = Array(#"{"id":"rem_x"}"#.utf8)
        let outcome = Idempotency.decide(
            stored: record(hash: hash, status: 201, body: body),
            requestHash: hash
        )
        #expect(outcome == .replay(status: 201, body: body))
        #expect(outcome.apiError == nil)
    }

    @Test("a completed request with no body replays as an empty body, not as in-flight")
    func replaysEmptyBody() {
        #expect(
            Idempotency.decide(stored: record(hash: hash, status: 204, body: nil), requestHash: hash)
                == .replay(status: 204, body: [])
        )
    }

    @Test("the same key and body while still running is a 409 request_in_progress")
    func rejectsConcurrentRetry() {
        let outcome = Idempotency.decide(
            stored: record(hash: hash, status: nil, body: nil),
            requestHash: hash
        )
        #expect(outcome == .conflictInProgress)
        #expect(outcome.apiError == .requestInProgress)
        #expect(outcome.apiError?.httpStatus == 409)
    }

    @Test("the same key with a different body is a 409 idempotency_key_reuse")
    func rejectsKeyReuse() {
        let outcome = Idempotency.decide(
            stored: record(hash: hash, status: 201, body: Array("x".utf8)),
            requestHash: otherHash
        )
        #expect(outcome == .conflictKeyReuse)
        #expect(outcome.apiError == .idempotencyKeyReuse)
        #expect(outcome.apiError?.httpStatus == 409)
    }

    @Test("key reuse is reported even while the original is still in flight")
    func keyReuseBeatsInProgress() {
        // Hash mismatch is the more specific diagnosis; reporting `request_in_progress` here
        // would tell the client to retry a request that can never succeed under this key.
        #expect(
            Idempotency.decide(stored: record(hash: hash, status: nil, body: nil), requestHash: otherHash)
                == .conflictKeyReuse
        )
    }

    @Test("a truncated or extended hash counts as a different request")
    func hashLengthMatters() {
        #expect(
            Idempotency.decide(stored: record(hash: hash, status: 201, body: []), requestHash: Array(hash.dropLast()))
                == .conflictKeyReuse
        )
        #expect(
            Idempotency.decide(stored: record(hash: hash, status: 201, body: []), requestHash: hash + [0])
                == .conflictKeyReuse
        )
    }

    @Test("rows expire after 24 hours")
    func expiresAfterTtl() {
        #expect(!Idempotency.isExpired(record(hash: hash, status: 201, body: [], age: 0), now: now))
        #expect(!Idempotency.isExpired(record(hash: hash, status: 201, body: [], age: 23 * 3600), now: now))
        #expect(Idempotency.isExpired(record(hash: hash, status: 201, body: [], age: 24 * 3600), now: now))
        #expect(Idempotency.isExpired(record(hash: hash, status: 201, body: [], age: 48 * 3600), now: now))
        #expect(Idempotency.ttl == 86_400)
    }

    @Test("in-flight is a state of the row, not a separate flag")
    func inFlightReflectsStatus() {
        #expect(record(hash: hash, status: nil, body: nil).isInFlight)
        #expect(!record(hash: hash, status: 201, body: []).isInFlight)
    }
}
