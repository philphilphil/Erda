import Foundation

/// Step 1 of dossier §2.3: a hard ceiling on concurrent connections.
///
/// This is the first and cheapest defence — it costs one integer and runs before a single byte
/// is read, so a client opening sockets in a loop cannot make the process allocate parsers,
/// buffers or tasks. Over the ceiling, the connection is accepted and closed immediately.
public actor ConnectionAdmission {
    private let limit: Int
    private var active = 0

    public init(limit: Int) {
        precondition(limit > 0, "a bridge that admits no connections is a configuration bug")
        self.limit = limit
    }

    /// `true` when the caller now holds a slot and must call `release()`.
    func acquire() -> Bool {
        guard active < limit else { return false }
        active += 1
        return true
    }

    func release() {
        active = max(0, active - 1)
    }

    /// Exposed so tests can wait for the server to have admitted a known number of connections
    /// rather than sleeping and hoping.
    public var activeConnections: Int { active }
}
