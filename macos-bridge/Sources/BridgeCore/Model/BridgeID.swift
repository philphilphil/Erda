import Foundation

/// A bridge-issued reminder id, `rem_<uuid>`.
///
/// EventKit's `calendarItemIdentifier` is explicitly not sync-proof (`EKCalendarItem.h`), so it
/// never leaves the Mac. Callers only ever see a `BridgeID`, and the bridge keeps the mapping.
public struct BridgeID: Sendable, Hashable, Codable, RawRepresentable, CustomStringConvertible {
    public static let prefix = "rem_"

    /// Always the canonical lowercase form, so it can be used as a storage key directly.
    public let rawValue: String

    /// Parses `rem_<uuid>`. Accepts either case in the UUID and normalises to lowercase;
    /// anything else — a missing prefix, a non-UUID tail, trailing junk — is rejected.
    public init?(rawValue: String) {
        guard rawValue.hasPrefix(BridgeID.prefix) else { return nil }
        let tail = rawValue.dropFirst(BridgeID.prefix.count)
        // `UUID(uuidString:)` accepts only the canonical 36-character hyphenated form.
        guard tail.count == 36, let uuid = UUID(uuidString: String(tail)) else { return nil }
        self.rawValue = BridgeID.prefix + uuid.uuidString.lowercased()
    }

    public static func generate() -> BridgeID {
        // Force-unwrap is safe: a freshly generated UUID always parses.
        BridgeID(rawValue: prefix + UUID().uuidString.lowercased())!
    }

    public var description: String { rawValue }

    public init(from decoder: any Decoder) throws {
        let raw = try decoder.singleValueContainer().decode(String.self)
        guard let id = BridgeID(rawValue: raw) else { throw ApiError.invalidRequest }
        self = id
    }

    public func encode(to encoder: any Encoder) throws {
        var container = encoder.singleValueContainer()
        try container.encode(rawValue)
    }
}
