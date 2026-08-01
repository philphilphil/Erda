import BridgeCore
import Foundation

/// Name → list resolution, extracted from the actor so it is testable without EventKit.
///
/// With the allowlist gone this no longer decides *whether* a list may be touched. It decides
/// *which* list a name means — and refuses to guess, which makes it still the most
/// security-relevant branch in the module: a wrong answer here writes into somebody's shared list.
enum ListLookup {
    /// One reminder list, flattened to the two fields resolution needs. Deliberately not an
    /// `EKCalendar`: this type has to be constructible in a test on a Mac with no Reminders data.
    struct Candidate: Sendable, Equatable {
        let calendarId: String
        let title: String
    }

    /// Exact name first. Failing that, a case-insensitive match is accepted **only when it is
    /// unique**: someone who typed `groceries` for a list called `Groceries` meant that list, and
    /// there is nothing to be gained by refusing.
    ///
    /// Anything ambiguous is `no_such_list` rather than a coin flip. Two lists can genuinely share
    /// a name — an iCloud "Reminders" and an On My Mac "Reminders" — and the wire format carries no
    /// account, so there is no honest way to choose. Failing is recoverable; writing into the
    /// wrong list is not.
    static func resolve(_ name: ListName, in candidates: [Candidate]) throws -> Candidate {
        let exact = candidates.filter { canonicalName($0) == name }
        if exact.count == 1 { return exact[0] }
        guard exact.isEmpty else { throw ApiError.noSuchList }

        let folded = candidates.filter {
            $0.title.compare(name.rawValue, options: [.caseInsensitive]) == .orderedSame
        }
        guard folded.count == 1 else { throw ApiError.noSuchList }
        return folded[0]
    }

    /// The reverse lookup `complete` performs against a reminder's **current** calendar.
    ///
    /// A dangling or re-homed id must not silently succeed, so the list the reminder is in *now*
    /// has to be one this Mac currently reports as a reminder list. `nil` means the caller gets a
    /// 404 — the same answer as an id that was never issued.
    static func name(forCalendarId calendarId: String, in candidates: [Candidate]) -> ListName? {
        guard let match = candidates.first(where: { $0.calendarId == calendarId }) else { return nil }
        return canonicalName(match)
    }

    /// A list whose title is not a valid `ListName` — control characters, or longer than the cap —
    /// has no name a caller could send, so it matches nothing and its reminders stay invisible.
    /// Fails closed, and there is no such list on a Mac anyone has actually used.
    static func canonicalName(_ candidate: Candidate) -> ListName? {
        ListName(rawValue: candidate.title)
    }
}
