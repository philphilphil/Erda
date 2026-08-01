import Darwin
import Foundation

/// A deliberately dumb TCP client.
///
/// The socket tests have to send byte sequences no HTTP client would produce — a 17 KiB header,
/// half a request followed by silence, `Upgrade:` — and have to be able to observe a bare EOF
/// with no response at all. A real HTTP client abstracts away exactly the things under test.
final class RawSocket {
    enum SocketError: Error {
        case connectFailed(Int32)
        case sendFailed(Int32)
    }

    private var descriptor: Int32

    init(host: String, port: Int, timeout: TimeInterval = 3) throws {
        descriptor = Darwin.socket(AF_INET, SOCK_STREAM, 0)
        guard descriptor >= 0 else { throw SocketError.connectFailed(errno) }

        var address = sockaddr_in()
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = UInt16(port).bigEndian
        inet_pton(AF_INET, host, &address.sin_addr)

        let status = withUnsafePointer(to: &address) { pointer in
            pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) { sockaddrPointer in
                Darwin.connect(descriptor, sockaddrPointer, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        guard status == 0 else {
            let code = errno
            Darwin.close(descriptor)
            descriptor = -1
            throw SocketError.connectFailed(code)
        }

        setReadTimeout(timeout)
    }

    deinit {
        close()
    }

    func setReadTimeout(_ timeout: TimeInterval) {
        var value = timeval(
            tv_sec: Int(timeout),
            tv_usec: Int32((timeout - Double(Int(timeout))) * 1_000_000)
        )
        setsockopt(descriptor, SOL_SOCKET, SO_RCVTIMEO, &value, socklen_t(MemoryLayout<timeval>.size))
    }

    func send(_ bytes: [UInt8]) throws {
        var offset = 0
        while offset < bytes.count {
            let written = bytes[offset...].withUnsafeBufferPointer { buffer in
                Darwin.send(descriptor, buffer.baseAddress, buffer.count, 0)
            }
            // A peer that closed on us mid-write is a legitimate outcome for several of these
            // tests (the server rejecting an oversized body, for instance), not a test failure.
            guard written > 0 else { return }
            offset += written
        }
    }

    func send(_ text: String) throws {
        try send(Array(text.utf8))
    }

    /// Reads until the peer closes or the socket read times out.
    func readToEnd() -> [UInt8] {
        var collected: [UInt8] = []
        var chunk = [UInt8](repeating: 0, count: 4096)
        while true {
            let count = chunk.withUnsafeMutableBufferPointer { buffer in
                Darwin.recv(descriptor, buffer.baseAddress, buffer.count, 0)
            }
            guard count > 0 else { return collected }
            collected.append(contentsOf: chunk[0..<count])
        }
    }

    func close() {
        if descriptor >= 0 {
            Darwin.close(descriptor)
            descriptor = -1
        }
    }
}

/// The bare minimum HTTP response parsing needed to assert on a status line and headers.
struct RawResponse {
    let statusCode: Int
    let headers: [(name: String, value: String)]
    let body: String
    /// The peer closed without sending anything at all.
    let isEmpty: Bool

    init(_ bytes: [UInt8]) {
        guard !bytes.isEmpty else {
            statusCode = 0
            headers = []
            body = ""
            isEmpty = true
            return
        }

        isEmpty = false
        let text = String(decoding: bytes, as: UTF8.self)
        let parts = text.components(separatedBy: "\r\n\r\n")
        let headerBlock = parts.first ?? ""
        body = parts.count > 1 ? parts.dropFirst().joined(separator: "\r\n\r\n") : ""

        var lines = headerBlock.components(separatedBy: "\r\n")
        let statusLine = lines.isEmpty ? "" : lines.removeFirst()
        let statusFields = statusLine.split(separator: " ")
        statusCode = statusFields.count > 1 ? Int(statusFields[1]) ?? 0 : 0

        headers = lines.compactMap { line in
            guard let separator = line.firstIndex(of: ":") else { return nil }
            return (
                name: String(line[line.startIndex..<separator]).lowercased(),
                value: String(line[line.index(after: separator)...]).trimmingCharacters(in: .whitespaces)
            )
        }
    }

    func header(_ name: String) -> String? {
        headers.first { $0.name == name.lowercased() }?.value
    }

    var json: [String: Any]? {
        (try? JSONSerialization.jsonObject(with: Data(body.utf8))) as? [String: Any]
    }

    var jsonArray: [[String: Any]]? {
        (try? JSONSerialization.jsonObject(with: Data(body.utf8))) as? [[String: Any]]
    }

    /// The `items` of a `{"items":[…]}` wrapper body, as `GET /v1/reminders` answers.
    var jsonItems: [[String: Any]]? {
        json?["items"] as? [[String: Any]]
    }
}
