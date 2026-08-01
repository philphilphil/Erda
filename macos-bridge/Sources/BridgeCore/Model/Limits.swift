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

    public static let aliasMaxLength = 32

    public static let listLimitDefault = 100
    public static let listLimitMax = 200

    public static let idempotencyKeyMaxLength = 200
}
