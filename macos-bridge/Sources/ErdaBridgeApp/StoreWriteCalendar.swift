import BridgeCore
import BridgeEventKit
import BridgeStore
import Foundation

/// Wires `BridgeEventKit`'s write-calendar seam onto the real SQLite repository.
///
/// It lives here for the same reason `StoreReminderIdentity` does: this is the only place both
/// modules are visible. `BridgeEventKit` links no SQLite, `BridgeStore` knows nothing about
/// EventKit, and the adapter between them is four lines.
///
/// It is read-only, and the seam has no write half at all — so there is no way for anything on the
/// request path to re-point the calendar, only for the setup window to.
struct StoreWriteCalendar: WriteCalendarStore {
    let bindings: CalendarBindingRepository

    func writeCalendar() throws -> CalendarBinding? {
        try bindings.load()
    }
}
