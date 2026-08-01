import Foundation
import Testing

@testable import BridgeCore

@Suite("IP literal parsing")
struct IPAddressTests {
    @Test("IPv4 literals", arguments: [
        ("0.0.0.0", [0, 0, 0, 0] as [UInt8]),
        ("127.0.0.1", [127, 0, 0, 1]),
        ("192.168.178.106", [192, 168, 178, 106]),
        ("255.255.255.255", [255, 255, 255, 255]),
    ])
    func parsesIPv4(text: String, bytes: [UInt8]) {
        #expect(IPAddress.parse(text) == IPAddress.v4(bytes))
    }

    @Test("malformed IPv4", arguments: [
        "192.168.178", "192.168.178.106.1", "192.168.178.256", "192.168.178.-1",
        "192.168.178.0106",  // leading zero is octal in some parsers, decimal in others
        "192.168..1", "192.168.178.a", "", ".", "1.2.3.4 ",
    ])
    func rejectsMalformedIPv4(text: String) {
        #expect(IPAddress.parse(text) == nil)
    }

    @Test("IPv6 literals normalise regardless of case or compression")
    func parsesIPv6() {
        #expect(IPAddress.parse("::") == IPAddress.v6(Array(repeating: 0, count: 16)))
        #expect(IPAddress.parse("::1") == IPAddress.v6(Array(repeating: 0, count: 15) + [1]))
        #expect(IPAddress.parse("fe80::1") == IPAddress.parse("FE80:0000:0000:0000:0000:0000:0000:0001"))
        #expect(IPAddress.parse("2001:db8:0:0:0:0:2:1") == IPAddress.parse("2001:db8::2:1"))
        // Nine groups is not an address, however plausible it looks.
        #expect(IPAddress.parse("2001:0db8:0000:0000:0000:0000:8a2e:0370:7334") == nil)
    }

    @Test("an IPv4-mapped address is normalised to its IPv4 form")
    func normalisesIPv4Mapped() {
        #expect(IPAddress.parse("::ffff:192.168.178.106") == IPAddress.v4([192, 168, 178, 106]))
        #expect(IPAddress.parse("::ffff:127.0.0.1")?.isLoopback == true)
    }

    @Test("malformed IPv6", arguments: [
        ":::", "1::2::3", "fe80:::1", ":1:2:3:4:5:6:7", "1:2:3:4:5:6:7:8:9",
        "1:2:3:4:5:6:7", "12345::1", "fe80::g", "fe80::1%en10", "::ffff:999.1.1.1", "1::2:",
    ])
    func rejectsMalformedIPv6(text: String) {
        #expect(IPAddress.parse(text) == nil, "\(text) should not parse")
    }

    @Test("hostnames are not IP literals", arguments: [
        "localhost", "erda.local", "macbook", "example.com", "192.168.178.106:17832",
    ])
    func rejectsHostnames(text: String) {
        #expect(IPAddress.parse(text) == nil)
    }

    @Test("wildcard and loopback are recognised in both families")
    func classifiesAddresses() {
        #expect(IPAddress.parse("0.0.0.0")?.isWildcard == true)
        #expect(IPAddress.parse("::")?.isWildcard == true)
        #expect(IPAddress.parse("192.168.178.106")?.isWildcard == false)

        #expect(IPAddress.parse("127.0.0.1")?.isLoopback == true)
        #expect(IPAddress.parse("127.1.2.3")?.isLoopback == true)
        #expect(IPAddress.parse("::1")?.isLoopback == true)
        #expect(IPAddress.parse("192.168.178.106")?.isLoopback == false)
        #expect(IPAddress.parse("fe80::1")?.isLoopback == false)
    }

    @Test("canonical text is stable and lowercase")
    func rendersCanonicalText() {
        #expect(IPAddress.parse("192.168.178.106")?.canonicalText == "192.168.178.106")
        #expect(IPAddress.parse("FE80::1")?.canonicalText == "fe80:0000:0000:0000:0000:0000:0000:0001")
    }
}

@Suite("Bind address validation")
struct BindAddressTests {
    private let interfaces = StaticInterfaceLister([
        "127.0.0.1", "::1", "192.168.178.106", "fe80::1c2b:3d4e:5f60:7a8b", "not-an-address",
    ])

    @Test("the configured LAN address is accepted")
    func acceptsLanAddress() throws {
        let bound = try BindAddressValidator.validate(
            ipAddress: "192.168.178.106",
            port: 17832,
            interfaces: interfaces
        )
        #expect(bound.ipAddress == "192.168.178.106")
        #expect(bound.port == 17832)
        #expect(bound.parsed == .v4([192, 168, 178, 106]))
    }

    @Test("the wildcard is refused", arguments: ["0.0.0.0", "::", "0000:0000:0000:0000:0000:0000:0000:0000"])
    func rejectsWildcard(text: String) {
        #expect(throws: BindAddressError.wildcardNotAllowed) {
            try BindAddressValidator.validate(ipAddress: text, port: 17832, interfaces: interfaces)
        }
    }

    @Test("loopback is refused by default — it would be unreachable from Erda")
    func rejectsLoopback() {
        #expect(throws: BindAddressError.loopbackNotAllowed) {
            try BindAddressValidator.validate(ipAddress: "127.0.0.1", port: 17832, interfaces: interfaces)
        }
        #expect(throws: BindAddressError.loopbackNotAllowed) {
            try BindAddressValidator.validate(ipAddress: "::1", port: 17832, interfaces: interfaces)
        }
    }

    @Test("loopback is available to tests through an explicit policy, not a back door")
    func allowsLoopbackUnderPolicy() throws {
        let bound = try BindAddressValidator.validate(
            ipAddress: "127.0.0.1",
            port: 17832,
            interfaces: interfaces,
            policy: BindAddressPolicy(allowLoopback: true)
        )
        #expect(bound.parsed == .v4([127, 0, 0, 1]))
    }

    @Test("hostnames are refused — DNS must not decide what the bridge binds to", arguments: [
        "localhost", "macbook.local", "erda", "192.168.178.106%en10",
    ])
    func rejectsHostname(text: String) {
        #expect(throws: BindAddressError.notAnIPLiteral) {
            try BindAddressValidator.validate(ipAddress: text, port: 17832, interfaces: interfaces)
        }
    }

    @Test("an address that is not on any interface is refused before the socket layer sees it")
    func rejectsForeignAddress() {
        #expect(throws: BindAddressError.notOnLocalInterface) {
            try BindAddressValidator.validate(ipAddress: "192.168.178.26", port: 17832, interfaces: interfaces)
        }
        #expect(throws: BindAddressError.notOnLocalInterface) {
            try BindAddressValidator.validate(ipAddress: "10.0.0.1", port: 17832, interfaces: interfaces)
        }
    }

    @Test("an interface entry the parser does not understand is skipped, not fatal")
    func toleratesUnparseableInterfaceEntries() throws {
        // The list above contains "not-an-address"; a valid address after it must still match.
        let bound = try BindAddressValidator.validate(
            ipAddress: "fe80:0:0:0:1c2b:3d4e:5f60:7a8b",
            port: 17832,
            interfaces: interfaces
        )
        #expect(bound.parsed == IPAddress.parse("fe80::1c2b:3d4e:5f60:7a8b"))
    }

    @Test("ports outside the unprivileged range are refused", arguments: [0, 80, 443, 1023, 65536, -1])
    func rejectsPortOutOfRange(port: Int) {
        #expect(throws: BindAddressError.portOutOfRange) {
            try BindAddressValidator.validate(ipAddress: "192.168.178.106", port: port, interfaces: interfaces)
        }
    }

    @Test("a failing interface lister is a config error, not a silent pass")
    func reportsEnumerationFailure() {
        struct FailingLister: NetworkInterfaceLister {
            struct Boom: Error {}
            func localAddresses() throws -> [String] { throw Boom() }
        }

        #expect(throws: BindAddressError.interfaceEnumerationFailed) {
            try BindAddressValidator.validate(
                ipAddress: "192.168.178.106",
                port: 17832,
                interfaces: FailingLister()
            )
        }
    }

    @Test("a stored selection goes through the same validator, not a second one")
    func validatesStoredSelection() throws {
        let bound = try BindAddressValidator.validate(
            BindSelection(ipAddress: "192.168.178.106", port: 17832),
            interfaces: interfaces
        )
        #expect(bound.selection == BindSelection(ipAddress: "192.168.178.106", port: 17832))
        #expect(bound.displayText == "192.168.178.106:17832")

        // The rules do not soften for a value that came off disk: an address the machine no
        // longer has is refused however long it has been stored.
        #expect(throws: BindAddressError.notOnLocalInterface) {
            try BindAddressValidator.validate(
                BindSelection(ipAddress: "192.168.178.103", port: 17832),
                interfaces: interfaces
            )
        }
        #expect(throws: BindAddressError.wildcardNotAllowed) {
            try BindAddressValidator.validate(
                BindSelection(ipAddress: "0.0.0.0", port: 17832),
                interfaces: interfaces
            )
        }
    }

    @Test("the interface list is only consulted once the literal itself is acceptable")
    func rejectsWildcardWithoutTouchingInterfaces() {
        final class CountingLister: NetworkInterfaceLister, @unchecked Sendable {
            let counter = ReadCounter()
            func localAddresses() throws -> [String] {
                counter.bump()
                return ["0.0.0.0"]
            }
        }

        let lister = CountingLister()
        #expect(throws: BindAddressError.wildcardNotAllowed) {
            try BindAddressValidator.validate(ipAddress: "0.0.0.0", port: 17832, interfaces: lister)
        }
        #expect(lister.counter.reads == 0)
    }
}
