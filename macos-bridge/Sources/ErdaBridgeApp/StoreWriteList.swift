import BridgeCore
import BridgeEventKit
import BridgeStore
import Foundation

/// Wires `BridgeEventKit`'s write-list seam onto the real SQLite repository.
///
/// The reminder counterpart of `StoreWriteCalendar`, and here for the same reason: this is the only
/// place both modules are visible. `BridgeEventKit` links no SQLite, `BridgeStore` knows nothing
/// about EventKit, and the adapter between them is four lines.
///
/// It is read-only, and the seam has no write half at all — so there is no way for anything on the
/// request path to re-point the list, only for the setup window to.
struct StoreWriteList: WriteListStore {
    let bindings: ListBindingRepository

    func writeList() throws -> ListBinding? {
        try bindings.load()
    }
}
