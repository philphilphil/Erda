import Foundation

/// A local, human-assigned name for one allowlisted Reminders list.
///
/// The wire format never carries an `EKCalendar.calendarIdentifier`; aliases are the only
/// list handle a remote caller ever sees, and the remote API has no route that can create one.
///
/// Valid form is `^[a-z0-9][a-z0-9_-]{0,31}$`, checked character by character rather than with a
/// regex — the same no-regex posture the router takes, and it makes the rule readable.
public struct Alias: Sendable, Hashable, Codable, RawRepresentable, CustomStringConvertible, Comparable {
    public let rawValue: String

    public init?(rawValue: String) {
        guard Alias.isValid(rawValue) else { return nil }
        self.rawValue = rawValue
    }

    public static func isValid(_ candidate: String) -> Bool {
        // Count in Unicode scalars: `count` on Character would let a grapheme cluster of
        // several scalars pass the length cap.
        let scalars = Array(candidate.unicodeScalars)
        guard (1...Limits.aliasMaxLength).contains(scalars.count) else { return false }
        guard isLowerAlphanumeric(scalars[0]) else { return false }
        for scalar in scalars.dropFirst() where !isLowerAlphanumeric(scalar) && scalar != "_" && scalar != "-" {
            return false
        }
        return true
    }

    private static func isLowerAlphanumeric(_ scalar: Unicode.Scalar) -> Bool {
        ("a"..."z").contains(scalar) || ("0"..."9").contains(scalar)
    }

    public var description: String { rawValue }

    public static func < (lhs: Alias, rhs: Alias) -> Bool { lhs.rawValue < rhs.rawValue }

    public init(from decoder: any Decoder) throws {
        let raw = try decoder.singleValueContainer().decode(String.self)
        guard let alias = Alias(rawValue: raw) else { throw ApiError.invalidRequest }
        self = alias
    }

    public func encode(to encoder: any Encoder) throws {
        var container = encoder.singleValueContainer()
        try container.encode(rawValue)
    }
}
