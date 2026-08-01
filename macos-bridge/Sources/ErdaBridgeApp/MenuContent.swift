import AppKit
import SwiftUI

/// The menu bar drop-down. Read-only apart from the three things worth doing without opening a
/// window: granting access, starting/stopping the listener, and quitting.
struct MenuContent: View {
    @Bindable var model: AppModel
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        Text(model.readiness.text)
        Text("Reminders: \(model.authorization.displayText)")
        Text("Listener: \(model.listenerText)")

        Divider()

        Button("Setup…") { openSetup() }

        if !model.authorization.isUsable {
            Button("Request Reminders access") { model.requestAccess() }
        }

        if model.serverState.isSupervising {
            Button("Stop listener") { model.stopListener() }
        } else {
            Button("Start listener") { model.startListener() }
        }

        Divider()

        Button("Quit ErdaBridge") { NSApplication.shared.terminate(nil) }
    }

    private func openSetup() {
        model.reloadAll()
        openWindow(id: SetupWindow.id)
        // An `LSUIElement` app is not in the activation order, so a window opened from the menu
        // bar appears *behind* whatever is frontmost unless the app activates itself first.
        NSApp.activate(ignoringOtherApps: true)
    }
}
