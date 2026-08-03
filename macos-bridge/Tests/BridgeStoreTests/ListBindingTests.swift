import BridgeCore
import Foundation
import Testing

@testable import BridgeStore

/// The one reminder list the bridge writes to, on disk. Same shape as `CalendarBindingTests`,
/// because it is the same kind of thing: a local choice, in `meta`, that no route can reach.
@Suite("List binding")
struct ListBindingTests {
    private let root = TemporaryRoot()

    private func binding(
        id: String = "1D6E8AF9-0C2E-4A1B-9C77-9F5B0A4C3E21",
        title: String = "Groceries"
    ) throws -> ListBinding {
        ListBinding(listId: id, titleAtBind: try #require(ListName(rawValue: title)))
    }

    @Test("nothing is configured until a human chooses — there is no built-in default")
    func startsUnconfigured() throws {
        let store = try root.open()
        #expect(try store.listBinding.load() == nil)
    }

    @Test("a saved binding round-trips, identifier and title alike")
    func roundTrips() throws {
        let store = try root.open()
        let chosen = try binding()

        try store.listBinding.save(chosen)
        #expect(try store.listBinding.load() == chosen)
    }

    @Test("saving again replaces the choice rather than accumulating rows")
    func overwrites() throws {
        let store = try root.open()
        try store.listBinding.save(try binding(id: "first", title: "Groceries"))
        try store.listBinding.save(try binding(id: "second", title: "Work"))

        #expect(try store.listBinding.load() == (try binding(id: "second", title: "Work")))

        let rows = try store.db.query(
            "SELECT COUNT(*) FROM meta WHERE k IN ('list_id', 'list_title')",
            table: "meta"
        ) { try $0.integer(0, "count") }
        #expect(rows == [2])
    }

    /// The point of persisting it at all: the bridge must write to the same list after a restart,
    /// without anyone re-confirming.
    @Test("the choice survives a reopen")
    func survivesReopen() throws {
        let chosen = try binding()
        let first = try root.open()
        try first.listBinding.save(chosen)
        first.close()

        let second = try root.open()
        #expect(try second.listBinding.load() == chosen)
    }

    /// A half-written pair cannot be *displayed*, and a write target nobody can confirm is one the
    /// bridge has no business using — so it reads as unconfigured, and creates answer 503.
    @Test("a half-written pair reads as unconfigured rather than as a guess")
    func rejectsPartialRows() throws {
        let store = try root.open()

        try store.meta.set("some-identifier", for: "list_id")
        #expect(try store.listBinding.load() == nil)

        try store.meta.remove("list_id")
        try store.meta.set("Groceries", for: "list_title")
        #expect(try store.listBinding.load() == nil)
    }

    @Test("a title that is no longer a usable name is not repaired into one")
    func rejectsUnusableTitle() throws {
        let store = try root.open()
        try store.meta.set("some-identifier", for: "list_id")

        for title in ["", "   ", "a\nb"] {
            try store.meta.set(title, for: "list_title")
            #expect(try store.listBinding.load() == nil, "accepted \(title.debugDescription)")
        }
    }

    @Test("clearing leaves nothing behind")
    func clears() throws {
        let store = try root.open()
        try store.listBinding.save(try binding())
        try store.listBinding.clear()

        #expect(try store.listBinding.load() == nil)
        #expect(try store.meta.value(for: "list_id") == nil)
        #expect(try store.meta.value(for: "list_title") == nil)
    }

    /// It shares `meta` with the bind address and the calendar binding, so the obvious way to get
    /// this wrong is for one to stomp another. The keys are distinct on purpose.
    @Test("the list binding, the calendar binding and the bind address do not disturb each other")
    func coexistsWithOtherMeta() throws {
        let store = try root.open()
        let selection = BindSelection(ipAddress: "192.168.178.103", port: 17832)
        let calendar = CalendarBinding(
            calendarId: "CAL-1",
            titleAtBind: try #require(CalendarName(rawValue: "Privat"))
        )

        try store.bindSettings.save(selection)
        try store.calendarBinding.save(calendar)
        try store.listBinding.save(try binding())

        #expect(try store.bindSettings.load() == selection)
        #expect(try store.calendarBinding.load() == calendar)
        #expect(try store.listBinding.load() == (try binding()))

        try store.listBinding.clear()
        #expect(try store.bindSettings.load() == selection)
        #expect(try store.calendarBinding.load() == calendar)
        #expect(try Schema.readVersion(store.db) == Schema.currentVersion)
    }
}
