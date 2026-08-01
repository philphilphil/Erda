import AppKit
import BridgeCore
import BridgeEventKit
import BridgeHTTP
import BridgeStore
import Foundation
import Observation

/// Everything the menu and the setup window display, and every action they can take.
///
/// `@MainActor` throughout, so no EventKit object and no SQLite handle is ever touched from two
/// places at once, and the `Sendable` values that come back from the supervisor's state stream are
/// the only things crossing an isolation boundary.
///
/// The rule this type exists to enforce: **never claim readiness that is not there.** Every
/// readout is derived from something just measured — the authorization status, the allowlist
/// table, the supervisor's own state — and `readiness` is a conjunction of all three, so no
/// single green light can stand in for the others.
@MainActor
@Observable
final class AppModel {
    // MARK: - Assembly

    private let environment: BridgeEnvironment?
    private(set) var startupError: String?

    // MARK: - Observed state

    private(set) var authorization: RemindersAuthorization = RemindersAccess.status()
    private(set) var serverState: ServerState = .stopped
    private(set) var lists: [ReminderListInfo] = []
    /// Distinguishes "no lists" from "we have not been allowed to look".
    private(set) var listsLoaded = false
    private(set) var allowlist: [AllowlistEntry] = []
    private(set) var tokenSummary: TokenSummary?
    private(set) var lastRequest: AuditEvent?
    private(set) var requestCount = 0
    private(set) var storedSelection: BindSelection?
    private(set) var addressChoices: [AddressChoice] = []

    // MARK: - Editor state

    var draftAddress = ""
    var draftPort = ""
    private(set) var bindError: String?
    private(set) var actionError: String?

    /// The plaintext token, held only between rotating it and the user dismissing the panel. It
    /// is never written anywhere, and rotating again replaces it.
    private(set) var revealedToken: String?

    // MARK: - Lifecycle

    init() {
        do {
            environment = try BridgeEnvironment.make()
        } catch {
            environment = nil
            startupError = String(describing: error)
        }

        reloadAll()
        loadDraftFromStore()
        observeServerState()
        startPolling()

        if let environment {
            Task { await environment.supervisor.start() }
        }
    }

    private func observeServerState() {
        guard let environment else { return }
        Task {
            for await state in environment.supervisor.states {
                serverState = state
            }
        }
    }

    /// A slow poll of the three cheap readouts: the authorization status (a class method), the
    /// allowlist table (six columns of local SQLite) and the last audited request. Reminder lists
    /// are deliberately not in here — each read builds a fresh `EKEventStore`, so those refresh on
    /// a gesture instead.
    private func startPolling() {
        Task {
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(5))
                let status = RemindersAccess.status()
                let usabilityChanged = status.isUsable != authorization.isUsable
                authorization = status
                // Access granted (or revoked) in System Settings while the app runs. Only that
                // transition pays for a list read, so the poll stays cheap.
                if usabilityChanged { reloadLists() }
                reloadAllowlist()
                reloadLastRequest()
            }
        }
    }

    // MARK: - Reloading

    func reloadAll() {
        authorization = RemindersAccess.status()
        reloadLists()
        reloadAllowlist()
        reloadToken()
        reloadLastRequest()
        reloadStoredSelection()
        rescanAddresses()
    }

    func reloadLists() {
        lists = RemindersAccess.lists().sorted {
            ($0.source, $0.title) < ($1.source, $1.title)
        }
        listsLoaded = authorization.isUsable
    }

    private func reloadAllowlist() {
        guard let environment else { return }
        allowlist = (try? environment.store.allowlist.all()) ?? []
    }

    private func reloadToken() {
        guard let environment else { return }
        tokenSummary = try? environment.tokenService.currentSummary()
    }

    private func reloadLastRequest() {
        guard let environment else { return }
        lastRequest = environment.audit.lastEvent
        requestCount = environment.audit.eventCount
    }

    private func reloadStoredSelection() {
        guard let environment else { return }
        storedSelection = (try? environment.store.bindSettings.load()) ?? nil
    }

    private func loadDraftFromStore() {
        draftAddress = storedSelection?.ipAddress ?? ""
        draftPort = String(storedSelection?.port ?? BridgeEnvironment.suggestedPort)
    }

    // MARK: - Reminders access

    /// Only ever called from a user gesture — never automatically at launch, and never from an
    /// HTTP handler: a TCC prompt must not be raised by a network packet.
    func requestAccess() {
        Task {
            switch await RemindersAccess.requestFullAccess() {
            case .success:
                actionError = nil
            case .failure(let error):
                actionError = String(describing: error)
            }
            authorization = RemindersAccess.status()
            reloadLists()
        }
    }

    // MARK: - Allowlist

    /// Lists paired with whatever binding currently points at them.
    var listRows: [ListRow] {
        let byCalendar = Dictionary(
            allowlist.map { ($0.calendarId, $0) },
            uniquingKeysWith: { first, _ in first }
        )
        return lists.map { ListRow(list: $0, binding: byCalendar[$0.calendarId]) }
    }

    /// Bindings that cannot be used right now, with the reason.
    ///
    /// Two different things land here. An entry the EventKit actor already marked `broken` is the
    /// authoritative case. An entry still marked `ok` whose calendar is absent from the current
    /// list snapshot is the same failure caught earlier — but it is only computed when the lists
    /// were actually readable, because with access revoked *every* binding would look missing.
    var brokenBindings: [BrokenBinding] {
        let visible = Set(lists.map(\.calendarId))
        return allowlist.compactMap { entry in
            if entry.state == .broken {
                return BrokenBinding(entry: entry, reason: .markedBroken)
            }
            guard listsLoaded, !visible.contains(entry.calendarId) else { return nil }
            return BrokenBinding(entry: entry, reason: .calendarMissing)
        }
    }

    var healthyBindingCount: Int {
        let visible = Set(lists.map(\.calendarId))
        return allowlist.filter { entry in
            entry.state == .ok && (!listsLoaded || visible.contains(entry.calendarId))
        }.count
    }

    /// Why an alias cannot be used for this list, or `nil` if it can.
    func aliasRejection(_ raw: String, for list: ReminderListInfo) -> String? {
        guard !raw.isEmpty else { return nil }
        guard Alias.isValid(raw) else {
            return "1–32 characters: lowercase letters, digits, - or _, starting with a letter or digit."
        }
        guard let existing = allowlist.first(where: { $0.alias.rawValue == raw }) else { return nil }
        guard existing.calendarId != list.calendarId else { return nil }
        // Re-pointing an alias is the re-bind flow, and that one requires a human to confirm a
        // named candidate. Silently moving it from a text field would be the same act without
        // the confirmation.
        return "Already bound to “\(existing.titleAtBind)”. Unbind it first."
    }

    func allow(_ list: ReminderListInfo, alias raw: String) {
        guard let environment, let alias = Alias(rawValue: raw) else { return }
        guard aliasRejection(raw, for: list) == nil else { return }

        do {
            try environment.store.allowlist.upsert(
                AllowlistEntry(
                    alias: alias,
                    calendarId: list.calendarId,
                    titleAtBind: list.title,
                    sourceAtBind: list.source,
                    boundAt: Date(),
                    state: .ok
                )
            )
            actionError = nil
        } catch {
            actionError = "Could not allow the list: \(error)"
        }
        reloadAllowlist()
    }

    func unbind(_ alias: Alias) {
        guard let environment else { return }
        do {
            try environment.store.allowlist.remove(alias)
            actionError = nil
        } catch {
            actionError = "Could not remove the alias: \(error)"
        }
        reloadAllowlist()
    }

    /// Re-points a broken alias at a list a human named.
    ///
    /// The re-bind is never derived from the title. `EKCalendar.calendarIdentifier` is documented
    /// as not sync-proof, and Apple's own suggested fallback — match by title — is exactly how a
    /// resync would start writing into a stranger's shared list that happens to be called
    /// "Inbox". The UI proposes candidates; only this call, from an explicit confirmation, writes.
    func rebind(_ alias: Alias, to list: ReminderListInfo) {
        guard let environment else { return }
        do {
            try environment.store.allowlist.upsert(
                AllowlistEntry(
                    alias: alias,
                    calendarId: list.calendarId,
                    titleAtBind: list.title,
                    sourceAtBind: list.source,
                    boundAt: Date(),
                    state: .ok
                )
            )
            actionError = nil
        } catch {
            actionError = "Could not re-bind the alias: \(error)"
        }
        reloadAllowlist()
    }

    /// Every visible list is a candidate, ordered so the most plausible ones come first — same
    /// title *and* source, then same title, then the rest. Ordering is a hint; it preselects
    /// nothing.
    func rebindCandidates(for entry: AllowlistEntry) -> [ReminderListInfo] {
        lists.sorted { left, right in
            rank(left, against: entry) < rank(right, against: entry)
        }
    }

    private func rank(_ list: ReminderListInfo, against entry: AllowlistEntry) -> Int {
        if list.title == entry.titleAtBind && list.source == entry.sourceAtBind { return 0 }
        if list.title == entry.titleAtBind { return 1 }
        if list.source == entry.sourceAtBind { return 2 }
        return 3
    }

    // MARK: - Token

    func rotateToken() {
        guard let environment else { return }
        do {
            revealedToken = try environment.tokenService.rotate().token
            actionError = nil
        } catch {
            actionError = "Could not generate a token: \(error)"
        }
        reloadToken()
    }

    func copyRevealedToken() {
        guard let token = revealedToken else { return }
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        pasteboard.setString(token, forType: .string)
    }

    /// Clears the only copy of the plaintext that exists.
    func dismissRevealedToken() {
        revealedToken = nil
    }

    // MARK: - Bind address

    /// The addresses configured on this Mac right now, plus the stored one if it is not among
    /// them.
    ///
    /// This is discovery, not selection: nothing here is ever chosen automatically. Binding
    /// "whatever is available" would put the bridge on a café network or a phone hotspot without
    /// anyone deciding to.
    func rescanAddresses() {
        let discovered = ((try? NIOInterfaceLister().localAddresses()) ?? [])
            .compactMap { literal -> AddressChoice? in
                guard let parsed = IPAddress.parse(literal) else { return nil }
                guard !parsed.isWildcard else { return nil }
                // A link-local IPv6 address is only bindable with a zone index (`fe80::1%en0`),
                // and the validator refuses zone suffixes outright — offering one would be
                // offering a guaranteed failure.
                guard !Self.isLinkLocalV6(parsed) else { return nil }
                return AddressChoice(literal: literal, kind: parsed.isLoopback ? .loopback : .routable)
            }

        var unique: [AddressChoice] = []
        for choice in discovered where !unique.contains(where: { $0.literal == choice.literal }) {
            unique.append(choice)
        }
        unique.sort { left, right in
            (left.kind.sortOrder, left.literal) < (right.kind.sortOrder, right.literal)
        }

        if let stored = storedSelection?.ipAddress,
           !unique.contains(where: { $0.literal == stored }) {
            unique.insert(AddressChoice(literal: stored, kind: .absent), at: 0)
        }
        addressChoices = unique
    }

    private static func isLinkLocalV6(_ address: IPAddress) -> Bool {
        guard case .v6(let bytes) = address, bytes.count == 16 else { return false }
        return bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80
    }

    var canSaveBindSelection: Bool {
        guard !draftAddress.isEmpty, let port = Int(draftPort) else { return false }
        return storedSelection != BindSelection(ipAddress: draftAddress, port: port)
    }

    /// Validates the draft against the live interface list, stores it, and restarts the listener.
    ///
    /// Validation happens before the write so a bad choice never becomes the thing the supervisor
    /// keeps retrying — but it is *repeated* at every start attempt, because a valid address is
    /// only valid for as long as the lease behind it lasts.
    func saveBindSelection() {
        guard let environment else { return }
        guard let port = Int(draftPort) else {
            bindError = "The port must be a number."
            return
        }
        let selection = BindSelection(ipAddress: draftAddress, port: port)

        do {
            _ = try BindAddressValidator.validate(
                selection,
                interfaces: NIOInterfaceLister(),
                policy: BridgeEnvironment.bindPolicy
            )
        } catch let error as BindAddressError {
            bindError = ServerFailure.addressRejected(error).displayText
            return
        } catch {
            bindError = String(describing: error)
            return
        }

        do {
            try environment.store.bindSettings.save(selection)
            bindError = nil
        } catch {
            bindError = "Could not store the address: \(error)"
            return
        }

        reloadStoredSelection()
        rescanAddresses()
        Task { await environment.supervisor.restart() }
    }

    func startListener() {
        guard let environment else { return }
        Task { await environment.supervisor.start() }
    }

    func stopListener() {
        guard let environment else { return }
        Task { await environment.supervisor.stop() }
    }

    func restartListener() {
        guard let environment else { return }
        Task { await environment.supervisor.restart() }
    }

    // MARK: - Derived status

    var auditLogPath: String { environment?.directories.auditLogURL.path ?? "unavailable" }
    var databasePath: String { environment?.directories.databaseURL.path ?? "unavailable" }
    var tokenBackendName: String { environment?.tokenService.backendName ?? "unavailable" }

    /// Whether the bound address is one only this Mac can reach.
    var isBoundToLoopback: Bool {
        serverState.boundAddress?.parsed.isLoopback ?? false
    }

    var listenerText: String {
        switch serverState {
        case .stopped:
            "stopped"
        case .starting:
            "starting…"
        case .listening(let address):
            "listening on \(address.displayText)"
        case .failed(let failure, let attempt, let retryIn):
            retryIn.map {
                "\(failure.displayText) — attempt \(attempt), retrying in \(Self.seconds($0))"
            } ?? failure.displayText
        }
    }

    var lastRequestText: String {
        guard let lastRequest else { return "none since launch" }
        let time = lastRequest.timestamp.formatted(date: .omitted, time: .standard)
        return "\(time) · \(lastRequest.operation.rawValue) · \(lastRequest.result.code) "
            + "(\(requestCount) since launch)"
    }

    /// The single verdict, and the only place the word "ready" is produced.
    ///
    /// It is a conjunction on purpose. Reminders access, at least one usable binding and a bound
    /// listener are each necessary, so a green light cannot be shown while any of them is missing
    /// — including the case where the listener is happily bound to loopback and therefore
    /// unreachable from the machine that is supposed to call it.
    var readiness: Readiness {
        if let startupError { return .blocked("Startup failed: \(startupError)") }
        guard authorization.isUsable else {
            return .blocked("Reminders access is \(authorization.displayText).")
        }
        switch serverState {
        case .stopped:
            return .blocked("The listener is stopped.")
        case .starting:
            return .degraded("The listener is starting.")
        case .failed(let failure, _, let retryIn):
            return .blocked(
                retryIn == nil
                    ? "The listener cannot start: \(failure.displayText)."
                    : "The listener is not bound: \(failure.displayText)."
            )
        case .listening:
            break
        }
        guard healthyBindingCount > 0 else {
            return .blocked("No reminder list is allowed yet.")
        }
        guard tokenSummary != nil else {
            return .blocked("No API token has been generated.")
        }
        if isBoundToLoopback {
            return .degraded("Bound to loopback — reachable from this Mac only, not from Erda.")
        }
        if !brokenBindings.isEmpty {
            let names = brokenBindings.map(\.entry.alias.rawValue).joined(separator: ", ")
            return .degraded("Broken alias: \(names).")
        }
        return .ready
    }

    /// The menu bar icon. The shape carries the state, not the colour: a template image in the
    /// menu bar is tinted by the system, so a red one cannot be relied on to look red.
    var menuBarSymbol: String {
        switch readiness {
        case .ready: "checklist"
        case .degraded: "exclamationmark.triangle.fill"
        case .blocked: "xmark.octagon.fill"
        }
    }

    private static func seconds(_ duration: Duration) -> String {
        "\(duration.components.seconds)s"
    }
}

// MARK: - View models

/// One reminder list plus whatever binding points at it.
struct ListRow: Identifiable {
    let list: ReminderListInfo
    let binding: AllowlistEntry?

    var id: String { list.calendarId }
}

/// An alias that cannot be used until a human re-points it.
struct BrokenBinding: Identifiable {
    enum Reason {
        /// The EventKit actor failed to resolve the calendar and marked the row.
        case markedBroken
        /// The row still says `ok`, but the calendar is not in the current list snapshot.
        case calendarMissing
    }

    let entry: AllowlistEntry
    let reason: Reason

    var id: String { entry.alias.rawValue }

    var explanation: String {
        switch reason {
        case .markedBroken:
            "The list this alias was bound to no longer resolves."
        case .calendarMissing:
            "The list this alias was bound to is not among the lists visible right now."
        }
    }
}

/// A local address the picker can offer.
struct AddressChoice: Identifiable, Hashable {
    enum Kind {
        case routable
        case loopback
        /// Stored, but not on any interface at the moment.
        case absent

        var sortOrder: Int {
            switch self {
            case .routable: 0
            case .loopback: 1
            case .absent: 2
            }
        }
    }

    let literal: String
    let kind: Kind

    var id: String { literal }

    var label: String {
        switch kind {
        case .routable: literal
        case .loopback: "\(literal) — this Mac only"
        case .absent: "\(literal) — not on any interface now"
        }
    }
}

/// The traffic light. Deliberately three-valued: "not ready" and "ready but Erda cannot reach it"
/// are different problems and must not share a colour.
enum Readiness: Equatable {
    case ready
    case degraded(String)
    case blocked(String)

    var text: String {
        switch self {
        case .ready: "Ready"
        case .degraded(let reason): reason
        case .blocked(let reason): reason
        }
    }
}
