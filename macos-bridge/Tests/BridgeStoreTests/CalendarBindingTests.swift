import BridgeCore
import Foundation
import Testing

@testable import BridgeStore

/// The one calendar the bridge writes to, on disk. Same shape as `BindSettingsTests`, because it is
/// the same kind of thing: a local choice, in `meta`, that no route can reach.
@Suite("Calendar binding")
struct CalendarBindingTests {
    private let root = TemporaryRoot()

    private func binding(
        id: String = "1D6E8AF9-0C2E-4A1B-9C77-9F5B0A4C3E21",
        title: String = "Privat"
    ) throws -> CalendarBinding {
        CalendarBinding(calendarId: id, titleAtBind: try #require(CalendarName(rawValue: title)))
    }

    @Test("nothing is configured until a human chooses — there is no built-in default")
    func startsUnconfigured() throws {
        let store = try root.open()
        #expect(try store.calendarBinding.load() == nil)
    }

    @Test("a saved binding round-trips, identifier and title alike")
    func roundTrips() throws {
        let store = try root.open()
        let chosen = try binding()

        try store.calendarBinding.save(chosen)
        #expect(try store.calendarBinding.load() == chosen)
    }

    @Test("saving again replaces the choice rather than accumulating rows")
    func overwrites() throws {
        let store = try root.open()
        try store.calendarBinding.save(try binding(id: "first", title: "Privat"))
        try store.calendarBinding.save(try binding(id: "second", title: "Arbeit"))

        #expect(try store.calendarBinding.load() == (try binding(id: "second", title: "Arbeit")))

        let rows = try store.db.query(
            "SELECT COUNT(*) FROM meta WHERE k IN ('calendar_id', 'calendar_title')",
            table: "meta"
        ) { try $0.integer(0, "count") }
        #expect(rows == [2])
    }

    /// The point of persisting it at all: the bridge must write to the same calendar after a
    /// restart, without anyone re-confirming.
    @Test("the choice survives a reopen")
    func survivesReopen() throws {
        let chosen = try binding()
        let first = try root.open()
        try first.calendarBinding.save(chosen)
        first.close()

        let second = try root.open()
        #expect(try second.calendarBinding.load() == chosen)
    }

    /// A half-written pair cannot be *displayed*, and a write target nobody can confirm is one the
    /// bridge has no business using — so it reads as unconfigured, and creates answer 503.
    @Test("a half-written pair reads as unconfigured rather than as a guess")
    func rejectsPartialRows() throws {
        let store = try root.open()

        try store.meta.set("some-identifier", for: "calendar_id")
        #expect(try store.calendarBinding.load() == nil)

        try store.meta.remove("calendar_id")
        try store.meta.set("Privat", for: "calendar_title")
        #expect(try store.calendarBinding.load() == nil)
    }

    @Test("a title that is no longer a usable name is not repaired into one")
    func rejectsUnusableTitle() throws {
        let store = try root.open()
        try store.meta.set("some-identifier", for: "calendar_id")

        for title in ["", "   ", "a\nb"] {
            try store.meta.set(title, for: "calendar_title")
            #expect(try store.calendarBinding.load() == nil, "accepted \(title.debugDescription)")
        }
    }

    @Test("clearing leaves nothing behind")
    func clears() throws {
        let store = try root.open()
        try store.calendarBinding.save(try binding())
        try store.calendarBinding.clear()

        #expect(try store.calendarBinding.load() == nil)
        #expect(try store.meta.value(for: "calendar_id") == nil)
        #expect(try store.meta.value(for: "calendar_title") == nil)
    }

    /// It shares `meta` with the bind address, so the obvious way to get this wrong is for one to
    /// stomp the other.
    @Test("the calendar binding and the bind address do not disturb each other")
    func coexistsWithBindSettings() throws {
        let store = try root.open()
        let selection = BindSelection(ipAddress: "192.168.178.103", port: 17832)

        try store.bindSettings.save(selection)
        try store.calendarBinding.save(try binding())

        #expect(try store.bindSettings.load() == selection)
        #expect(try store.calendarBinding.load() == (try binding()))

        try store.calendarBinding.clear()
        #expect(try store.bindSettings.load() == selection)
        #expect(try Schema.readVersion(store.db) == Schema.currentVersion)
    }
}
