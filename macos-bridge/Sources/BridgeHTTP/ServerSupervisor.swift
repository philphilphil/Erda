import BridgeCore
import Foundation

/// Why the listener is not running. A closed set, and none of it ever reaches the wire — this is
/// the local status readout, so it may say plainly what a 500 response never could.
public enum ServerFailure: Error, Sendable, Equatable {
    /// No bind address has been chosen yet. Not an error so much as the honest initial state.
    case notConfigured
    /// The stored choice did not survive validation.
    case addressRejected(BindAddressError)
    /// Validation passed but `bind(2)` did not — the port is taken, or the address disappeared in
    /// the moment between the two.
    case bindFailed(description: String)

    /// Whether waiting and trying the same stored choice again could plausibly work.
    ///
    /// The distinction is the whole point of retrying: an address that is not on an interface
    /// *right now* is the normal shape of a DHCP lease still settling after a wake, and it
    /// usually comes back. A hostname, a wildcard or a port below 1024 will never become valid on
    /// its own, so retrying those would only bury the real message under a spinner.
    public var isTransient: Bool {
        switch self {
        case .notConfigured:
            false
        case .addressRejected(let error):
            switch error {
            case .notOnLocalInterface, .interfaceEnumerationFailed: true
            case .notAnIPLiteral, .wildcardNotAllowed, .loopbackNotAllowed, .portOutOfRange: false
            }
        case .bindFailed:
            true
        }
    }

    /// One line for the menu bar and the status panel.
    public var displayText: String {
        switch self {
        case .notConfigured:
            "no bind address chosen"
        case .addressRejected(let error):
            switch error {
            case .notAnIPLiteral: "not an IP literal"
            case .wildcardNotAllowed: "0.0.0.0 / :: are not allowed"
            case .loopbackNotAllowed: "loopback is not allowed"
            case .notOnLocalInterface: "address is not on any interface right now"
            case .portOutOfRange: "port is outside 1024–65535"
            case .interfaceEnumerationFailed: "could not read the interface list"
            }
        case .bindFailed(let description):
            "bind failed: \(description)"
        }
    }
}

/// What the listener is doing, as the UI is allowed to describe it.
///
/// There is no `.ready`: "listening" is the strongest claim this type can make, and it says
/// nothing about Reminders authorization or the allowlist. Composing those into a single verdict
/// is the app's job, so that no state here can be mistaken for an all-clear.
public enum ServerState: Sendable, Equatable {
    case stopped
    case starting
    case listening(BindAddress)
    /// `retryIn` is `nil` when the failure is permanent and the supervisor has given up.
    case failed(ServerFailure, attempt: Int, retryIn: Duration?)

    public var boundAddress: BindAddress? {
        if case .listening(let address) = self { return address }
        return nil
    }

    public var isListening: Bool { boundAddress != nil }

    /// Whether the supervisor still has work in flight — bound, starting, or waiting out a
    /// backoff. `start()` is a no-op here and `stop()` is the meaningful action, so a UI that
    /// offers "Start" in this state is offering a button that does nothing.
    public var isSupervising: Bool {
        switch self {
        case .stopped: false
        case .starting, .listening: true
        case .failed(_, _, let retryIn): retryIn != nil
        }
    }
}

/// Owns the listener's lifecycle: validate, bind, watch, and retry with backoff.
///
/// The listener is not started once and forgotten. Three things move underneath it — the stored
/// choice (the setup UI can change it), the interface list (DHCP), and the socket itself — so the
/// stored choice is re-read and re-validated on **every** attempt, and a bound address is
/// re-checked against the interface list periodically. An address that vanishes tears the listener
/// down and re-enters the retry loop, because a listener bound to an address the machine no longer
/// has is unreachable, and reporting it as healthy would be the one thing the status panel must
/// never do.
public actor ServerSupervisor {
    /// Reads the stored choice. A closure rather than a value so a save in the setup UI is picked
    /// up by the next attempt without the supervisor being rebuilt.
    public typealias SelectionProvider = @Sendable () async -> BindSelection?

    private let services: BridgeServices
    private let selectionProvider: SelectionProvider
    private let interfaces: any NetworkInterfaceLister
    private let policy: BindAddressPolicy
    private let backoff: RetryBackoff
    private let healthCheckInterval: Duration

    private var supervision: Task<Void, Never>?
    private var server: BridgeHTTPServer?
    private var current: ServerState = .stopped

    /// Single-consumer, by construction: the app has exactly one status model watching it.
    public nonisolated let states: AsyncStream<ServerState>
    private nonisolated let continuation: AsyncStream<ServerState>.Continuation

    public init(
        services: BridgeServices,
        selection: @escaping SelectionProvider,
        interfaces: any NetworkInterfaceLister = NIOInterfaceLister(),
        policy: BindAddressPolicy = .production,
        backoff: RetryBackoff = .default,
        healthCheckInterval: Duration = .seconds(30)
    ) {
        let (stream, continuation) = AsyncStream<ServerState>.makeStream(
            of: ServerState.self,
            bufferingPolicy: .bufferingNewest(32)
        )
        self.states = stream
        self.continuation = continuation
        self.services = services
        self.selectionProvider = selection
        self.interfaces = interfaces
        self.policy = policy
        self.backoff = backoff
        self.healthCheckInterval = healthCheckInterval
        continuation.yield(.stopped)
    }

    deinit {
        continuation.finish()
    }

    public var state: ServerState { current }

    /// Begins supervising. Idempotent: a second call while a supervision task is alive is ignored
    /// rather than starting a second listener.
    ///
    /// A task that has already given up on a permanent failure does not count as alive — otherwise
    /// "fix the port, press Start" would silently do nothing.
    public func start() {
        guard supervision == nil else { return }
        supervision = Task { await self.supervise() }
    }

    public func stop() async {
        guard let task = supervision else {
            await tearDown()
            publish(.stopped)
            return
        }
        supervision = nil
        task.cancel()
        // Suspending here releases the actor, so the cancelled task can run its own isolated
        // cleanup — awaiting it while holding isolation would deadlock.
        await task.value
        await tearDown()
        publish(.stopped)
    }

    /// What the setup UI calls after saving a new address.
    public func restart() async {
        await stop()
        start()
    }

    // MARK: - Supervision

    private func supervise() async {
        // Runs on every exit path — cancellation and giving up alike — and always before `stop()`
        // resumes from `await task.value`, so it can never clear a task that replaced this one.
        defer { forgetSupervision() }

        var attempt = 0
        while !Task.isCancelled {
            attempt += 1
            publish(.starting)

            switch await attemptStart() {
            case .success(let address):
                // A good bind resets the schedule: the next outage starts from the short delay
                // again rather than inheriting an hour-old backoff.
                attempt = 0
                publish(.listening(address))
                await watch(address)
                await tearDown()

            case .failure(let failure):
                await tearDown()
                guard failure.isTransient else {
                    publish(.failed(failure, attempt: attempt, retryIn: nil))
                    return
                }
                let delay = backoff.delay(forAttempt: attempt)
                publish(.failed(failure, attempt: attempt, retryIn: delay))
                guard await sleep(delay) else { return }
            }
        }
    }

    private func attemptStart() async -> Result<BindAddress, ServerFailure> {
        guard let selection = await selectionProvider() else { return .failure(.notConfigured) }

        let address: BindAddress
        do {
            address = try BindAddressValidator.validate(
                selection,
                interfaces: interfaces,
                policy: policy
            )
        } catch let error as BindAddressError {
            return .failure(.addressRejected(error))
        } catch {
            return .failure(.addressRejected(.interfaceEnumerationFailed))
        }

        let candidate = BridgeHTTPServer(
            configuration: BridgeServerConfiguration(bindAddress: address),
            services: services
        )
        do {
            _ = try await candidate.start()
        } catch {
            // The bootstrap can fail after partially setting up; stopping is harmless if it did
            // not, and leaks an event-loop registration if it did and we skipped it.
            await candidate.stop()
            return .failure(.bindFailed(description: String(describing: error)))
        }

        server = candidate
        return .success(address)
    }

    /// Returns once the bound address is no longer on any interface, or once cancelled.
    private func watch(_ address: BindAddress) async {
        while !Task.isCancelled {
            guard await sleep(healthCheckInterval) else { return }
            guard isStillLocal(address) else { return }
        }
    }

    /// Whether the address is still configured on some interface.
    ///
    /// A failure to read the interface list answers `true`. Tearing a working listener down
    /// because a syscall hiccuped would turn a diagnostic problem into an outage; the next check
    /// runs in another interval either way.
    private func isStillLocal(_ address: BindAddress) -> Bool {
        guard let local = try? interfaces.localAddresses() else { return true }
        return local.compactMap(IPAddress.parse).contains(address.parsed)
    }

    private func forgetSupervision() {
        supervision = nil
    }

    private func tearDown() async {
        if let server { await server.stop() }
        server = nil
    }

    /// `false` when the sleep was cancelled — the caller's cue to unwind rather than loop.
    private func sleep(_ duration: Duration) async -> Bool {
        do {
            try await Task.sleep(for: duration)
            return !Task.isCancelled
        } catch {
            return false
        }
    }

    private func publish(_ state: ServerState) {
        guard state != current else { return }
        current = state
        continuation.yield(state)
    }
}
