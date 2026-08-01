import Foundation

/// The input hygiene `ListName` and `CalendarName` share.
///
/// Neither type is an access rule. macOS grants EventKit access all-or-nothing, so what a name
/// can address is decided at resolution time against EventKit, not here. What this *does* decide
/// is what a name may carry: it is bounded in length and refuses control characters, so a title
/// cannot drag a newline into the JSONL audit log or a NUL into SQLite.
///
/// It is shared rather than written twice so the two names cannot drift apart. They are the same
/// kind of string — a title someone typed into Reminders.app or Calendar.app — and a difference
/// in how they are validated would be a bug, not a design.
enum NameHygiene {
    /// True for a name already in canonical form. Both `init(rawValue:)`s trim before calling
    /// this, so they accept input it returns `false` for; what they will never do is *store*
    /// something non-canonical.
    static func isValid(_ candidate: String, length: ClosedRange<Int>) -> Bool {
        guard candidate.trimmingCharacters(in: .whitespacesAndNewlines) == candidate else { return false }
        // Counted in Unicode scalars: `count` on `Character` would let a grapheme cluster of many
        // scalars slip past the cap.
        let scalars = Array(candidate.unicodeScalars)
        guard length.contains(scalars.count) else { return false }
        for scalar in scalars where isControl(scalar) { return false }
        return true
    }

    /// C0, DEL and C1. Everything else — umlauts, CJK, emoji — is a perfectly ordinary name and
    /// there is no reason to refuse it.
    private static func isControl(_ scalar: Unicode.Scalar) -> Bool {
        scalar.value < 0x20 || (0x7F...0x9F).contains(scalar.value)
    }
}
