import BridgeCore
import EventKit
import Foundation

/// A fetched reminder, flattened to a `Sendable` value.
///
/// **This is the isolation boundary.** `EKReminder` carries no `Sendable` annotation anywhere in
/// the EventKit headers, so it is a non-`Sendable` class under Swift 6 and the compiler will not
/// let one out of the fetch completion closure. Everything that closure is allowed to produce is
/// a `RawReminder`, which is why `init(_ reminder:)` is the only place in the whole package that
/// reads EventKit properties off an object it does not own.
///
/// It is deliberately *not* a `ReminderSnapshot`: it still speaks EventKit's identifiers. Turning
/// `itemId` into a `BridgeID` and `calendarId` into an `Alias` needs the identity store and the
/// allowlist, neither of which may be touched from a completion block, so that happens back on
/// the actor in `EventKitReminders`.
struct RawReminder: Sendable, Equatable {
    let itemId: String
    /// `calendarItemExternalIdentifier`, carried for diagnostics only — the header lists four
    /// ways it can be duplicated inside one database, so it never resolves anything.
    let externalId: String?
    /// `nil` when the reminder has no calendar. `EKCalendarItem.calendar` is `null_unspecified`,
    /// so Swift imports it as an implicitly-unwrapped optional that really can be nil.
    let calendarId: String?
    let title: String
    let notes: String?
    let dueAt: Date?
    let priority: Int
    let isCompleted: Bool
    let completedAt: Date?

    init(_ reminder: EKReminder) {
        self.itemId = reminder.calendarItemIdentifier
        let external = reminder.calendarItemExternalIdentifier
        self.externalId = (external?.isEmpty == false) ? external : nil
        self.calendarId = reminder.calendar?.calendarIdentifier
        // `title` is `null_unspecified` too; an untitled reminder becomes an empty string rather
        // than being dropped, so the caller still sees that it exists.
        self.title = reminder.title ?? ""
        self.notes = reminder.notes
        self.dueAt = DueDate.date(from: reminder.dueDateComponents)
        // Declared `NSUInteger` in the header but imported as `Int`; EventKit only ever stores
        // 0…9 here, because anything else fails the save.
        self.priority = reminder.priority
        self.isCompleted = reminder.isCompleted
        self.completedAt = reminder.completionDate
    }

    /// Memberwise access for tests, which have no way to build an `EKReminder`.
    init(
        itemId: String,
        externalId: String? = nil,
        calendarId: String?,
        title: String,
        notes: String? = nil,
        dueAt: Date? = nil,
        priority: Int = 0,
        isCompleted: Bool = false,
        completedAt: Date? = nil
    ) {
        self.itemId = itemId
        self.externalId = externalId
        self.calendarId = calendarId
        self.title = title
        self.notes = notes
        self.dueAt = dueAt
        self.priority = priority
        self.isCompleted = isCompleted
        self.completedAt = completedAt
    }

    /// The wire shape, once the caller has resolved the two identifiers it cannot resolve itself.
    func snapshot(id: BridgeID, alias: Alias) -> ReminderSnapshot {
        ReminderSnapshot(
            id: id,
            alias: alias,
            title: title,
            notes: notes,
            dueAt: dueAt,
            priority: priority,
            isCompleted: isCompleted,
            completedAt: completedAt
        )
    }
}

/// Construction of `EKReminder.dueDateComponents`, which has two documented traps.
enum DueDate {
    /// Builds due-date components for `date` as seen from `timeZone`.
    ///
    /// Two rules from `EKReminder.h`, both of which are silent or fatal if broken:
    ///
    /// 1. *"If you set this property, the calendar must be set to NSCalendarIdentifierGregorian.
    ///    An exception is raised otherwise."* An Objective-C exception is not catchable in Swift,
    ///    so a user whose system calendar is Buddhist or Japanese would take the whole bridge
    ///    process down. The calendar is therefore constructed here and never read from the
    ///    environment — `Calendar.current` is never used.
    /// 2. *"Setting a date component without a hour, minute and second component will set allDay
    ///    to YES."* Hour, minute and second are always included, so a timed request can never
    ///    silently become an all-day reminder.
    static func components(for date: Date, timeZone: TimeZone) -> DateComponents {
        var gregorian = Calendar(identifier: .gregorian)
        gregorian.timeZone = timeZone

        var components = gregorian.dateComponents(
            [.year, .month, .day, .hour, .minute, .second],
            from: date
        )
        components.calendar = gregorian
        components.timeZone = timeZone
        return components
    }

    /// The inverse, for reading a reminder back.
    ///
    /// Reminders written by other clients can arrive with a nil `calendar`, which makes
    /// `DateComponents.date` return nil; a Gregorian calendar is substituted rather than losing
    /// the due date. A component set with no date fields at all still yields nil.
    static func date(from components: DateComponents?) -> Date? {
        guard let components else { return nil }
        guard components.year != nil || components.month != nil || components.day != nil else {
            return nil
        }
        if let date = components.date { return date }

        var gregorian = Calendar(identifier: .gregorian)
        if let timeZone = components.timeZone { gregorian.timeZone = timeZone }
        return gregorian.date(from: components)
    }
}
