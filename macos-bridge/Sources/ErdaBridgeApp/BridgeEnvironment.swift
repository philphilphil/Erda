import BridgeCore
import BridgeEventKit
import BridgeHTTP
import BridgeStore
import Foundation

/// The composition root: the real store, the real crypto, the real EventKit service and the
/// listener supervisor, assembled once and owned for the life of the process.
///
/// Assembly deliberately does **not** bind a socket. Binding is the supervisor's job, it happens
/// on a schedule rather than once, and it can fail for reasons (a moved DHCP lease, an address
/// nobody has chosen yet) that must not stop the app from launching and showing why.
struct BridgeEnvironment: Sendable {
    let directories: BridgeDirectories
    let store: BridgeStoreHandle
    /// Wraps the durable JSONL sink so the setup UI can show the last request without reading the
    /// file back.
    let audit: LastEventAuditSink
    let tokenService: TokenService
    let supervisor: ServerSupervisor

    /// What the port field is pre-filled with when nothing has been stored yet. It is a UI
    /// convenience only — never a fallback, and never combined with a guessed address.
    static let suggestedPort = 17832

    /// Loopback is selectable here, unlike `BindAddressPolicy.production`.
    ///
    /// Binding `127.0.0.1` makes the bridge unreachable from Erda, which is why the production
    /// policy refuses it — but it is also the only way to exercise the HTTP surface on this Mac
    /// without involving the Application Firewall or the LAN. So the *policy* permits it and the
    /// *UI* is responsible for saying, every time, that Erda cannot reach it.
    static let bindPolicy = BindAddressPolicy(allowLoopback: true)

    static func make() throws -> BridgeEnvironment {
        let directories = try BridgeDirectories.standard()
        let store = try BridgeStoreHandle.open(directories: directories)
        let audit = LastEventAuditSink(
            wrapping: try RotatingJSONLAuditSink(directory: directories.logs)
        )

        // Rows left behind by a process that died mid-request are swept before the listener
        // opens, so a retried key is never blocked by a ghost.
        _ = try? store.idempotency.sweepExpired()

        let tokenStore = TokenStoreFactory.make(backend: .keychain, directories: directories)
        let tokenService = TokenService(store: tokenStore)

        // Every reminder list on this Mac is in scope; the actor resolves names against EventKit
        // per request, so a list created or renamed in Reminders.app takes effect without a
        // restart and nothing here needs to be re-read.
        let reminders = EventKitReminders(
            identity: StoreReminderIdentity(reminderMap: store.reminderMap)
        )

        let services = BridgeServices(
            reminders: reminders,
            // Re-read per request, so rotating the token in the setup UI takes effect at once
            // rather than at the next restart.
            tokenVerifier: { try? tokenService.currentVerifier() },
            rateLimiter: RateLimiter(clock: SystemClock()),
            idempotency: store.idempotency,
            audit: audit,
            hasher: CryptoKitSha256Hasher()
        )

        let bindSettings = store.bindSettings
        let supervisor = ServerSupervisor(
            services: services,
            selection: { (try? bindSettings.load()) ?? nil },
            policy: bindPolicy
        )

        return BridgeEnvironment(
            directories: directories,
            store: store,
            audit: audit,
            tokenService: tokenService,
            supervisor: supervisor
        )
    }

    /// Generates a token and prints it once, for the `--rotate-token` command-line path.
    ///
    /// The setup UI is the normal route now; this stays because it works before any window has
    /// been opened, and because the README documents it. It still writes the token nowhere.
    static func rotateToken() throws -> GeneratedToken {
        let directories = try BridgeDirectories.standard()
        try directories.create()
        let store = TokenStoreFactory.make(backend: .keychain, directories: directories)
        return try TokenService(store: store).rotate()
    }
}
