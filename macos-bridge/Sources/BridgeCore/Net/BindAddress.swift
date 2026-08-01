import Foundation

/// A validated listener address.
public struct BindAddress: Sendable, Equatable {
    /// The literal as configured — this is what gets handed to `SocketAddress(ipAddress:port:)`.
    public let ipAddress: String
    public let parsed: IPAddress
    public let port: Int

    public init(ipAddress: String, parsed: IPAddress, port: Int) {
        self.ipAddress = ipAddress
        self.parsed = parsed
        self.port = port
    }

    /// `192.168.178.103:17832`.
    public var displayText: String { "\(ipAddress):\(port)" }

    public var selection: BindSelection { BindSelection(ipAddress: ipAddress, port: port) }
}

/// A bind choice as persisted by the store and edited in the setup UI — **unvalidated**.
///
/// It is a separate type from `BindAddress` on purpose. A choice has to be storable, displayable
/// and editable before anyone has checked it against the live interface list, and the check is
/// re-done on every start attempt rather than cached: a DHCP lease can move the address out from
/// under a value that was perfectly valid an hour ago. Only `BindAddressValidator` turns one of
/// these into a `BindAddress`, so nothing downstream can bind an unchecked choice by accident.
public struct BindSelection: Sendable, Equatable, Codable {
    public var ipAddress: String
    public var port: Int

    public init(ipAddress: String, port: Int) {
        self.ipAddress = ipAddress
        self.port = port
    }

    /// `192.168.178.103:17832` — for menus, status lines and error text.
    public var displayText: String { "\(ipAddress):\(port)" }
}

public enum BindAddressError: Error, Equatable, Sendable {
    /// A hostname, an address with a zone id, or plain nonsense. Names are refused because
    /// resolution is an outbound capability and because DNS decides what the bridge binds to.
    case notAnIPLiteral
    /// `0.0.0.0` or `::`.
    case wildcardNotAllowed
    /// `127.0.0.0/8` or `::1` — a loopback bind silently makes the bridge unreachable from Erda.
    case loopbackNotAllowed
    /// Parseable, allowed, but not currently configured on any interface: binding would fail with
    /// `EADDRNOTAVAIL`, and failing at config load says so plainly instead.
    case notOnLocalInterface
    case portOutOfRange
    /// The interface list could not be read.
    case interfaceEnumerationFailed
}

/// The knobs the validator exposes, so tests and future callers do not need a second validator.
public struct BindAddressPolicy: Sendable, Equatable {
    /// `false` in production. `BridgeHTTP`'s socket tests bind `127.0.0.1`, and they should go
    /// through the same validator rather than around it.
    public var allowLoopback: Bool
    public var portRange: ClosedRange<Int>

    public init(allowLoopback: Bool = false, portRange: ClosedRange<Int> = 1024...65535) {
        self.allowLoopback = allowLoopback
        self.portRange = portRange
    }

    public static let production = BindAddressPolicy()
}

/// Supplies the addresses currently configured on local interfaces.
///
/// **This is the deliberate seam that keeps `BridgeCore` pure.** Enumerating interfaces needs
/// `getifaddrs(3)` (or NIO's `System.enumerateDevices()`), i.e. a syscall and a non-Foundation
/// import; that implementation belongs to the target that already links NIO (`BridgeHTTP`, M3).
/// Everything worth testing — literal parsing, the wildcard/loopback/hostname rules, the
/// membership check and its failure modes — is on this side of the protocol and needs no syscall.
///
/// Implementations return address literals as the OS reports them; unparseable entries are
/// ignored by the validator rather than treated as an error, because an interface list may
/// legitimately contain forms this parser does not model (a scoped link-local, say).
public protocol NetworkInterfaceLister: Sendable {
    func localAddresses() throws -> [String]
}

/// A fixed list, for tests and for a caller that already knows the machine's addresses.
public struct StaticInterfaceLister: NetworkInterfaceLister {
    private let addresses: [String]

    public init(_ addresses: [String]) {
        self.addresses = addresses
    }

    public func localAddresses() throws -> [String] { addresses }
}

public enum BindAddressValidator {
    /// Validates a configured bind address. Order matters: the cheapest and most categorical
    /// rejections come first, and the interface list is only read once the literal itself is
    /// acceptable.
    public static func validate(
        ipAddress: String,
        port: Int,
        interfaces: any NetworkInterfaceLister,
        policy: BindAddressPolicy = .production
    ) throws -> BindAddress {
        guard policy.portRange.contains(port) else { throw BindAddressError.portOutOfRange }
        guard let parsed = IPAddress.parse(ipAddress) else { throw BindAddressError.notAnIPLiteral }
        guard !parsed.isWildcard else { throw BindAddressError.wildcardNotAllowed }
        if parsed.isLoopback, !policy.allowLoopback { throw BindAddressError.loopbackNotAllowed }

        let local: [String]
        do {
            local = try interfaces.localAddresses()
        } catch {
            throw BindAddressError.interfaceEnumerationFailed
        }

        let present = local.compactMap(IPAddress.parse).contains(parsed)
        guard present else { throw BindAddressError.notOnLocalInterface }

        return BindAddress(ipAddress: ipAddress, parsed: parsed, port: port)
    }

    /// The stored-choice form. Same rules, same order — there is only one validator.
    public static func validate(
        _ selection: BindSelection,
        interfaces: any NetworkInterfaceLister,
        policy: BindAddressPolicy = .production
    ) throws -> BindAddress {
        try validate(
            ipAddress: selection.ipAddress,
            port: selection.port,
            interfaces: interfaces,
            policy: policy
        )
    }
}
