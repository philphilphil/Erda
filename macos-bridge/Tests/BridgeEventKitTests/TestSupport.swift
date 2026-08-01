import BridgeCore
import Foundation
import Testing

@testable import BridgeEventKit

func listName(_ raw: String, sourceLocation: SourceLocation = #_sourceLocation) throws -> ListName {
    try #require(ListName(rawValue: raw), sourceLocation: sourceLocation)
}

func calendarName(_ raw: String, sourceLocation: SourceLocation = #_sourceLocation) throws -> CalendarName {
    try #require(CalendarName(rawValue: raw), sourceLocation: sourceLocation)
}

/// One entry of the list table `ListLookup` resolves against, with a calendar id that is obviously
/// not derived from the title — resolution must never fall back to matching identifiers.
func candidate(_ title: String, calendarId: String? = nil) -> ListLookup.Candidate {
    ListLookup.Candidate(calendarId: calendarId ?? "CAL-\(UUID().uuidString)", title: title)
}

/// The same, for `CalendarLookup`. A distinct function rather than an overload on return type:
/// every call site would otherwise depend on inference to pick a namespace.
func calendarCandidate(_ title: String, calendarId: String? = nil) -> CalendarLookup.Candidate {
    CalendarLookup.Candidate(calendarId: calendarId ?? "CAL-\(UUID().uuidString)", title: title)
}

/// `EKErrorDomain` and its ordinals, written out rather than imported.
///
/// The test target deliberately does **not** `import EventKit` — `BridgeEventKit` is the only
/// module allowed to — so these are transcribed from
/// `MacOSX.sdk/…/EventKit.framework/Headers/EKError.h`. Transcribing them is the stronger
/// assertion anyway: it pins the integers EventKit actually puts in an `NSError`, so a mapping
/// that silently stopped matching would fail here rather than in production.
enum EKErrorFixture {
    static let domain = "EKErrorDomain"

    static let noCalendar = 1
    static let noStartDate = 2
    static let noEndDate = 3
    static let datesInverted = 4
    static let internalFailure = 5
    static let calendarReadOnly = 6
    static let startDateTooFarInFuture = 9
    static let invalidSpan = 13
    static let calendarIsImmutable = 16
    static let recurringReminderRequiresDueDate = 18
    static let calendarDoesNotAllowEvents = 22
    static let calendarDoesNotAllowReminders = 23
    static let sourceDoesNotAllowReminders = 24
    static let sourceDoesNotAllowEvents = 25
    static let priorityIsInvalid = 26
    static let eventStoreNotAuthorized = 29

    static func error(_ code: Int) -> NSError {
        NSError(
            domain: domain,
            code: code,
            // A realistic payload: the mapper must never let any of this reach the wire.
            userInfo: [
                NSLocalizedDescriptionKey: "The operation couldn’t be completed.",
                NSFilePathErrorKey: "/Users/somebody/Library/Calendars/x.caldav",
            ]
        )
    }
}

/// Deterministic, so a failure reproduces instead of appearing once a week.
struct SeededGenerator: RandomNumberGenerator {
    private var state: UInt64

    init(seed: UInt64) {
        self.state = seed == 0 ? 0x9E37_79B9_7F4A_7C15 : seed
    }

    mutating func next() -> UInt64 {
        // xorshift64*
        state ^= state >> 12
        state ^= state << 25
        state ^= state >> 27
        return state &* 2_685_821_657_736_338_717
    }

    mutating func next(upperBound: UInt64) -> UInt64 {
        upperBound == 0 ? 0 : next() % upperBound
    }
}
