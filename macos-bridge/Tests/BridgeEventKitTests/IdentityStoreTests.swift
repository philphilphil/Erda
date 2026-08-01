import BridgeCore
import Foundation
import Testing

@testable import BridgeEventKit

@Suite("In-memory identity store")
struct MemoryReminderIdentityStoreTests {
    private let now = Date(timeIntervalSince1970: 1_785_940_200)

    @Test("a mapping resolves in both directions")
    func resolvesBothWays() throws {
        let store = MemoryReminderIdentityStore()
        let id = BridgeID.generate()
        try store.recordMapping(bridgeId: id, itemId: "ek-1", externalId: "x", alias: try alias("inbox"), at: now)

        #expect(try store.itemId(for: id) == "ek-1")
        #expect(try store.bridgeId(forItemId: "ek-1") == id)
    }

    @Test("an unmapped id resolves to nothing — which is what makes it a 404")
    func unmappedResolvesToNil() throws {
        let store = MemoryReminderIdentityStore()
        #expect(try store.itemId(for: .generate()) == nil)
        #expect(try store.bridgeId(forItemId: "never-seen") == nil)
    }

    @Test("touching an unknown id is a no-op rather than an error")
    func touchIsForgiving() throws {
        let store = MemoryReminderIdentityStore()
        let id = BridgeID.generate()
        try store.touch(id, at: now)
        #expect(store.lastSeen(for: id) == nil)
    }

    @Test("touching advances the pruning clock")
    func touchAdvancesLastSeen() throws {
        let store = MemoryReminderIdentityStore()
        let id = BridgeID.generate()
        try store.recordMapping(bridgeId: id, itemId: "ek-1", externalId: nil, alias: try alias("inbox"), at: now)
        try store.touch(id, at: now.addingTimeInterval(3600))

        #expect(store.lastSeen(for: id) == now.addingTimeInterval(3600))
    }

    @Test("marking an alias broken records it and nothing else")
    func marksBroken() throws {
        let store = MemoryReminderIdentityStore()
        try store.markAliasBroken(try alias("gone"))
        #expect(store.brokenAliases == [try alias("gone")])
        #expect(store.mappingCount == 0)
    }

    @Test("an injected write failure surfaces on the mutating calls only")
    func writeFailure() throws {
        let store = MemoryReminderIdentityStore()
        store.setWriteFailure(ApiError.internal)

        #expect(throws: ApiError.internal) {
            try store.recordMapping(bridgeId: .generate(), itemId: "i", externalId: nil, alias: try alias("inbox"), at: now)
        }
        #expect(throws: ApiError.internal) { try store.markAliasBroken(try alias("inbox")) }
        // Reads keep working, so the actor can still tell "no mapping" from "cannot write".
        #expect(try store.itemId(for: .generate()) == nil)
    }
}
