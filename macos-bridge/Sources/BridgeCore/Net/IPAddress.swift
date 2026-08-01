import Foundation

/// A parsed IP literal.
///
/// Parsing is hand-written rather than delegated to `inet_pton` or `Network.framework` so that
/// `BridgeCore` keeps its "Foundation and nothing else" guarantee. Comparison happens on the
/// parsed bytes, never on the text, so `FE80:0:0:0:0:0:0:1` and `fe80::1` are recognised as the
/// same address.
public enum IPAddress: Sendable, Hashable {
    case v4([UInt8])  // 4 bytes
    case v6([UInt8])  // 16 bytes

    /// Parses a numeric IP literal. Returns `nil` for a hostname, a literal with a scope/zone id
    /// (`fe80::1%en0`), or anything else malformed — the distinction between "not an IP" and
    /// "an IP we do not like" is drawn by `BindAddressValidator`, not here.
    public static func parse(_ text: String) -> IPAddress? {
        if text.contains(".") && !text.contains(":") {
            return IPAddress.parseIPv4(text).map { IPAddress.v4($0) }
        }
        guard text.contains(":") else { return nil }
        guard let bytes = IPAddress.parseIPv6(text) else { return nil }
        // Normalise IPv4-mapped (`::ffff:a.b.c.d`) to its IPv4 form so the wildcard, loopback and
        // interface checks all see one representation.
        if bytes[0..<10].allSatisfy({ $0 == 0 }), bytes[10] == 0xFF, bytes[11] == 0xFF {
            return .v4(Array(bytes[12..<16]))
        }
        return .v6(bytes)
    }

    /// `0.0.0.0` / `::` — binding these would expose the listener on every interface, which is
    /// the one thing the transport decision rules out.
    public var isWildcard: Bool {
        switch self {
        case .v4(let bytes), .v6(let bytes): bytes.allSatisfy { $0 == 0 }
        }
    }

    /// `127.0.0.0/8` / `::1`.
    ///
    /// The length checks are not redundant: the cases are public, so a caller can hand-build an
    /// `IPAddress` with the wrong payload length, and an out-of-bounds read here would be a crash
    /// in the request path.
    public var isLoopback: Bool {
        switch self {
        case .v4(let bytes): bytes.count == 4 && bytes[0] == 127
        case .v6(let bytes): bytes.count == 16 && bytes[0..<15].allSatisfy { $0 == 0 } && bytes[15] == 1
        }
    }

    /// A stable textual form for logging and error display. IPv6 is rendered uncompressed and
    /// lowercase; this is not RFC 5952 canonical form and is never parsed back for comparison.
    public var canonicalText: String {
        switch self {
        case .v4(let bytes):
            return bytes.map(String.init).joined(separator: ".")
        case .v6(let bytes):
            guard bytes.count == 16 else { return "" }
            return stride(from: 0, to: 16, by: 2)
                .map { String(format: "%02x%02x", bytes[$0], bytes[$0 + 1]) }
                .joined(separator: ":")
        }
    }

    // MARK: - Parsing

    static func parseIPv4(_ text: some StringProtocol) -> [UInt8]? {
        let parts = text.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 4 else { return nil }
        var bytes: [UInt8] = []
        bytes.reserveCapacity(4)
        for part in parts {
            guard !part.isEmpty, part.count <= 3,
                  part.allSatisfy({ $0.isASCII && $0.isNumber })
            else { return nil }
            // A leading zero would be octal in some parsers and decimal in others; refusing it
            // removes the ambiguity rather than picking a side.
            if part.count > 1, part.first == "0" { return nil }
            guard let value = UInt8(part) else { return nil }
            bytes.append(value)
        }
        return bytes
    }

    static func parseIPv6(_ text: String) -> [UInt8]? {
        // A zone id names an interface, which is a different thing from an address; reject it.
        guard !text.contains("%") else { return nil }

        let halves = text.components(separatedBy: "::")
        guard halves.count <= 2 else { return nil }

        if halves.count == 2 {
            guard let head = ipv6Groups(halves[0], allowTrailingIPv4: false),
                  let tail = ipv6Groups(halves[1], allowTrailingIPv4: true)
            else { return nil }
            let fill = 16 - head.count - tail.count
            // `::` must stand for at least one all-zero group, otherwise it is redundant syntax
            // and the address should have been written out in full.
            guard fill >= 2, fill.isMultiple(of: 2) else { return nil }
            return head + [UInt8](repeating: 0, count: fill) + tail
        }

        guard let bytes = ipv6Groups(halves[0], allowTrailingIPv4: true), bytes.count == 16 else {
            return nil
        }
        return bytes
    }

    /// Parses a colon-separated run of hex groups, optionally ending in a dotted-quad.
    private static func ipv6Groups(_ text: String, allowTrailingIPv4: Bool) -> [UInt8]? {
        guard !text.isEmpty else { return [] }
        let groups = text.split(separator: ":", omittingEmptySubsequences: false)
        var bytes: [UInt8] = []
        for (index, group) in groups.enumerated() {
            // An empty group here means a stray `:` — the only legal empty run is `::`, which was
            // already split off.
            guard !group.isEmpty else { return nil }

            if allowTrailingIPv4, index == groups.count - 1, group.contains(".") {
                guard let embedded = parseIPv4(group) else { return nil }
                bytes.append(contentsOf: embedded)
                continue
            }

            guard group.count <= 4, group.allSatisfy({ $0.isASCII && $0.isHexDigit }),
                  let value = UInt16(group, radix: 16)
            else { return nil }
            bytes.append(UInt8(truncatingIfNeeded: value >> 8))
            bytes.append(UInt8(truncatingIfNeeded: value))
        }
        return bytes.count <= 16 ? bytes : nil
    }
}
