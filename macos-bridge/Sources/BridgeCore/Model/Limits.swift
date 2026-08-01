import Foundation

/// Every size cap the request layer enforces, in one place so a test can quote them.
public enum Limits {
    /// Title, after trimming leading/trailing whitespace.
    public static let titleMinLength = 1
    public static let titleMaxLength = 512
    public static let notesMaxLength = 4096

    /// `EKReminder.priority` is 0 (none) or 1 (highest) … 9 (lowest); anything else fails
    /// the save with `EKErrorPriorityIsInvalid`, so it is rejected at the edge instead.
    public static let priorityRange = 0...9

    /// A Reminders list title. The cap is generous because the name is the user's, not ours —
    /// it exists so a name cannot be used to bloat a log line or a database row, not to police
    /// what someone may call their list.
    public static let listNameMinLength = 1
    public static let listNameMaxLength = 128

    public static let listLimitDefault = 100
    public static let listLimitMax = 200

    /// A calendar title. Same reasoning and same cap as a list title — see `NameHygiene` for why
    /// the two are validated identically.
    public static let calendarNameMinLength = 1
    public static let calendarNameMaxLength = 128

    /// An event's length. `EKEvent` itself imposes no ceiling, so this one is ours: the bridge
    /// creates single appointments, and a multi-week block is far more likely to be a model that
    /// got a year wrong than something anyone meant. Refusing it costs a retry; writing it puts a
    /// week-long band across a real calendar.
    public static let eventMaxDuration: TimeInterval = 7 * 24 * 60 * 60

    /// How far ahead `GET /v1/calendar-events` looks, in days.
    ///
    /// The default is the answer to "what's coming up"; the cap bounds the fetch, because
    /// `predicateForEvents(withStart:end:calendars:)` expands every recurrence in the window and a
    /// years-wide window on a busy calendar is a very slow synchronous call on the actor's queue.
    public static let eventWindowDefaultDays = 7
    public static let eventWindowMinDays = 1
    public static let eventWindowMaxDays = 31

    public static let eventLimitDefault = 50
    public static let eventLimitMax = 200

    public static let idempotencyKeyMaxLength = 200
}
