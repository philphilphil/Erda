import AppKit
import BridgeStore
import SwiftUI

// NOTE: this file must NOT be called `main.swift` — SwiftPM would treat it as
// top-level code and `@main` becomes an error.

/// The real entry point, so `--selftest` can run and exit **before** SwiftUI takes over the
/// process. `App` supplies its own `static main()`, which cannot be called from an override of
/// itself, so the interception lives in a separate `@main` type that forwards to it.
@main
enum ErdaBridgeLauncher {
    static func main() {
        let arguments = CommandLine.arguments

        if arguments.contains("--selftest") {
            finish(StoreSelfTest.run())
        }
        if arguments.contains("--rotate-token") {
            do {
                let generated = try BridgeEnvironment.rotateToken()
                // Printed exactly once and stored nowhere: only the salted digest reaches the
                // Keychain. The setup UI is the normal route; this stays for a headless rotate.
                print("token id: \(generated.material.tokenId)")
                print(generated.token)
                exit(0)
            } catch {
                FileHandle.standardError.write(Data("token rotation failed: \(error)\n".utf8))
                exit(1)
            }
        }
        if let index = arguments.firstIndex(of: "--selftest-persist") {
            let phase = arguments.dropFirst(index + 1).first.flatMap(StoreSelfTest.PersistencePhase.init(rawValue:))
            guard let phase else {
                FileHandle.standardError.write(Data("usage: --selftest-persist write|read|cleanup\n".utf8))
                exit(2)
            }
            finish(StoreSelfTest.runPersistenceProbe(phase: phase))
        }

        ErdaBridgeApp.main()
    }

    /// Prints to stdout *and* to a file, because launching via `open` detaches stdout — the
    /// report has to be readable after the fact when the app is started the normal way.
    private static func finish(_ report: StoreSelfTest.Report) -> Never {
        print(report.text)
        if let directories = try? BridgeDirectories.standard() {
            try? FilePermissions.createDirectory(at: directories.logs)
            let url = directories.logs.appendingPathComponent("selftest.log")
            try? Data((report.text + "\n").utf8).write(to: url)
            try? FilePermissions.hardenFile(at: url)
        }
        exit(report.passed ? 0 : 1)
    }
}

struct ErdaBridgeApp: App {
    @State private var model = AppModel()

    var body: some Scene {
        MenuBarExtra {
            MenuContent(model: model)
        } label: {
            // The symbol changes with `readiness`, so a bridge that cannot serve a request looks
            // different in the menu bar without anyone opening the window.
            Image(systemName: model.menuBarSymbol)
                .symbolRenderingMode(.multicolor)
        }

        Window("ErdaBridge Setup", id: SetupWindow.id) {
            SetupView(model: model)
        }
        .defaultSize(width: 560, height: 640)
    }
}
