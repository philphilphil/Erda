import BridgeCore
import Foundation
import NIOHTTP1
import Testing

@testable import BridgeHTTP

/// The whole stack over a real socket on `127.0.0.1:0` — pipeline, caps, timeouts, admission
/// and the middleware chain — against `FakeReminders`.
@Suite("Socket-level integration", .serialized)
struct SocketTests {
    /// Binds a server on an ephemeral port and tears it down afterwards.
    private func withServer(
        configure: (inout BridgeServerConfiguration) -> Void = { _ in },
        _ body: (TestHarness, Int, BridgeHTTPServer) async throws -> Void
    ) async throws {
        let harness = try TestHarness()
        var configuration = BridgeServerConfiguration(host: "127.0.0.1", port: 0)
        configure(&configuration)

        let server = BridgeHTTPServer(configuration: configuration, services: harness.services)
        let address = try await server.start()
        let port = try #require(address.port)

        do {
            try await body(harness, port, server)
        } catch {
            await server.stop()
            throw error
        }
        await server.stop()
    }

    private func request(
        _ line: String,
        headers: [String] = [],
        body: String = "",
        token: String? = nil
    ) -> String {
        var text = "\(line)\r\nHost: 127.0.0.1\r\n"
        if let token { text += "Authorization: Bearer \(token)\r\n" }
        for header in headers { text += "\(header)\r\n" }
        if !body.isEmpty { text += "Content-Length: \(body.utf8.count)\r\n" }
        text += "\r\n\(body)"
        return text
    }

    private func exchange(port: Int, _ text: String, timeout: TimeInterval = 3) throws -> RawResponse {
        let socket = try RawSocket(host: "127.0.0.1", port: port, timeout: timeout)
        defer { socket.close() }
        try socket.send(text)
        return RawResponse(socket.readToEnd())
    }

    // MARK: - The four routes, end to end

    @Test("all four routes answer over a real socket")
    func happyPaths() async throws {
        try await withServer { harness, port, _ in
            let status = try exchange(port: port, request("GET /v1/status HTTP/1.1", token: harness.token))
            #expect(status.statusCode == 200)
            #expect(status.header("content-type") == "application/json")
            #expect(status.header("x-request-id")?.isEmpty == false)
            #expect(status.json?["availability"] as? String == "ok")

            let created = try exchange(
                port: port,
                request(
                    "POST /v1/reminders HTTP/1.1",
                    headers: ["Content-Type: application/json", "Idempotency-Key: socket-1"],
                    body: createBody,
                    token: harness.token
                )
            )
            #expect(created.statusCode == 201)
            let id = try #require(created.json?["id"] as? String)

            let listed = try exchange(port: port, request("GET /v1/reminders HTTP/1.1", token: harness.token))
            #expect(listed.statusCode == 200)
            #expect(listed.jsonItems?.count == 1)

            let completed = try exchange(
                port: port,
                request(
                    "POST /v1/reminders/\(id)/complete HTTP/1.1",
                    headers: ["Idempotency-Key: socket-2"],
                    token: harness.token
                )
            )
            #expect(completed.statusCode == 200)
            #expect(completed.json?["alreadyCompleted"] as? Bool == false)

            // Four requests, four audit lines.
            #expect(harness.audit.events.count == 4)
        }
    }

    @Test("a query string reaches the list handler")
    func listWithQuery() async throws {
        try await withServer { harness, port, _ in
            let response = try exchange(
                port: port,
                request("GET /v1/reminders?list=Groceries&limit=5 HTTP/1.1", token: harness.token)
            )
            #expect(response.statusCode == 200)
            #expect(response.jsonItems?.isEmpty == true)
        }
    }

    // MARK: - Caps

    @Test("a 17 KiB header is refused by the decoder's limit configuration")
    func oversizedHeaderIsRefused() async throws {
        try await withServer { harness, port, _ in
            let padding = String(repeating: "x", count: 17 * 1024)
            let response = try exchange(
                port: port,
                request("GET /v1/status HTTP/1.1", headers: ["X-Padding: \(padding)"], token: harness.token)
            )
            // NIO's `HTTPServerProtocolErrorHandler` answers a parser error with a 400 and the
            // decoder closes the connection; a bare close is equally acceptable.
            #expect(response.isEmpty || response.statusCode == 400, "got \(response.statusCode)")
            // Whatever happened, it never reached a handler.
            #expect(harness.audit.events.isEmpty)
        }
    }

    @Test("a 17 KiB body is a 413 from the aggregator, before any handler sees it")
    func oversizedBodyIs413() async throws {
        try await withServer { harness, port, _ in
            let padding = String(repeating: "x", count: 17 * 1024)
            let body = #"{"list":"Groceries","title":"\#(padding)"}"#
            let response = try exchange(
                port: port,
                request(
                    "POST /v1/reminders HTTP/1.1",
                    headers: ["Content-Type: application/json", "Idempotency-Key: big"],
                    body: body,
                    token: harness.token
                )
            )
            #expect(response.statusCode == 413)
            #expect(harness.audit.events.isEmpty)
        }
    }

    @Test("a body just under the cap is accepted, so the limit is a cap and not a coincidence")
    func bodyUnderTheCapIsAccepted() async throws {
        try await withServer { harness, port, _ in
            // Notes cap out at 4096, so a large-but-legal body is padded there.
            let notes = String(repeating: "n", count: 4096)
            let body = #"{"list":"Groceries","title":"Buy milk","notes":"\#(notes)"}"#
            #expect(body.utf8.count < 16 * 1024)

            let response = try exchange(
                port: port,
                request(
                    "POST /v1/reminders HTTP/1.1",
                    headers: ["Content-Type: application/json", "Idempotency-Key: nearly-big"],
                    body: body,
                    token: harness.token
                )
            )
            #expect(response.statusCode == 201)
        }
    }

    // MARK: - Protocol gate

    @Test("HTTP/1.0 is 505")
    func http10Is505() async throws {
        try await withServer { harness, port, _ in
            let response = try exchange(port: port, request("GET /v1/status HTTP/1.0", token: harness.token))
            #expect(response.statusCode == 505)
            #expect(response.json?["error"] as? String == "unsupported_http_version")
        }
    }

    @Test("an upgrade attempt is 400")
    func upgradeIs400() async throws {
        try await withServer { harness, port, _ in
            let response = try exchange(
                port: port,
                request(
                    "GET /v1/status HTTP/1.1",
                    headers: ["Connection: Upgrade", "Upgrade: websocket"],
                    token: harness.token
                )
            )
            #expect(response.statusCode == 400)
            #expect(response.json?["error"] as? String == "invalid_request")
        }
    }

    // MARK: - Auth

    @Test("a missing or wrong bearer token is 401 with no message field")
    func unauthorizedIs401() async throws {
        try await withServer { _, port, _ in
            let missing = try exchange(port: port, request("GET /v1/status HTTP/1.1"))
            #expect(missing.statusCode == 401)
            #expect(Set(try #require(missing.json).keys) == ["error", "requestId"])

            let wrong = try exchange(port: port, request("GET /v1/status HTTP/1.1", token: "erdab_nope"))
            #expect(wrong.statusCode == 401)
        }
    }

    // MARK: - Routing

    @Test("routing failures come back over the wire too")
    func routingFailures() async throws {
        try await withServer { harness, port, _ in
            let unknown = try exchange(port: port, request("GET /v1/nope HTTP/1.1", token: harness.token))
            #expect(unknown.statusCode == 404)

            let trailing = try exchange(port: port, request("GET /v1/status/ HTTP/1.1", token: harness.token))
            #expect(trailing.statusCode == 404)

            let traversal = try exchange(
                port: port,
                request("GET /v1/%2e%2e/v1/status HTTP/1.1", token: harness.token)
            )
            #expect(traversal.statusCode == 404)

            let wrongMethod = try exchange(port: port, request("DELETE /v1/reminders HTTP/1.1", token: harness.token))
            #expect(wrongMethod.statusCode == 405)
            #expect(wrongMethod.header("allow") == "GET, POST")
        }
    }

    @Test("a HEAD response carries headers but no body, so framing cannot go wrong")
    func headHasNoBody() async throws {
        try await withServer { harness, port, _ in
            let response = try exchange(port: port, request("HEAD /v1/status HTTP/1.1", token: harness.token))
            #expect(response.statusCode == 405)
            #expect(response.body.isEmpty)
            #expect(response.header("content-length") != nil)
        }
    }

    // MARK: - Timeouts and admission

    @Test("a connection that dribbles headers is closed on the read timeout")
    func slowHeaderWriteIsClosed() async throws {
        try await withServer(configure: { $0.readTimeoutSeconds = 1 }) { _, port, _ in
            let socket = try RawSocket(host: "127.0.0.1", port: port, timeout: 5)
            defer { socket.close() }

            // Half a request, then silence.
            try socket.send("GET /v1/status HTTP/1.1\r\nHost: 127.0.0.1\r\n")

            let started = Date()
            let response = RawResponse(socket.readToEnd())
            let elapsed = Date().timeIntervalSince(started)

            #expect(response.isEmpty, "expected a bare close, got \(response.statusCode)")
            #expect(elapsed < 4, "took \(elapsed)s — the read timeout did not fire")
        }
    }

    @Test("the ninth concurrent connection is accepted and closed without being read")
    func admissionCeiling() async throws {
        try await withServer(configure: { $0.maxConcurrentConnections = 8 }) { _, port, server in
            var held: [RawSocket] = []
            defer { held.forEach { $0.close() } }

            for _ in 0..<8 {
                held.append(try RawSocket(host: "127.0.0.1", port: port, timeout: 2))
            }

            // Wait for the server to have admitted all eight, rather than sleeping and hoping.
            let deadline = Date().addingTimeInterval(5)
            var admitted = 0
            while Date() < deadline, admitted < 8 {
                try await Task.sleep(for: .milliseconds(10))
                admitted = await server.activeConnections
            }
            #expect(admitted == 8, "server admitted \(admitted) connections")

            let ninth = try RawSocket(host: "127.0.0.1", port: port, timeout: 2)
            defer { ninth.close() }
            try ninth.send("GET /v1/status HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n")
            #expect(RawResponse(ninth.readToEnd()).isEmpty, "the ninth connection should be closed unread")
        }
    }

}
