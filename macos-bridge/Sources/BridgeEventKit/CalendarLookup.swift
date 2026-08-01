import BridgeCore
import Foundation

/// Name → calendar resolution, extracted from the actor so it is testable without EventKit.
///
/// The same shape as `ListLookup`, and the most security-relevant branch on the calendar side for
/// the same reason: a wrong answer here writes an appointment into somebody's shared calendar.
///
/// It differs from `ListLookup` in exactly one way, and deliberately. A list that matches nothing
/// and a list that matches two both answer `no_such_list`, because the caller's next move is the
/// same either way. A calendar reports them **separately** — `no_such_calendar` versus
/// `ambiguous_calendar` — because Erda relays the reason to Phil verbatim and the two fixes are
/// different: check the spelling, versus rename one of the two calendars you have called that.
enum CalendarLookup {
    /// One calendar, flattened to the two fields resolution needs. Deliberately not an
    /// `EKCalendar`: this type has to be constructible in a test on a Mac with no calendar data.
    struct Candidate: Sendable, Equatable {
        let calendarId: String
        let title: String
    }

    /// Exact name first. Failing that, a case-insensitive match is accepted **only when it is
    /// unique**: someone who typed `privat` for a calendar called `Privat` meant that calendar,
    /// and there is nothing to be gained by refusing.
    ///
    /// Anything ambiguous fails. Two calendars can genuinely share a name — an iCloud "Privat" and
    /// a local one — and the wire format carries no account, so there is no honest way to choose.
    /// Failing is recoverable; writing into the wrong calendar is not.
    static func resolve(_ name: CalendarName, in candidates: [Candidate]) throws -> Candidate {
        let exact = candidates.filter { canonicalName($0) == name }
        if exact.count == 1 { return exact[0] }
        guard exact.isEmpty else { throw ApiError.ambiguousCalendar }

        let folded = candidates.filter {
            $0.title.compare(name.rawValue, options: [.caseInsensitive]) == .orderedSame
        }
        if folded.count == 1 { return folded[0] }
        throw folded.isEmpty ? ApiError.noSuchCalendar : ApiError.ambiguousCalendar
    }

    /// A calendar whose title is not a valid `CalendarName` — control characters, or longer than
    /// the cap — has no name a caller could send, so it matches nothing and its events stay
    /// invisible. Fails closed, and there is no such calendar on a Mac anyone has actually used.
    static func canonicalName(_ candidate: Candidate) -> CalendarName? {
        CalendarName(rawValue: candidate.title)
    }
}
