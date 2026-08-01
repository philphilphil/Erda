import Foundation

/// ISO-8601 date-time handling with one non-negotiable rule: **an incoming timestamp must carry
/// an explicit UTC offset**.
///
/// A naive `2026-07-31T09:00:00` would otherwise be interpreted in whatever zone the Mac happens
/// to be in, which silently moves a reminder by hours when Erda and the Mac disagree.
public enum ISO8601 {
    /// Parses a timestamp, rejecting anything without an explicit offset (`Z` or `±HH:MM` /
    /// `±HHMM`). Fractional seconds are optional.
    public static func parseRequiringOffset(_ text: String) -> Date? {
        guard hasExplicitOffset(text) else { return nil }
        // `ISO8601DateFormatter` is a class with mutable state; instantiate per call rather than
        // sharing one across connections. At 30 requests/minute this costs nothing.
        let withFraction = ISO8601DateFormatter()
        withFraction.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let date = withFraction.date(from: text) { return date }

        let plain = ISO8601DateFormatter()
        plain.formatOptions = [.withInternetDateTime]
        return plain.date(from: text)
    }

    /// Renders a timestamp for responses: always UTC, always with an offset (`Z`).
    public static func string(from date: Date) -> String {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime]
        formatter.timeZone = TimeZone(secondsFromGMT: 0)
        return formatter.string(from: date)
    }

    /// Millisecond-precision UTC, the format the audit log uses (`2026-07-31T17:42:03.221Z`).
    public static func millisecondString(from date: Date) -> String {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        formatter.timeZone = TimeZone(secondsFromGMT: 0)
        return formatter.string(from: date)
    }

    /// A structural check done before handing the string to Foundation, because the formatter's
    /// exact tolerance is not something the security posture should rest on.
    static func hasExplicitOffset(_ text: String) -> Bool {
        let scalars = Array(text.unicodeScalars)
        // Shortest legal form: `2026-07-31T09:00Z` — 17 scalars.
        guard scalars.count >= 17, scalars.contains("T") || scalars.contains("t") else { return false }

        if scalars.last == "Z" || scalars.last == "z" { return true }

        func isDigit(_ index: Int) -> Bool {
            index >= 0 && index < scalars.count && ("0"..."9").contains(scalars[index])
        }

        // `±HH:MM`
        let colonSign = scalars.count - 6
        if colonSign >= 0, scalars[colonSign] == "+" || scalars[colonSign] == "-",
           isDigit(colonSign + 1), isDigit(colonSign + 2), scalars[colonSign + 3] == ":",
           isDigit(colonSign + 4), isDigit(colonSign + 5) {
            return true
        }

        // `±HHMM`
        let plainSign = scalars.count - 5
        if plainSign >= 0, scalars[plainSign] == "+" || scalars[plainSign] == "-",
           isDigit(plainSign + 1), isDigit(plainSign + 2), isDigit(plainSign + 3), isDigit(plainSign + 4) {
            return true
        }

        return false
    }
}
