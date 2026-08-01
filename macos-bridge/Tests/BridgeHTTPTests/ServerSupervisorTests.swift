import BridgeCore
import Foundation
import NIOCore
import NIOPosix
import Testing

@testable import BridgeHTTP

/// A mutable stand-in for the store's bind row, so a test can move the configuration under a
/// running supervisor the way the setup UI does.
private final class SelectionBox: @unchecked Sendable {
    private let lock = NSLock()
    private var value: BindSelection?

    init(_ value: BindSelection? = nil) {
        self.value = value
    }

    var selection: BindSelection? {
        get { lock.withLock { value } }
        set { lock.withLock { value = newValue } }
    }
}

/// An interface list a test can change, standing in for a DHCP lease coming and going.
private final class MutableLister: NetworkInterfaceLister, @unchecked Sendable {
    private let lock = NSLock()
    private var addresses: [String]
    private var failure: (any Error)?

    init(_ addresses: [String]) {
        self.addresses = addresses
    }

    func localAddresses() throws -> [String] {
        let (current, error) = lock.withLock { (addresses, failure) }
        if let error { throw error }
        return current
    }

    func set(_ addresses: [String]) {
        lock.withLock { self.addresses = addresses }
    }

    func fail(with error: any Error) {
        lock.withLock { failure = error }
    }
}

@Suite("Listener supervision", .serialized)
struct ServerSupervisorTests {
    /// A loopback policy: these tests bind `127.0.0.1`, and they go through the same validator the
    /// app does rather than around it.
    private let policy = BindAddressPolicy(allowLoopback: true, portRange: 1024...65535)
    private let fastBackoff = RetryBackoff(initial: .milliseconds(20), maximum: .milliseconds(40), multiplier: 2)

    private func makeSupervisor(
        selection: SelectionBox,
        interfaces: any NetworkInterfaceLister,
        healthCheckInterval: Duration = .seconds(30)
    ) throws -> ServerSupervisor {
        ServerSupervisor(
            services: try TestHarness().services,
            selection: { selection.selection },
            interfaces: interfaces,
            policy: policy,
            backoff: fastBackoff,
            healthCheckInterval: healthCheckInterval
        )
    }

    /// A port nothing is listening on, obtained by binding an ephemeral one and giving it back.
    private func freePort() async throws -> Int {
        let harness = try TestHarness()
        let server = BridgeHTTPServer(
            configuration: BridgeServerConfiguration(host: "127.0.0.1", port: 0),
            services: harness.services
        )
        let address = try await server.start()
        let port = try #require(address.port)
        await server.stop()
        return port
    }

    /// Consumes the state stream until `predicate` matches, so tests assert on transitions
    /// instead of sleeping.
    private func waitFor(
        _ supervisor: ServerSupervisor,
        timeout: Duration = .seconds(10),
        where predicate: @escaping @Sendable (ServerState) -> Bool
    ) async throws -> [ServerState] {
        let states = supervisor.states
        return try await withThrowingTaskGroup(of: [ServerState].self) { group in
            group.addTask {
                var seen: [ServerState] = []
                for await state in states {
                    seen.append(state)
                    if predicate(state) { return seen }
                }
                return seen
            }
            group.addTask {
                try await Task.sleep(for: timeout)
                throw SupervisionTimeout()
            }
            let result = try await group.next()!
            group.cancelAll()
            return result
        }
    }

    private struct SupervisionTimeout: Error {}

    // MARK: - The happy path

    @Test("a stored, currently-valid address is bound")
    func bindsStoredAddress() async throws {
        let port = try await freePort()
        let selection = SelectionBox(BindSelection(ipAddress: "127.0.0.1", port: port))
        let supervisor = try makeSupervisor(selection: selection, interfaces: MutableLister(["127.0.0.1"]))

        await supervisor.start()
        let seen = try await waitFor(supervisor) { $0.isListening }
        #expect(seen.contains(.starting))
        #expect(seen.last?.boundAddress?.port == port)

        await supervisor.stop()
        #expect(await supervisor.state == .stopped)
    }

    @Test("starting twice does not open a second listener")
    func startIsIdempotent() async throws {
        let port = try await freePort()
        let selection = SelectionBox(BindSelection(ipAddress: "127.0.0.1", port: port))
        let supervisor = try makeSupervisor(selection: selection, interfaces: MutableLister(["127.0.0.1"]))

        await supervisor.start()
        _ = try await waitFor(supervisor) { $0.isListening }
        // A second `start` while bound would otherwise race a second bind onto the same port.
        await supervisor.start()
        #expect(await supervisor.state.isListening)

        await supervisor.stop()
    }

    // MARK: - Fail closed

    @Test("with nothing configured the supervisor does not bind and does not retry")
    func refusesWhenUnconfigured() async throws {
        let supervisor = try makeSupervisor(selection: SelectionBox(), interfaces: MutableLister(["127.0.0.1"]))

        await supervisor.start()
        let seen = try await waitFor(supervisor) {
            if case .failed(.notConfigured, _, let retryIn) = $0 { return retryIn == nil }
            return false
        }
        #expect(seen.last == .failed(.notConfigured, attempt: 1, retryIn: nil))
        #expect(await supervisor.state.boundAddress == nil)

        await supervisor.stop()
    }

    @Test("an address that will never be valid is reported once, not retried forever", arguments: [
        BindSelection(ipAddress: "0.0.0.0", port: 17832),
        BindSelection(ipAddress: "localhost", port: 17832),
        BindSelection(ipAddress: "127.0.0.1", port: 80),
    ])
    func doesNotRetryPermanentFailures(selection: BindSelection) async throws {
        let supervisor = try makeSupervisor(
            selection: SelectionBox(selection),
            interfaces: MutableLister(["127.0.0.1"])
        )

        await supervisor.start()
        let seen = try await waitFor(supervisor) {
            if case .failed(_, _, let retryIn) = $0 { return retryIn == nil }
            return false
        }
        let last = try #require(seen.last)
        guard case .failed(let failure, let attempt, let retryIn) = last else {
            Issue.record("expected a failure, got \(last)")
            return
        }
        #expect(attempt == 1)
        #expect(retryIn == nil)
        #expect(!failure.isTransient)

        await supervisor.stop()
    }

    // MARK: - Backoff

    @Test("an address that is not on any interface is retried on the backoff schedule")
    func retriesMissingAddress() async throws {
        let port = try await freePort()
        // The stored choice is a plausible LAN address that this machine does not currently have
        // — exactly the shape of a DHCP lease that has moved.
        let selection = SelectionBox(BindSelection(ipAddress: "192.168.178.106", port: port))
        let interfaces = MutableLister(["127.0.0.1"])
        let supervisor = try makeSupervisor(selection: selection, interfaces: interfaces)

        await supervisor.start()
        let seen = try await waitFor(supervisor) {
            if case .failed(_, let attempt, _) = $0 { return attempt >= 3 }
            return false
        }

        let failures = seen.compactMap { state -> (Int, Duration?)? in
            guard case .failed(let failure, let attempt, let retryIn) = state else { return nil }
            #expect(failure == .addressRejected(.notOnLocalInterface))
            return (attempt, retryIn)
        }
        #expect(failures.count >= 3)
        #expect(failures[0] == (1, .milliseconds(20)))
        #expect(failures[1] == (2, .milliseconds(40)))
        // Capped, and still retrying.
        #expect(failures[2] == (3, .milliseconds(40)))

        await supervisor.stop()
    }

    @Test("the address reappearing ends the retry loop without a restart")
    func recoversWhenAddressReturns() async throws {
        let port = try await freePort()
        let selection = SelectionBox(BindSelection(ipAddress: "127.0.0.1", port: port))
        let interfaces = MutableLister([])
        let supervisor = try makeSupervisor(selection: selection, interfaces: interfaces)

        await supervisor.start()
        _ = try await waitFor(supervisor) {
            if case .failed(.addressRejected(.notOnLocalInterface), _, _) = $0 { return true }
            return false
        }

        interfaces.set(["127.0.0.1"])
        let seen = try await waitFor(supervisor) { $0.isListening }
        #expect(seen.last?.boundAddress?.ipAddress == "127.0.0.1")

        await supervisor.stop()
    }

    @Test("a port already in use is a transient failure, and a new choice is picked up on retry")
    func retriesBindFailureAndRereadsSelection() async throws {
        let harness = try TestHarness()
        let occupiedPort = try await freePort()
        let occupier = BridgeHTTPServer(
            configuration: BridgeServerConfiguration(host: "127.0.0.1", port: occupiedPort),
            services: harness.services
        )
        _ = try await occupier.start()

        let selection = SelectionBox(BindSelection(ipAddress: "127.0.0.1", port: occupiedPort))
        let supervisor = try makeSupervisor(selection: selection, interfaces: MutableLister(["127.0.0.1"]))

        do {
            await supervisor.start()
            let failures = try await waitFor(supervisor) {
                if case .failed(.bindFailed, _, let retryIn) = $0 { return retryIn != nil }
                return false
            }
            #expect(failures.last?.boundAddress == nil)

            // The setup UI saving a different port is exactly this: the closure is re-read on the
            // next attempt, with nothing cached from the first one.
            selection.selection = BindSelection(ipAddress: "127.0.0.1", port: try await freePort())
            let recovered = try await waitFor(supervisor) { $0.isListening }
            #expect(recovered.last?.boundAddress?.port == selection.selection?.port)
        } catch {
            await supervisor.stop()
            await occupier.stop()
            throw error
        }

        await supervisor.stop()
        await occupier.stop()
    }

    // MARK: - Liveness

    @Test("a bound address vanishing tears the listener down instead of reporting it as healthy")
    func tearsDownWhenAddressDisappears() async throws {
        let port = try await freePort()
        let selection = SelectionBox(BindSelection(ipAddress: "127.0.0.1", port: port))
        let interfaces = MutableLister(["127.0.0.1"])
        let supervisor = try makeSupervisor(
            selection: selection,
            interfaces: interfaces,
            healthCheckInterval: .milliseconds(20)
        )

        await supervisor.start()
        _ = try await waitFor(supervisor) { $0.isListening }

        interfaces.set(["10.0.0.7"])
        let seen = try await waitFor(supervisor) {
            if case .failed(.addressRejected(.notOnLocalInterface), _, _) = $0 { return true }
            return false
        }
        #expect(seen.last?.isListening == false)

        await supervisor.stop()
    }

    @Test("an unreadable interface list does not tear down a working listener")
    func keepsListenerWhenEnumerationFails() async throws {
        struct Boom: Error {}

        let port = try await freePort()
        let selection = SelectionBox(BindSelection(ipAddress: "127.0.0.1", port: port))
        let interfaces = MutableLister(["127.0.0.1"])
        let supervisor = try makeSupervisor(
            selection: selection,
            interfaces: interfaces,
            healthCheckInterval: .milliseconds(20)
        )

        await supervisor.start()
        _ = try await waitFor(supervisor) { $0.isListening }

        interfaces.fail(with: Boom())
        // Several health checks' worth: a diagnostic failure must not become an outage.
        try await Task.sleep(for: .milliseconds(200))
        #expect(await supervisor.state.isListening)

        await supervisor.stop()
    }

    // MARK: - Restart

    @Test("restart rebinds using the freshly stored choice")
    func restartUsesNewSelection() async throws {
        let first = try await freePort()
        let selection = SelectionBox(BindSelection(ipAddress: "127.0.0.1", port: first))
        let supervisor = try makeSupervisor(selection: selection, interfaces: MutableLister(["127.0.0.1"]))

        await supervisor.start()
        _ = try await waitFor(supervisor) { $0.isListening }

        let second = try await freePort()
        selection.selection = BindSelection(ipAddress: "127.0.0.1", port: second)
        await supervisor.restart()

        let seen = try await waitFor(supervisor) { $0.isListening }
        #expect(seen.last?.boundAddress?.port == second)

        await supervisor.stop()
        #expect(await supervisor.state == .stopped)
    }

    @Test("after giving up, Start works again once the configuration is fixed")
    func startsAgainAfterGivingUp() async throws {
        let selection = SelectionBox()
        let supervisor = try makeSupervisor(selection: selection, interfaces: MutableLister(["127.0.0.1"]))

        await supervisor.start()
        _ = try await waitFor(supervisor) {
            if case .failed(.notConfigured, _, let retryIn) = $0 { return retryIn == nil }
            return false
        }

        // The supervision task has ended. A stale handle to it must not make `start` a no-op —
        // that would leave the only recovery from a permanent failure being a relaunch.
        let port = try await freePort()
        selection.selection = BindSelection(ipAddress: "127.0.0.1", port: port)
        await supervisor.start()

        let seen = try await waitFor(supervisor) { $0.isListening }
        #expect(seen.last?.boundAddress?.port == port)

        await supervisor.stop()
    }

    @Test("stopping a supervisor that never started still reports stopped")
    func stopWithoutStart() async throws {
        let supervisor = try makeSupervisor(selection: SelectionBox(), interfaces: MutableLister([]))
        await supervisor.stop()
        #expect(await supervisor.state == .stopped)
    }

    // MARK: - Failure classification

    @Test("a state that is still being worked on is distinguishable from one that is not")
    func reportsSupervision() throws {
        let address = try BindAddressValidator.validate(
            BindSelection(ipAddress: "127.0.0.1", port: 17832),
            interfaces: StaticInterfaceLister(["127.0.0.1"]),
            policy: policy
        )
        #expect(!ServerState.stopped.isSupervising)
        #expect(ServerState.starting.isSupervising)
        #expect(ServerState.listening(address).isSupervising)
        // Waiting out a backoff is still work in flight; having given up is not.
        #expect(
            ServerState.failed(.bindFailed(description: "in use"), attempt: 1, retryIn: .seconds(2))
                .isSupervising
        )
        #expect(!ServerState.failed(.notConfigured, attempt: 1, retryIn: nil).isSupervising)
    }

    @Test("only failures that can fix themselves are retried")
    func classifiesFailures() {
        #expect(!ServerFailure.notConfigured.isTransient)
        #expect(ServerFailure.addressRejected(.notOnLocalInterface).isTransient)
        #expect(ServerFailure.addressRejected(.interfaceEnumerationFailed).isTransient)
        #expect(!ServerFailure.addressRejected(.notAnIPLiteral).isTransient)
        #expect(!ServerFailure.addressRejected(.wildcardNotAllowed).isTransient)
        #expect(!ServerFailure.addressRejected(.loopbackNotAllowed).isTransient)
        #expect(!ServerFailure.addressRejected(.portOutOfRange).isTransient)
        #expect(ServerFailure.bindFailed(description: "EADDRNOTAVAIL").isTransient)
    }
}
