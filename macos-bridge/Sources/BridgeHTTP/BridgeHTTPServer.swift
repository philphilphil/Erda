import BridgeCore
import Foundation
import NIOCore
import NIOHTTP1
import NIOPosix

/// The listener.
///
/// Each accepted connection becomes a plain `async` task via `NIOAsyncChannel`, which is what
/// lets a handler `await` the reminders actor without ever blocking an event loop thread — the
/// payoff for not using classic `ChannelInboundHandler` callbacks.
public actor BridgeHTTPServer {
    public typealias Connection = NIOAsyncChannel<NIOHTTPServerRequestFull, HTTPServerResponsePart>

    private let configuration: BridgeServerConfiguration
    private let responder: BridgeResponder
    private let admission: ConnectionAdmission
    private let eventLoopGroup: MultiThreadedEventLoopGroup

    private var serveTask: Task<Void, Never>?
    private var listener: NIOAsyncChannel<Connection, Never>?
    private var bound: SocketAddress?

    public init(
        configuration: BridgeServerConfiguration,
        services: BridgeServices,
        eventLoopGroup: MultiThreadedEventLoopGroup = .singleton
    ) {
        self.configuration = configuration
        self.responder = BridgeResponder(services: services)
        self.admission = ConnectionAdmission(limit: configuration.maxConcurrentConnections)
        self.eventLoopGroup = eventLoopGroup
    }

    /// Binds and starts serving. Returns the address actually bound, which matters when the
    /// caller asked for port 0.
    @discardableResult
    public func start() async throws -> SocketAddress {
        guard serveTask == nil else { return try requireBound() }

        // A `let` copy: the child-channel initializer is `@Sendable`, and capturing a `var`
        // there is a Swift 6 error rather than a warning.
        let configuration = self.configuration

        let listener = try await ServerBootstrap(group: eventLoopGroup)
            .serverChannelOption(.backlog, value: configuration.backlog)
            .serverChannelOption(.socketOption(.so_reuseaddr), value: 1)
            .childChannelOption(.maxMessagesPerRead, value: 1)
            .bind(host: configuration.host, port: configuration.port) { channel in
                channel.eventLoop.makeCompletedFuture {
                    try channel.pipeline.syncOperations.addHandlers(
                        Pipeline.handlers(configuration: configuration)
                    )
                    return try Connection(wrappingChannelSynchronously: channel)
                }
            }

        self.listener = listener
        self.bound = listener.channel.localAddress

        let responder = self.responder
        let admission = self.admission
        serveTask = Task {
            // The listener closing is a normal shutdown, not something to report.
            try? await Self.serve(listener, responder: responder, admission: admission)
        }

        return try requireBound()
    }

    public func stop() async {
        serveTask?.cancel()
        serveTask = nil
        if let listener {
            try? await listener.channel.close()
        }
        listener = nil
        bound = nil
    }

    public var boundAddress: SocketAddress? { bound }

    /// For tests: lets them wait until a known number of connections have been admitted instead
    /// of sleeping and hoping.
    public var activeConnections: Int {
        get async { await admission.activeConnections }
    }

    private func requireBound() throws -> SocketAddress {
        guard let bound else { throw BridgeServerError.notBound }
        return bound
    }

    // MARK: - Serving

    /// `nonisolated static` on purpose: the accept loop must not hop onto the server's actor for
    /// every connection.
    private static func serve(
        _ listener: NIOAsyncChannel<Connection, Never>,
        responder: BridgeResponder,
        admission: ConnectionAdmission
    ) async throws {
        try await listener.executeThenClose { inbound in
            try await withThrowingDiscardingTaskGroup { group in
                for try await connection in inbound {
                    group.addTask {
                        await Self.handle(connection, responder: responder, admission: admission)
                    }
                }
            }
        }
    }

    private static func handle(
        _ connection: Connection,
        responder: BridgeResponder,
        admission: ConnectionAdmission
    ) async {
        // Step 1: admission. Over the ceiling, close without reading a byte.
        guard await admission.acquire() else {
            try? await connection.channel.close()
            return
        }

        do {
            try await connection.executeThenClose { inbound, outbound in
                for try await request in inbound {
                    let bridgeRequest = BridgeRequest(
                        method: request.head.method,
                        version: request.head.version,
                        uri: request.head.uri,
                        headers: request.head.headers,
                        body: request.body.map { [UInt8]($0.readableBytesView) } ?? [],
                        requestId: UUID()
                    )

                    let response = await responder.respond(to: bridgeRequest)
                    try await write(response, for: bridgeRequest, to: outbound)

                    // One response per connection: `CloseAfterResponseHandler` is closing the
                    // channel behind us, so there is nothing further to read.
                    break
                }
            }
        } catch {
            // A peer that vanished mid-request is routine, not an incident.
            try? await connection.channel.close()
        }

        await admission.release()
    }

    private static func write(
        _ response: BridgeResponse,
        for request: BridgeRequest,
        to outbound: NIOAsyncChannelOutboundWriter<HTTPServerResponsePart>
    ) async throws {
        var headers = HTTPHeaders()
        headers.add(name: "Content-Type", value: "application/json")
        headers.add(name: "Content-Length", value: String(response.body.count))
        headers.add(name: "X-Request-Id", value: request.requestId.uuidString.lowercased())
        headers.add(name: "Connection", value: "close")
        for header in response.extraHeaders {
            headers.add(name: header.name, value: header.value)
        }

        let head = HTTPResponseHead(
            version: .http1_1,
            status: HTTPResponseStatus(statusCode: response.status),
            headers: headers
        )
        try await outbound.write(.head(head))

        // A HEAD response keeps the headers a GET would have produced but carries no body.
        // Nothing routes HEAD — it is a 405 — but emitting a body here would still be wrong.
        if request.method != .HEAD, !response.body.isEmpty {
            var buffer = ByteBufferAllocator().buffer(capacity: response.body.count)
            buffer.writeBytes(response.body)
            try await outbound.write(.body(.byteBuffer(buffer)))
        }

        try await outbound.write(.end(nil))
    }
}

public enum BridgeServerError: Error, Equatable {
    case notBound
}
