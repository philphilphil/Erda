import BridgeCore
import Foundation
import Testing

@testable import BridgeEventKit

@Suite("Due-date components")
struct DueDateTests {
    private let berlin = TimeZone(identifier: "Europe/Berlin")!
    private let tokyo = TimeZone(identifier: "Asia/Tokyo")!
    /// 2026-08-01T14:30:00Z.
    private let instant = Date(timeIntervalSince1970: 1_785_940_200)

    /// `EKReminder.h`: *"If you set this property, the calendar must be set to
    /// NSCalendarIdentifierGregorian. An exception is raised otherwise."* An Objective-C
    /// exception is not catchable from Swift, so this is the difference between a 400 and the
    /// bridge process dying.
    @Test("the calendar is always Gregorian, whatever the system calendar is")
    func alwaysGregorian() {
        let components = DueDate.components(for: instant, timeZone: berlin)
        #expect(components.calendar?.identifier == .gregorian)
    }

    /// `EKReminder.h`: *"Setting a date component without a hour, minute and second component
    /// will set allDay to YES."* A timed request must never silently become an all-day reminder.
    @Test("hour, minute and second are always present so allDay is never set behind our back")
    func alwaysCarriesTimeOfDay() {
        let components = DueDate.components(for: instant, timeZone: berlin)
        #expect(components.hour != nil)
        #expect(components.minute != nil)
        #expect(components.second != nil)
        #expect(components.year != nil)
        #expect(components.month != nil)
        #expect(components.day != nil)
    }

    @Test("a midnight instant still carries an explicit 0:00:00 rather than becoming all-day")
    func midnightIsStillTimed() throws {
        // 2026-08-01T00:00:00+02:00 — the case where a naive implementation drops the time
        // fields because they are all zero.
        let midnight = Date(timeIntervalSince1970: 1_785_888_000 - 7200)
        let components = DueDate.components(for: midnight, timeZone: berlin)
        #expect(components.hour == 0)
        #expect(components.minute == 0)
        #expect(components.second == 0)
    }

    @Test("components are expressed in the configured zone, not the machine's")
    func expressedInConfiguredZone() {
        let inBerlin = DueDate.components(for: instant, timeZone: berlin)
        let inTokyo = DueDate.components(for: instant, timeZone: tokyo)

        // 14:30 UTC is 16:30 in Berlin (CEST) and 23:30 in Tokyo.
        #expect(inBerlin.hour == 16)
        #expect(inBerlin.minute == 30)
        #expect(inTokyo.hour == 23)
        #expect(inTokyo.minute == 30)
        #expect(inBerlin.timeZone == berlin)
        #expect(inTokyo.timeZone == tokyo)
    }

    @Test("both zones still describe the same instant", arguments: [
        "Europe/Berlin", "Asia/Tokyo", "UTC", "America/Los_Angeles", "Australia/Lord_Howe",
    ])
    func roundTripsToTheSameInstant(zoneName: String) throws {
        let zone = try #require(TimeZone(identifier: zoneName))
        let components = DueDate.components(for: instant, timeZone: zone)
        #expect(DueDate.date(from: components) == instant)
    }

    @Test("a component set with no calendar falls back to Gregorian rather than losing the date")
    func recoversFromAMissingCalendar() throws {
        var components = DueDate.components(for: instant, timeZone: berlin)
        // What a reminder written by another client can look like when read back.
        components.calendar = nil
        #expect(DueDate.date(from: components) == instant)
    }

    @Test("nil and empty component sets read back as no due date")
    func noDueDate() {
        #expect(DueDate.date(from: nil) == nil)
        #expect(DueDate.date(from: DateComponents()) == nil)
        // Time of day alone is not a date.
        #expect(DueDate.date(from: DateComponents(hour: 9, minute: 0)) == nil)
    }

    @Test("a date-only component set still reads back, as local midnight")
    func dateOnlyStillReads() throws {
        // The all-day shape EventKit produces when another client set a date with no time.
        var gregorian = Calendar(identifier: .gregorian)
        gregorian.timeZone = berlin
        var components = DateComponents()
        components.year = 2026
        components.month = 8
        components.day = 1
        components.timeZone = berlin

        let read = try #require(DueDate.date(from: components))
        #expect(gregorian.dateComponents([.year, .month, .day], from: read).day == 1)
    }
}
