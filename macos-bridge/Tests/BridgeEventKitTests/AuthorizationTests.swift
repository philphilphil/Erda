import BridgeCore
import Foundation
import Testing

@testable import BridgeEventKit

@Suite("Authorization")
struct AuthorizationTests {
    /// Write-only access can create a reminder but cannot read one back, so it satisfies neither
    /// `list` nor `complete`. Accepting it would produce a bridge that half works.
    @Test("only full access is usable")
    func onlyFullAccessIsUsable() {
        for status in RemindersAuthorization.allCases {
            #expect(status.isUsable == (status == .fullAccess), "\(status) usability")
        }
    }

    @Test("every status has readable text for the setup window")
    func hasDisplayText() {
        for status in RemindersAuthorization.allCases {
            #expect(!status.displayText.isEmpty)
        }
    }

    /// A macOS release adding a new `EKAuthorizationStatus` must fail closed, not crash and not
    /// be optimistically treated as access.
    @Test("an unknown future status is not usable")
    func unknownFailsClosed() {
        #expect(RemindersAuthorization.unknown.isUsable == false)
    }

    /// Reading the status raises no TCC prompt and needs no grant, so this is safe everywhere —
    /// including in a checkout that has never been given Reminders access.
    @Test("reading the status is side-effect free and always answers")
    func statusAlwaysAnswers() {
        #expect(RemindersAuthorization.allCases.contains(RemindersAccess.status()))
    }

    /// Enumerating lists is a local-only capability with no HTTP route; when access is not usable
    /// it must answer "nothing" rather than reaching for the store.
    @Test("lists and the count agree, and are empty without usable access")
    func listsMatchCount() {
        let lists = RemindersAccess.lists()
        #expect(lists.count == RemindersAccess.reminderListCount())
        if !RemindersAccess.status().isUsable {
            #expect(lists.isEmpty)
        }
    }
}
