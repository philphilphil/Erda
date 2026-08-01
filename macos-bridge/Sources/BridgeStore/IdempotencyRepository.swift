import BridgeCore
import Foundation

/// The `Idempotency-Key` ledger (dossier §4.3).
///
/// The interesting property is that this type contains **no** decision logic: the four outcomes
/// come from `BridgeCore.Idempotency.decide`, which is pure and exhaustively unit-tested without
/// a database. What lives here is only the part that needs SQLite — claiming a key atomically.
public struct IdempotencyRepository: Sendable {
    private let db: SQLiteDB
    private let clock: any BridgeClock

    public init(db: SQLiteDB, clock: any BridgeClock = SystemClock()) {
        self.db = db
        self.clock = clock
    }

    /// Claims `key` for this request, or reports what the key already means.
    ///
    /// `BEGIN IMMEDIATE` + `INSERT` is the whole mechanism. Two requests racing on the same key
    /// serialise on the write lock; the winner inserts an in-flight row and gets `.proceed`, the
    /// loser hits the primary-key constraint, reads the row it lost to, and gets a conflict or a
    /// replay. Doing this as `SELECT` then `INSERT` would let both readers see "absent".
    public func claim(key: String, requestHash: [UInt8]) throws -> IdempotencyOutcome {
        let now = clock.now
        return try db.transaction(immediate: true) {
            do {
                try insertInFlight(key: key, requestHash: requestHash, now: now)
                return .proceed
            } catch let error as SQLiteError where error.isConstraintViolation {
                guard let stored = try fetch(key: key) else {
                    // The row vanished between the failed insert and this read. Under
                    // `BEGIN IMMEDIATE` no other writer can be inside this transaction, so this
                    // is unreachable in practice; retrying the insert is still the right answer.
                    try insertInFlight(key: key, requestHash: requestHash, now: now)
                    return .proceed
                }

                // A row past its TTL is no longer binding. The hourly sweep may not have run
                // yet, and without this an expired key would produce a spurious 409 for the
                // next 24 hours' worth of retries.
                if Idempotency.isExpired(stored, now: now) {
                    try db.run("DELETE FROM idempotency WHERE key = ?", [.text(key)])
                    try insertInFlight(key: key, requestHash: requestHash, now: now)
                    return .proceed
                }

                return Idempotency.decide(stored: stored, requestHash: requestHash)
            }
        }
    }

    /// Stores the finished response so a later retry with the same key and body replays it.
    ///
    /// Only an in-flight row may be completed; the `status IS NULL` guard means a late second
    /// completion cannot overwrite the response the client already received.
    public func complete(key: String, status: Int, body: [UInt8]) throws {
        let changed = try db.run(
            "UPDATE idempotency SET status = ?, response_body = ? WHERE key = ? AND status IS NULL",
            [.int(status), .blob(body), .text(key)]
        )
        guard changed == 1 else { throw StoreError.idempotencyRowNotInFlight }
    }

    /// Drops the in-flight row after a handler failure, so the client's retry can proceed.
    ///
    /// Failures are deliberately **not** cached: replaying a 500 for 24 hours would turn one
    /// transient EventKit hiccup into a day-long outage for that key.
    @discardableResult
    public func abandon(key: String) throws -> Bool {
        try db.run("DELETE FROM idempotency WHERE key = ? AND status IS NULL", [.text(key)]) == 1
    }

    public func record(for key: String) throws -> IdempotencyRecord? {
        try fetch(key: key)
    }

    /// Removes rows past the 24 h TTL. Run at startup and hourly.
    @discardableResult
    public func sweepExpired() throws -> Int {
        let cutoff = clock.now.addingTimeInterval(-Idempotency.ttl)
        return try db.run("DELETE FROM idempotency WHERE created_at <= ?", [.date(cutoff)])
    }

    public func count() throws -> Int {
        let value = try db.queryOne("SELECT COUNT(*) FROM idempotency", table: "idempotency") { row in
            try row.integer(0, "count")
        }
        return Int(value ?? 0)
    }

    // MARK: - Internals

    private func insertInFlight(key: String, requestHash: [UInt8], now: Date) throws {
        try db.run(
            """
            INSERT INTO idempotency(key, request_hash, status, response_body, created_at)
            VALUES (?, ?, NULL, NULL, ?)
            """,
            [.text(key), .blob(requestHash), .date(now)]
        )
    }

    private func fetch(key: String) throws -> IdempotencyRecord? {
        try db.queryOne(
            "SELECT key, request_hash, status, response_body, created_at FROM idempotency WHERE key = ?",
            [.text(key)],
            table: "idempotency"
        ) { row in
            IdempotencyRecord(
                key: try row.text(0, "key"),
                requestHash: try row.blob(1, "request_hash"),
                status: row.optionalInteger(2).map(Int.init),
                responseBody: row.optionalBlob(3),
                createdAt: try row.date(4, "created_at")
            )
        }
    }
}

/// The repository already has exactly the shape `BridgeHTTP` asks for, so the conformance is
/// empty. `BridgeHTTP` depends on `BridgeCore` only — it never links SQLite.
extension IdempotencyRepository: IdempotencyStore {}

/// Runs the TTL sweep at startup and then hourly (dossier §4.3 step 4).
public actor IdempotencySweeper {
    private let repository: IdempotencyRepository
    private let interval: Duration
    private var task: Task<Void, Never>?
    private var sweeps = 0
    private var lastError: String?

    public init(repository: IdempotencyRepository, interval: Duration = .seconds(3600)) {
        self.repository = repository
        self.interval = interval
    }

    /// Sweeps immediately, then keeps sweeping in the background until `stop()`.
    @discardableResult
    public func start() -> Int {
        let removed = sweepNow()
        guard task == nil else { return removed }
        task = Task { [weak self, interval] in
            while !Task.isCancelled {
                try? await Task.sleep(for: interval)
                guard !Task.isCancelled, let self else { return }
                _ = await self.sweepNow()
            }
        }
        return removed
    }

    public func stop() {
        task?.cancel()
        task = nil
    }

    /// Never throws: a failing sweep is a housekeeping problem, not a reason to take the
    /// bridge down. The error is recorded for the status UI.
    @discardableResult
    public func sweepNow() -> Int {
        do {
            let removed = try repository.sweepExpired()
            sweeps += 1
            lastError = nil
            return removed
        } catch {
            sweeps += 1
            lastError = String(describing: error)
            return 0
        }
    }

    public var sweepCount: Int { sweeps }
    public var lastSweepError: String? { lastError }
}
