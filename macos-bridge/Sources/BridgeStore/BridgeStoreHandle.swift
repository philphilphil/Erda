import BridgeCore
import Foundation

/// Everything the store owns, opened and migrated in one step.
public struct BridgeStoreHandle: Sendable {
    public let directories: BridgeDirectories
    public let db: SQLiteDB
    public let meta: MetaRepository
    public let bindSettings: BindSettingsRepository
    /// The single reminder list creates land in. Local-only, like `bindSettings` — no route reads
    /// or writes it.
    public let listBinding: ListBindingRepository
    /// The single calendar creates land in. Local-only, like `bindSettings` — no route reads or
    /// writes it.
    public let calendarBinding: CalendarBindingRepository
    public let reminderMap: ReminderMapRepository
    public let idempotency: IdempotencyRepository
    public let schemaVersion: Int

    /// Creates the directories 0700, opens (or creates) the database 0600, migrates it, and
    /// hardens the WAL sidecars — which do not exist until the first write, so this has to
    /// happen after the migration rather than before.
    public static func open(
        directories: BridgeDirectories,
        clock: any BridgeClock = SystemClock()
    ) throws -> BridgeStoreHandle {
        try directories.create()

        let db = try SQLiteDB(path: directories.databaseURL.path)
        let version = try Schema.migrate(db)
        try directories.hardenDatabaseFiles()

        let meta = MetaRepository(db: db)
        return BridgeStoreHandle(
            directories: directories,
            db: db,
            meta: meta,
            bindSettings: BindSettingsRepository(meta: meta),
            listBinding: ListBindingRepository(meta: meta),
            calendarBinding: CalendarBindingRepository(meta: meta),
            reminderMap: ReminderMapRepository(db: db),
            idempotency: IdempotencyRepository(db: db, clock: clock),
            schemaVersion: version
        )
    }

    public func close() {
        db.close()
    }
}
