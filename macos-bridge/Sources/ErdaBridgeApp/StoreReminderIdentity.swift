import BridgeCore
import BridgeEventKit
import BridgeStore
import Foundation

/// Wires `BridgeEventKit`'s identity seam onto the real SQLite repositories.
///
/// It lives here rather than in either module because this is the only place both are visible:
/// `BridgeEventKit` links no SQLite, and `BridgeStore` knows nothing about EventKit. The adapter
/// is the whole of the coupling between them, and it is thirty lines.
struct StoreReminderIdentity: ReminderIdentityStore {
    let reminderMap: ReminderMapRepository

    func recordMapping(
        bridgeId: BridgeID,
        itemId: String,
        externalId: String?,
        list: ListName,
        at date: Date
    ) throws {
        try reminderMap.insert(
            ReminderMapEntry(
                bridgeId: bridgeId,
                eventKitItemId: itemId,
                eventKitExternalId: externalId,
                listName: list,
                createdAt: date,
                lastSeenAt: date
            )
        )
    }

    func itemId(for bridgeId: BridgeID) throws -> String? {
        try reminderMap.entry(for: bridgeId)?.eventKitItemId
    }

    func bridgeId(forItemId itemId: String) throws -> BridgeID? {
        try reminderMap.entry(forEventKitItemId: itemId)?.bridgeId
    }

    func touch(_ bridgeId: BridgeID, at date: Date) throws {
        try reminderMap.touch(bridgeId, at: date)
    }
}
