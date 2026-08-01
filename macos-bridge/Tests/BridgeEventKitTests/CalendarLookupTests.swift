import BridgeCore
import Foundation
import Testing

@testable import BridgeEventKit

/// The branch that decides which calendar a name means. A wrong answer here writes an appointment
/// into somebody's shared calendar, so every case that is not a single unambiguous match fails.
@Suite("Calendar lookup")
struct CalendarLookupTests {
    @Test("an exact match resolves")
    func exactMatch() throws {
        let candidates = [calendarCandidate("Privat"), calendarCandidate("Arbeit")]
        let match = try CalendarLookup.resolve(try calendarName("Privat"), in: candidates)
        #expect(match.title == "Privat")
    }

    /// Someone who typed `privat` for a calendar called `Privat` meant that calendar, and there is
    /// nothing to be gained by refusing.
    @Test("a unique case-insensitive match resolves", arguments: ["privat", "PRIVAT", "PriVat"])
    func caseInsensitiveMatch(spelling: String) throws {
        let candidates = [calendarCandidate("Privat"), calendarCandidate("Arbeit")]
        let match = try CalendarLookup.resolve(try calendarName(spelling), in: candidates)
        #expect(match.title == "Privat")
    }

    /// An exact match wins outright, even when a case-fold would find more. Otherwise a caller who
    /// spelled a name exactly right would be refused because of some *other* calendar's casing.
    @Test("an exact match beats a case-insensitive one")
    func exactBeatsFolded() throws {
        let candidates = [calendarCandidate("Privat", calendarId: "A"), calendarCandidate("privat", calendarId: "B")]
        #expect(try CalendarLookup.resolve(try calendarName("Privat"), in: candidates).calendarId == "A")
        #expect(try CalendarLookup.resolve(try calendarName("privat"), in: candidates).calendarId == "B")
    }

    @Test("a name nothing wears is no_such_calendar")
    func noMatch() throws {
        let candidates = [calendarCandidate("Privat"), calendarCandidate("Arbeit")]
        #expect(throws: ApiError.noSuchCalendar) {
            try CalendarLookup.resolve(try calendarName("Family"), in: candidates)
        }
        #expect(throws: ApiError.noSuchCalendar) {
            try CalendarLookup.resolve(try calendarName("Privat"), in: [])
        }
    }

    /// **The reason this type exists separately from `ListLookup`.** Two accounts can both hold a
    /// "Privat", the wire format carries no account, and there is no honest way to choose. The
    /// caller is told which of the two problems it has, because "rename one of them" and "check
    /// the spelling" are different fixes.
    @Test("two exact matches is ambiguous_calendar, not no_such_calendar")
    func exactAmbiguity() throws {
        let candidates = [
            calendarCandidate("Privat", calendarId: "icloud"),
            calendarCandidate("Privat", calendarId: "local"),
        ]
        #expect(throws: ApiError.ambiguousCalendar) {
            try CalendarLookup.resolve(try calendarName("Privat"), in: candidates)
        }
    }

    @Test("two case-insensitive matches is ambiguous too")
    func foldedAmbiguity() throws {
        let candidates = [
            calendarCandidate("Privat", calendarId: "icloud"),
            calendarCandidate("PRIVAT", calendarId: "local"),
        ]
        #expect(throws: ApiError.ambiguousCalendar) {
            try CalendarLookup.resolve(try calendarName("privat"), in: candidates)
        }
    }

    /// The two 4xx are genuinely different codes with different statuses, so a client can branch
    /// on them. This is what the .NET side's two distinct messages rest on.
    @Test("missing and ambiguous are distinguishable, unlike the list side")
    func missingAndAmbiguousAreDistinct() throws {
        let ambiguous = [calendarCandidate("Privat", calendarId: "a"), calendarCandidate("Privat", calendarId: "b")]

        var caught: [ApiError] = []
        for (name, candidates) in [("Privat", ambiguous), ("Family", ambiguous)] {
            do {
                _ = try CalendarLookup.resolve(try calendarName(name), in: candidates)
                Issue.record("\(name) should not resolve")
            } catch let error as ApiError {
                caught.append(error)
            }
        }
        #expect(caught == [.ambiguousCalendar, .noSuchCalendar])
        #expect(caught[0].httpStatus != caught[1].httpStatus)
    }

    /// A calendar whose title is not a usable `CalendarName` has no name a caller could send, so
    /// it matches nothing and its events stay invisible. Fails closed.
    @Test("a calendar with an unusable title is unaddressable", arguments: [
        "", "   ", "Pri\nvat", String(repeating: "a", count: Limits.calendarNameMaxLength + 1),
    ])
    func unusableTitle(title: String) throws {
        #expect(CalendarLookup.canonicalName(calendarCandidate(title)) == nil)
        // …and it cannot be matched even by something that looks like it.
        #expect(throws: ApiError.noSuchCalendar) {
            try CalendarLookup.resolve(try calendarName("Privat"), in: [calendarCandidate(title)])
        }
    }

    /// Resolution is by title only. Matching an identifier would let a caller address a calendar
    /// by a handle the wire format promises never to expose.
    @Test("a calendar identifier is never matched as a name")
    func identifiersAreNotNames() throws {
        let candidates = [calendarCandidate("Privat", calendarId: "CAL-DEADBEEF")]
        #expect(throws: ApiError.noSuchCalendar) {
            try CalendarLookup.resolve(try calendarName("CAL-DEADBEEF"), in: candidates)
        }
    }

    @Test("names with umlauts, emoji and spaces resolve like any other", arguments: [
        "Geburtstage", "Café ☕️", "Family / Shared", "日历", "🗓️",
    ])
    func unicodeNames(title: String) throws {
        let match = try CalendarLookup.resolve(try calendarName(title), in: [calendarCandidate(title)])
        #expect(match.title == title)
    }
}
