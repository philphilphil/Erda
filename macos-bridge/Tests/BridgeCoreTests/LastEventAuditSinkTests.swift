import Foundation
import Testing

@testable import BridgeCore

@Suite("Last-event audit sink")
struct LastEventAuditSinkTests {
    private func event(
        at seconds: TimeInterval,
        operation: AuditOperation = .remindersCreate,
        result: AuditResult = .ok
    ) -> AuditEvent {
        AuditEvent(
            timestamp: Date(timeIntervalSince1970: seconds),
            requestId: UUID(),
            tokenId: TokenId(rawValue: "a1b2c3d4"),
            operation: operation,
            alias: Alias(rawValue: "inbox"),
            result: result,
            status: 201,
            durationMs: 12
        )
    }

    @Test("nothing has been seen before the first request")
    func startsEmpty() {
        let sink = LastEventAuditSink(wrapping: MemoryAuditSink())
        #expect(sink.lastEvent == nil)
        #expect(sink.eventCount == 0)
    }

    @Test("every event still reaches the wrapped sink, in order")
    func passesThrough() {
        let wrapped = MemoryAuditSink()
        let sink = LastEventAuditSink(wrapping: wrapped)

        sink.record(event(at: 1))
        sink.record(event(at: 2))

        #expect(wrapped.events.count == 2)
        #expect(wrapped.events.map(\.timestamp.timeIntervalSince1970) == [1, 2])
    }

    @Test("the latest event and the count are what the status panel reads")
    func tracksLatest() {
        let sink = LastEventAuditSink(wrapping: MemoryAuditSink())

        sink.record(event(at: 1, operation: .remindersCreate))
        sink.record(event(at: 2, operation: .remindersList))

        #expect(sink.lastEvent?.operation == .remindersList)
        #expect(sink.eventCount == 2)
    }

    @Test("a rejected request counts too — the panel must not look idle while it is being probed")
    func countsRejections() {
        let sink = LastEventAuditSink(wrapping: MemoryAuditSink())
        sink.record(event(at: 3, operation: .unrouted, result: .error(.unauthorized)))

        #expect(sink.lastEvent?.result == .error(.unauthorized))
        #expect(sink.eventCount == 1)
    }

    @Test("concurrent recording neither loses an event nor races the readout")
    func concurrentRecording() async {
        let wrapped = MemoryAuditSink()
        let sink = LastEventAuditSink(wrapping: wrapped)

        await withTaskGroup(of: Void.self) { group in
            for index in 0..<200 {
                group.addTask { sink.record(self.event(at: TimeInterval(index))) }
            }
        }

        #expect(sink.eventCount == 200)
        #expect(wrapped.events.count == 200)
        #expect(sink.lastEvent != nil)
    }
}
