import BridgeCore
import Foundation
import NIOCore

/// The real implementation of `BridgeCore.NetworkInterfaceLister`.
///
/// This is the far side of the seam M1 left open: enumerating interfaces needs a syscall, which
/// would have cost `BridgeCore` its "Foundation and nothing else" guarantee, so the enumeration
/// lives here — in the target that already links NIO — while all the rules about which addresses
/// are acceptable stay pure and unit-tested.
///
/// `System.enumerateDevices()` rather than a raw `getifaddrs(3)`: NIO already owns that syscall
/// and its memory management, and it hands back parsed `SocketAddress` values.
public struct NIOInterfaceLister: NetworkInterfaceLister {
    public init() {}

    public func localAddresses() throws -> [String] {
        try System.enumerateDevices().compactMap { device in
            switch device.address {
            case .v4(let address):
                return Self.text(address.host)
            case .v6(let address):
                return Self.text(address.host)
            default:
                // Unix-domain and unaddressed devices are not bind candidates.
                return nil
            }
        }
    }

    /// `SocketAddress`'s host string can carry a scope suffix for link-local IPv6
    /// (`fe80::1%en0`); `BridgeCore`'s parser rejects those deliberately, so the suffix is
    /// trimmed here rather than silently dropping the address from the list.
    private static func text(_ host: String) -> String {
        guard let separator = host.firstIndex(of: "%") else { return host }
        return String(host[host.startIndex..<separator])
    }
}
