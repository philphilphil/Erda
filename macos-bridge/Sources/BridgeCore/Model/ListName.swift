import Foundation

/// The name of a Reminders list, exactly as it reads in Reminders.app.
///
/// Lists are addressed by name on the wire. There is no allowlist and no alias indirection — a
/// deliberate decision (see `macos-bridge/README.md`): Apple grants EventKit reminder access
/// all-or-nothing, and rather than pretend otherwise behind a table of aliases, the bridge simply
/// reaches every reminder list on this Mac. What still never crosses the boundary is an
/// `EKCalendar.calendarIdentifier`; a name is the only list handle a caller ever sees.
///
/// This type is therefore **input hygiene, not an access rule**. It bounds the length and refuses
/// control characters, so a name cannot carry a newline into the audit log or a NUL into SQLite.
/// It says nothing about which lists exist — that is decided at resolution time, against EventKit.
public struct ListName: Sendable, Hashable, Codable, RawRepresentable, CustomStringConvertible, Comparable {
    public let rawValue: String

    /// Surrounding whitespace is trimmed before validating, so a stray space in a query string or
    /// a JSON body does not become a name that matches nothing. A list whose title genuinely
    /// begins or ends with whitespace is consequently unaddressable — Reminders.app will not let
    /// you make one, and accepting it would put two names on screen that look identical.
    public init?(rawValue: String) {
        let trimmed = rawValue.trimmingCharacters(in: .whitespacesAndNewlines)
        guard ListName.isValid(trimmed) else { return nil }
        self.rawValue = trimmed
    }

    /// True for a name already in canonical form. `init(rawValue:)` trims first, so it accepts
    /// input this returns `false` for; what it will never do is *store* something non-canonical.
    public static func isValid(_ candidate: String) -> Bool {
        guard candidate.trimmingCharacters(in: .whitespacesAndNewlines) == candidate else { return false }
        // Counted in Unicode scalars: `count` on `Character` would let a grapheme cluster of many
        // scalars slip past the cap.
        let scalars = Array(candidate.unicodeScalars)
        guard (Limits.listNameMinLength...Limits.listNameMaxLength).contains(scalars.count) else {
            return false
        }
        for scalar in scalars where isControl(scalar) { return false }
        return true
    }

    /// C0, DEL and C1. Everything else — umlauts, CJK, emoji — is a perfectly ordinary list name
    /// and there is no reason to refuse it.
    private static func isControl(_ scalar: Unicode.Scalar) -> Bool {
        scalar.value < 0x20 || (0x7F...0x9F).contains(scalar.value)
    }

    public var description: String { rawValue }

    public static func < (lhs: ListName, rhs: ListName) -> Bool { lhs.rawValue < rhs.rawValue }

    public init(from decoder: any Decoder) throws {
        let raw = try decoder.singleValueContainer().decode(String.self)
        guard let name = ListName(rawValue: raw) else { throw ApiError.invalidRequest }
        self = name
    }

    public func encode(to encoder: any Encoder) throws {
        var container = encoder.singleValueContainer()
        try container.encode(rawValue)
    }
}
