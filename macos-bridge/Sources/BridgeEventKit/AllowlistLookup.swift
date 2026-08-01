import BridgeCore
import Foundation

extension Allowlist {
    /// The reverse lookup `complete` performs against a reminder's **current** calendar.
    ///
    /// Only healthy entries match. A reminder sitting in a list whose alias has gone `broken`
    /// therefore becomes unreachable rather than quietly still writable — the same fail-closed
    /// rule as `resolve`, applied from the other direction.
    ///
    /// Extracted from the actor so it is testable without EventKit: this predicate decides
    /// whether a reminder id is 404 or not, which makes it the single most security-relevant
    /// branch in the module.
    func alias(forCalendarId calendarId: String) -> Alias? {
        for alias in healthyAliases where entry(for: alias)?.calendarId == calendarId {
            return alias
        }
        return nil
    }
}
