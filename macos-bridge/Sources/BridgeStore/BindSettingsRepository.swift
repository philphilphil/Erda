import BridgeCore
import Foundation

/// Where the listener's address lives.
///
/// It is stored, not compiled in. The first build hardcoded `192.168.178.106`; the Mac's DHCP
/// lease then moved to another address and the app could no longer bind at all — a constant in a
/// source file is the wrong place for a value the router owns.
///
/// **There is deliberately no default and no auto-pick.** A missing or half-written row reads back
/// as `nil`, which the supervisor reports as "not configured" and refuses to bind on. Choosing
/// "whatever interface is available" would be worse than failing: on a Mac joined to a café
/// network or a phone hotspot it would quietly publish the bridge there.
public struct BindSettingsRepository: Sendable {
    /// Dossier §4.3 names these two `meta` keys.
    static let ipKey = "bind_ip"
    static let portKey = "port"

    private let meta: MetaRepository

    public init(meta: MetaRepository) {
        self.meta = meta
    }

    /// `nil` when no complete choice has been stored.
    ///
    /// A row with an address but no port — or a port that is not an integer — is treated as
    /// absent rather than repaired with a guess, because the guess would be a bind address
    /// nobody chose.
    public func load() throws -> BindSelection? {
        guard let ipAddress = try meta.value(for: Self.ipKey),
              let rawPort = try meta.value(for: Self.portKey),
              let port = Int(rawPort)
        else { return nil }
        return BindSelection(ipAddress: ipAddress, port: port)
    }

    /// Stores a choice verbatim. Validation belongs to `BindAddressValidator` and happens at
    /// every start attempt, not here: an address that is valid at save time can stop being valid
    /// before the next launch, so a "validated" flag on disk would be a lie with a timestamp.
    public func save(_ selection: BindSelection) throws {
        try meta.set(selection.ipAddress, for: Self.ipKey)
        try meta.set(String(selection.port), for: Self.portKey)
    }

    public func clear() throws {
        try meta.remove(Self.ipKey)
        try meta.remove(Self.portKey)
    }
}
