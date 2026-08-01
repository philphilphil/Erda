import Foundation

/// The name of a calendar, exactly as it reads in Calendar.app.
///
/// The same shape as `ListName` and validated the same way (see `NameHygiene`), but deliberately
/// a **separate type**: reminder lists and calendars are two namespaces EventKit keeps apart
/// (`calendars(for: .reminder)` versus `calendars(for: .event)`), and a Mac can easily hold a
/// "Privat" in each. Making them one type would let a resolved list name be passed where a
/// calendar name belongs, and the compiler would say nothing.
///
/// Like `ListName`, this is **input hygiene, not an access rule**. It says nothing about which
/// calendars exist — that is decided at resolution time, against EventKit — only that a name
/// cannot carry a newline into the audit log or a NUL into SQLite. What still never crosses the
/// boundary is an `EKCalendar.calendarIdentifier`; a name is the only calendar handle a caller
/// ever sees.
public struct CalendarName: Sendable, Hashable, Codable, RawRepresentable, CustomStringConvertible, Comparable {
    public let rawValue: String

    /// Surrounding whitespace is trimmed before validating, so a stray space in a query string or
    /// a JSON body does not become a name that matches nothing. A calendar whose title genuinely
    /// begins or ends with whitespace is consequently unaddressable — Calendar.app will not let
    /// you make one, and accepting it would put two names on screen that look identical.
    public init?(rawValue: String) {
        let trimmed = rawValue.trimmingCharacters(in: .whitespacesAndNewlines)
        guard CalendarName.isValid(trimmed) else { return nil }
        self.rawValue = trimmed
    }

    /// True for a name already in canonical form. `init(rawValue:)` trims first, so it accepts
    /// input this returns `false` for; what it will never do is *store* something non-canonical.
    public static func isValid(_ candidate: String) -> Bool {
        NameHygiene.isValid(
            candidate,
            length: Limits.calendarNameMinLength...Limits.calendarNameMaxLength
        )
    }

    public var description: String { rawValue }

    public static func < (lhs: CalendarName, rhs: CalendarName) -> Bool { lhs.rawValue < rhs.rawValue }

    public init(from decoder: any Decoder) throws {
        let raw = try decoder.singleValueContainer().decode(String.self)
        guard let name = CalendarName(rawValue: raw) else { throw ApiError.invalidRequest }
        self = name
    }

    public func encode(to encoder: any Encoder) throws {
        var container = encoder.singleValueContainer()
        try container.encode(rawValue)
    }
}
