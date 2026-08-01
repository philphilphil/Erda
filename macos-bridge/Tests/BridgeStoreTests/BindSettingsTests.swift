import BridgeCore
import Foundation
import Testing

@testable import BridgeStore

@Suite("Bind settings")
struct BindSettingsTests {
    private let root = TemporaryRoot()

    @Test("nothing is configured until a human chooses — there is no built-in default")
    func startsUnconfigured() throws {
        let store = try root.open()
        #expect(try store.bindSettings.load() == nil)
    }

    @Test("a saved choice round-trips")
    func roundTrips() throws {
        let store = try root.open()
        let selection = BindSelection(ipAddress: "192.168.178.103", port: 17832)

        try store.bindSettings.save(selection)
        #expect(try store.bindSettings.load() == selection)
    }

    @Test("saving again replaces the choice rather than accumulating rows")
    func overwrites() throws {
        let store = try root.open()
        try store.bindSettings.save(BindSelection(ipAddress: "192.168.178.106", port: 17832))
        try store.bindSettings.save(BindSelection(ipAddress: "192.168.178.103", port: 18000))

        #expect(try store.bindSettings.load() == BindSelection(ipAddress: "192.168.178.103", port: 18000))

        let rows = try store.db.query(
            "SELECT COUNT(*) FROM meta WHERE k IN ('bind_ip', 'port')",
            table: "meta"
        ) { try $0.integer(0, "count") }
        #expect(rows == [2])
    }

    @Test("the choice survives a reopen — it is the listener's address across launches")
    func survivesReopen() throws {
        let selection = BindSelection(ipAddress: "192.168.178.103", port: 17832)
        let first = try root.open()
        try first.bindSettings.save(selection)
        first.close()

        let second = try root.open()
        #expect(try second.bindSettings.load() == selection)
    }

    @Test("a half-written row reads as unconfigured rather than as a guess")
    func rejectsPartialRows() throws {
        let store = try root.open()

        try store.meta.set("192.168.178.103", for: "bind_ip")
        #expect(try store.bindSettings.load() == nil)

        try store.meta.remove("bind_ip")
        try store.meta.set("17832", for: "port")
        #expect(try store.bindSettings.load() == nil)
    }

    @Test("a port that is not an integer is not repaired into one")
    func rejectsNonNumericPort() throws {
        let store = try root.open()
        try store.meta.set("192.168.178.103", for: "bind_ip")
        try store.meta.set("seventeen thousand", for: "port")

        #expect(try store.bindSettings.load() == nil)
    }

    @Test("clearing leaves nothing behind")
    func clears() throws {
        let store = try root.open()
        try store.bindSettings.save(BindSelection(ipAddress: "127.0.0.1", port: 17832))
        try store.bindSettings.clear()

        #expect(try store.bindSettings.load() == nil)
        #expect(try store.meta.value(for: "bind_ip") == nil)
        #expect(try store.meta.value(for: "port") == nil)
    }

    @Test("the value is stored verbatim — validation is the supervisor's job at every start")
    func storesVerbatim() throws {
        // An address that is certainly not on this machine's interfaces still saves: it may be
        // the one the router hands back after the next lease, and refusing to remember it would
        // make a DHCP blip lose the configuration entirely.
        let store = try root.open()
        try store.bindSettings.save(BindSelection(ipAddress: "10.99.99.99", port: 17832))

        #expect(try store.bindSettings.load()?.ipAddress == "10.99.99.99")
    }

    @Test("the schema version key is untouched by bind settings")
    func leavesSchemaVersionAlone() throws {
        let store = try root.open()
        try store.bindSettings.save(BindSelection(ipAddress: "127.0.0.1", port: 17832))

        #expect(try Schema.readVersion(store.db) == Schema.currentVersion)
    }
}
