import BridgeCore
import Foundation
import Testing

@testable import BridgeStore

@Suite("Idempotency repository")
struct IdempotencyRepositoryTests {
    private let root = TemporaryRoot()
    private let key = "6f0c1b6e-1f4a-4a9d-9f3e-1b2c3d4e5f60"

    @Test("a fresh key proceeds and leaves an in-flight row")
    func claimsFreshKey() throws {
        let store = try root.open()
        #expect(try store.idempotency.claim(key: key, requestHash: hash(1)) == .proceed)

        let record = try #require(try store.idempotency.record(for: key))
        #expect(record.isInFlight)
        #expect(record.requestHash == hash(1))
    }

    @Test("the same key and body while in flight is request_in_progress")
    func rejectsConcurrentRetry() throws {
        let store = try root.open()
        _ = try store.idempotency.claim(key: key, requestHash: hash(1))
        #expect(try store.idempotency.claim(key: key, requestHash: hash(1)) == .conflictInProgress)
    }

    @Test("the same key and body after completion replays the stored response byte for byte")
    func replaysCompletedResponse() throws {
        let store = try root.open()
        _ = try store.idempotency.claim(key: key, requestHash: hash(1))

        let body = Array(#"{"id":"rem_6f0c1b6e-1f4a-4a9d-9f3e-1b2c3d4e5f60"}"#.utf8)
        try store.idempotency.complete(key: key, status: 201, body: body)

        #expect(try store.idempotency.claim(key: key, requestHash: hash(1)) == .replay(status: 201, body: body))
    }

    @Test("an empty response body replays as empty, not as a still-running request")
    func replaysEmptyBody() throws {
        let store = try root.open()
        _ = try store.idempotency.claim(key: key, requestHash: hash(1))
        try store.idempotency.complete(key: key, status: 204, body: [])

        #expect(try store.idempotency.claim(key: key, requestHash: hash(1)) == .replay(status: 204, body: []))
        // The distinction that matters: a zero-length BLOB must not read back as SQL NULL.
        let record = try #require(try store.idempotency.record(for: key))
        #expect(record.responseBody == [])
        #expect(record.status == 204)
    }

    @Test("the same key with a different body is idempotency_key_reuse")
    func rejectsKeyReuse() throws {
        let store = try root.open()
        _ = try store.idempotency.claim(key: key, requestHash: hash(1))
        try store.idempotency.complete(key: key, status: 201, body: Array("x".utf8))

        #expect(try store.idempotency.claim(key: key, requestHash: hash(2)) == .conflictKeyReuse)
    }

    @Test("key reuse is reported even while the first request is still in flight")
    func keyReuseBeatsInProgress() throws {
        let store = try root.open()
        _ = try store.idempotency.claim(key: key, requestHash: hash(1))
        #expect(try store.idempotency.claim(key: key, requestHash: hash(2)) == .conflictKeyReuse)
    }

    @Test("a failed handler drops the in-flight row so the retry can proceed")
    func abandonFreesTheKey() throws {
        let store = try root.open()
        _ = try store.idempotency.claim(key: key, requestHash: hash(1))

        #expect(try store.idempotency.abandon(key: key))
        #expect(try store.idempotency.record(for: key) == nil)
        #expect(try store.idempotency.claim(key: key, requestHash: hash(1)) == .proceed)
    }

    @Test("a completed response is never abandoned — a replay must stay replayable")
    func abandonLeavesCompletedRows() throws {
        let store = try root.open()
        _ = try store.idempotency.claim(key: key, requestHash: hash(1))
        try store.idempotency.complete(key: key, status: 201, body: Array("x".utf8))

        #expect(try store.idempotency.abandon(key: key) == false)
        #expect(try store.idempotency.claim(key: key, requestHash: hash(1))
            == .replay(status: 201, body: Array("x".utf8)))
    }

    @Test("a second completion cannot overwrite the response the client already got")
    func completeOnlyOnce() throws {
        let store = try root.open()
        _ = try store.idempotency.claim(key: key, requestHash: hash(1))
        try store.idempotency.complete(key: key, status: 201, body: Array("first".utf8))

        #expect(throws: StoreError.idempotencyRowNotInFlight) {
            try store.idempotency.complete(key: self.key, status: 500, body: Array("second".utf8))
        }
        #expect(try store.idempotency.record(for: key)?.responseBody == Array("first".utf8))
    }

    @Test("completing a key that was never claimed is an error, not a silent insert")
    func completeRequiresAClaim() throws {
        let store = try root.open()
        #expect(throws: StoreError.idempotencyRowNotInFlight) {
            try store.idempotency.complete(key: self.key, status: 201, body: [])
        }
        #expect(try store.idempotency.count() == 0)
    }

    @Test("rows past the 24 h TTL are swept")
    func sweepsExpiredRows() throws {
        let clock = ManualClock()
        let store = try root.open(clock: clock)

        _ = try store.idempotency.claim(key: "old", requestHash: hash(1))
        clock.advance(by: 23 * 3600)
        _ = try store.idempotency.claim(key: "recent", requestHash: hash(2))

        clock.advance(by: 3600)  // "old" is now exactly 24 h, "recent" is 1 h
        #expect(try store.idempotency.sweepExpired() == 1)
        #expect(try store.idempotency.record(for: "old") == nil)
        #expect(try store.idempotency.record(for: "recent") != nil)
    }

    @Test("an expired row is reclaimed on the spot, without waiting for the hourly sweep")
    func expiredKeyIsReusable() throws {
        let clock = ManualClock()
        let store = try root.open(clock: clock)

        _ = try store.idempotency.claim(key: key, requestHash: hash(1))
        try store.idempotency.complete(key: key, status: 201, body: Array("x".utf8))

        clock.advance(by: 25 * 3600)
        // A different body under an expired key must be a fresh request, not a 409.
        #expect(try store.idempotency.claim(key: key, requestHash: hash(2)) == .proceed)
        #expect(try store.idempotency.record(for: key)?.requestHash == hash(2))
    }

    @Test("the sweeper sweeps at startup and keeps going")
    func sweeperRunsPeriodically() async throws {
        let clock = ManualClock()
        let store = try root.open(clock: clock)
        _ = try store.idempotency.claim(key: "old", requestHash: hash(1))
        clock.advance(by: 25 * 3600)

        let sweeper = IdempotencySweeper(repository: store.idempotency, interval: .milliseconds(20))
        let removedAtStartup = await sweeper.start()
        #expect(removedAtStartup == 1)

        // Then it keeps running on its own.
        var sweeps = await sweeper.sweepCount
        for _ in 0..<100 where sweeps < 2 {
            try await Task.sleep(for: .milliseconds(20))
            sweeps = await sweeper.sweepCount
        }
        await sweeper.stop()
        #expect(sweeps >= 2)
        #expect(await sweeper.lastSweepError == nil)
    }

    /// The property the whole mechanism exists for: two requests carrying the same
    /// `Idempotency-Key` must not both create a reminder.
    @Test("concurrent claims on the same key produce exactly one winner")
    func concurrentClaimsHaveOneWinner() async throws {
        let store = try root.open()
        store.close()

        // Separate connections, so the contention is real SQLite write-lock contention rather
        // than this process's own mutex serialising the callers.
        let connections = try (0..<8).map { _ in try root.openRawConnection() }
        defer { connections.forEach { $0.close() } }

        let requestHash = hash(9)
        let outcomes = await withTaskGroup(of: IdempotencyOutcome?.self) { group in
            for connection in connections {
                group.addTask {
                    try? IdempotencyRepository(db: connection).claim(key: "race", requestHash: requestHash)
                }
            }
            var collected: [IdempotencyOutcome?] = []
            for await outcome in group { collected.append(outcome) }
            return collected
        }

        #expect(outcomes.count == 8)
        #expect(outcomes.allSatisfy { $0 != nil }, "a claim failed outright: \(outcomes)")
        #expect(outcomes.filter { $0 == .proceed }.count == 1)
        #expect(outcomes.filter { $0 == .conflictInProgress }.count == 7)

        let verifier = try root.openRawConnection()
        #expect(try IdempotencyRepository(db: verifier).count() == 1)
    }

    @Test("concurrent claims on different keys all proceed")
    func concurrentDistinctKeysAllProceed() async throws {
        let store = try root.open()
        store.close()

        let connections = try (0..<8).map { _ in try root.openRawConnection() }
        defer { connections.forEach { $0.close() } }

        let requestHash = hash(3)
        let outcomes = await withTaskGroup(of: IdempotencyOutcome?.self) { group in
            for (index, connection) in connections.enumerated() {
                group.addTask {
                    try? IdempotencyRepository(db: connection).claim(key: "key-\(index)", requestHash: requestHash)
                }
            }
            var collected: [IdempotencyOutcome?] = []
            for await outcome in group { collected.append(outcome) }
            return collected
        }

        #expect(outcomes.filter { $0 == .proceed }.count == 8)
    }
}
