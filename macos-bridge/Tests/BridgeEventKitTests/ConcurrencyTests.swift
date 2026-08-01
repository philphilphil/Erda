import BridgeCore
import Foundation
import Testing

@testable import BridgeEventKit

@Suite("Fetch gate")
struct FetchGateTests {
    private func reminder(_ id: String) -> RawReminder {
        RawReminder(itemId: id, calendarId: "cal-inbox", title: id)
    }

    @Test("a result that arrives before anyone waits is not lost")
    func deliversAResultThatArrivedEarly() async throws {
        let gate = FetchGate()
        gate.finish(.success([reminder("a")]))
        #expect(try await gate.value.map(\.itemId) == ["a"])
    }

    @Test("a result that arrives after the wait started is delivered")
    func deliversALateResult() async throws {
        let gate = FetchGate()
        Task.detached {
            try? await Task.sleep(for: .milliseconds(20))
            gate.finish(.success([reminder("b")]))
        }
        #expect(try await gate.value.map(\.itemId) == ["b"])
    }

    @Test("failures propagate")
    func propagatesFailure() async {
        let gate = FetchGate()
        gate.finish(.failure(ApiError.remindersUnavailable))
        await #expect(throws: ApiError.remindersUnavailable) { try await gate.value }
    }

    /// The crash this type exists to prevent: EventKit's completion block and our own timeout
    /// genuinely race, and resuming a `CheckedContinuation` twice traps the process rather than
    /// throwing. Only the first finish may be seen.
    @Test("the first finish wins and every later one is a no-op")
    func firstFinishWins() async throws {
        let gate = FetchGate()
        gate.finish(.success([reminder("first")]))
        gate.finish(.failure(ApiError.remindersUnavailable))
        gate.finish(.success([reminder("third")]))

        #expect(try await gate.value.map(\.itemId) == ["first"])
    }

    /// The gate is one-shot on purpose — `value` is awaited by exactly the frame that started the
    /// fetch — so each round awaits it once, from inside the stampede.
    @Test("concurrent finishers still resume exactly once", .timeLimit(.minutes(1)))
    func survivesAConcurrentStampede() async throws {
        // Repeated, because a double-resume is a race: one round would pass by luck.
        for round in 0..<200 {
            let gate = FetchGate()

            await withTaskGroup(of: Void.self) { group in
                group.addTask {
                    // The awaiting side joins the race, so "finished before the wait started" and
                    // "finished during the wait" are both exercised across rounds.
                    _ = try? await gate.value
                }
                for finisher in 0..<8 {
                    group.addTask {
                        if finisher.isMultiple(of: 2) {
                            gate.finish(.success([RawReminder(itemId: "r\(round)", calendarId: "c", title: "t")]))
                        } else {
                            gate.finish(.failure(ApiError.remindersUnavailable))
                        }
                    }
                }
            }
        }
    }

    @Test("a finish racing the very first await is still delivered exactly once")
    func racesTheFirstAwait() async throws {
        for _ in 0..<200 {
            let gate = FetchGate()
            let waiter = Task { try await gate.value }
            gate.finish(.success([reminder("x")]))
            #expect(try await waiter.value.map(\.itemId) == ["x"])
        }
    }
}

@Suite("Event store change flag")
struct EventStoreChangeFlagTests {
    @Test("a fresh flag owes nothing")
    func startsClear() {
        #expect(EventStoreChangeFlag(observing: false).consume() == false)
    }

    @Test("raising is sticky until consumed, then clears")
    func raiseThenConsume() {
        let flag = EventStoreChangeFlag(observing: false)
        flag.raise()
        #expect(flag.consume())
        #expect(flag.consume() == false)
    }

    @Test("several changes between operations still owe exactly one reset")
    func collapsesRepeats() {
        let flag = EventStoreChangeFlag(observing: false)
        flag.raise()
        flag.raise()
        flag.raise()
        #expect(flag.consume())
        #expect(flag.consume() == false)
    }

    /// The notification really does arrive on an arbitrary thread, so the flag has to be safe
    /// against a raise landing while another thread consumes.
    @Test("concurrent raises are never lost outright", .timeLimit(.minutes(1)))
    func concurrentRaises() async {
        let flag = EventStoreChangeFlag(observing: false)

        await withTaskGroup(of: Void.self) { group in
            for _ in 0..<64 { group.addTask { flag.raise() } }
        }
        #expect(flag.consume())
    }

    /// A subscribed flag reacts to the notification without any actor hop — the property that
    /// lets `EKEventStore.reset()` happen on the actor's queue instead of on whichever thread
    /// EventKit posted from.
    @Test("a subscribed flag is raised by the notification itself")
    func observesTheNotification() {
        let center = NotificationCenter()
        let flag = EventStoreChangeFlag(center: center, observing: true)
        #expect(flag.consume() == false)

        center.post(name: EventStoreChangeFlag.notificationName, object: nil)
        #expect(flag.consume())
    }
}
