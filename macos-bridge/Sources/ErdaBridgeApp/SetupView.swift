import AppKit
import BridgeCore
import BridgeEventKit
import SwiftUI

enum SetupWindow {
    static let id = "setup"
}

/// The whole local control surface: authorization, bind address and the token. None of it is
/// reachable over HTTP — the remote API has no route that can change any of these, which is why
/// this window exists at all.
///
/// There is no list picker. The bridge reaches every reminder list on this Mac, so there is
/// nothing here to choose; the list section below is a read-only inventory, and it is there
/// because the names it shows are exactly what Erda has to send.
struct SetupView: View {
    @Bindable var model: AppModel

    var body: some View {
        Form {
            StatusSection(model: model)
            AccessSection(model: model)
            ListenerSection(model: model)
            ListsSection(model: model)
            TokenSection(model: model)
            FilesSection(model: model)

            if let error = model.actionError {
                Section {
                    Text(error).font(.caption).foregroundStyle(.red).textSelection(.enabled)
                }
            }
        }
        .formStyle(.grouped)
        .frame(minWidth: 520, minHeight: 520)
        .onAppear { model.reloadAll() }
    }
}

// MARK: - Status

private struct StatusSection: View {
    @Bindable var model: AppModel

    var body: some View {
        Section("Status") {
            HStack(spacing: 8) {
                Circle().fill(model.readiness.tint).frame(width: 10, height: 10)
                Text(model.readiness.text)
                Spacer()
                Button("Refresh") { model.reloadAll() }
            }
            LabeledContent("Reminders access", value: model.authorization.displayText)
            LabeledContent("Listener", value: model.listenerText)
            LabeledContent("Scope", value: model.scopeText)
            LabeledContent("Last request", value: model.lastRequestText)
        }
    }
}

// MARK: - Reminders access

private struct AccessSection: View {
    @Bindable var model: AppModel

    var body: some View {
        Section("Reminders access") {
            if model.authorization.isUsable {
                Text("Full access granted. Revoke it in System Settings › Privacy & Security › Reminders.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            } else {
                Text(
                    """
                    ErdaBridge needs full access — write-only cannot read a reminder back, so it \
                    could satisfy neither list nor complete. macOS only shows the prompt once; \
                    after a denial it has to be changed in System Settings.
                    """
                )
                .font(.caption)
                .foregroundStyle(.secondary)
                Button("Request Reminders access") { model.requestAccess() }
            }
        }
    }
}

// MARK: - Listener

private struct ListenerSection: View {
    @Bindable var model: AppModel

    var body: some View {
        Section("Listener") {
            Picker("Bind address", selection: $model.draftAddress) {
                Text("Choose an address…").tag("")
                ForEach(model.addressChoices) { choice in
                    Text(choice.label).tag(choice.literal)
                }
            }

            TextField("Port", text: $model.draftPort)
                .frame(maxWidth: 120)

            Text(
                """
                Only addresses currently configured on this Mac are offered, and nothing is picked \
                for you: binding whatever happens to be available could publish the bridge on a \
                network you did not mean to. If the address you want is missing, wait for DHCP or \
                give this Mac a reservation on the router.
                """
            )
            .font(.caption)
            .foregroundStyle(.secondary)

            if let error = model.bindError {
                Text(error).font(.caption).foregroundStyle(.red)
            }

            HStack {
                Button("Save and restart listener") { model.saveBindSelection() }
                    .disabled(!model.canSaveBindSelection)
                Button("Rescan addresses") { model.rescanAddresses() }
                Spacer()
                if model.serverState.isSupervising {
                    Button("Stop") { model.stopListener() }
                } else {
                    Button("Start") { model.startListener() }
                }
            }

            LabeledContent(
                "Stored",
                value: model.storedSelection?.displayText ?? "nothing stored yet"
            )
        }
    }
}

// MARK: - Lists

/// Read-only on purpose. Erda addresses a list by the name shown here, so the one job of this
/// section is to show the exact spelling — there is nothing to allow, bind or unbind.
private struct ListsSection: View {
    @Bindable var model: AppModel

    var body: some View {
        Section("Reminder lists") {
            Text(
                """
                ErdaBridge can read and write **all** of these — macOS grants Reminders access \
                all-or-nothing, and this app no longer pretends otherwise with a list of its own. \
                Erda names a list by its title, exactly as it reads here.
                """
            )
            .font(.caption)
            .foregroundStyle(.secondary)

            if !model.authorization.isUsable {
                Text("Grant Reminders access to see the lists on this Mac.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            } else if model.lists.isEmpty {
                Text("No reminder lists found.")
                    .font(.caption)
                    .foregroundStyle(.orange)
            } else {
                ForEach(model.lists, id: \.calendarId) { list in
                    HStack(alignment: .firstTextBaseline) {
                        Text(list.title).textSelection(.enabled)
                        Spacer()
                        Text("\(list.source) · \(list.isWritable ? "writable" : "read-only")")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                }
                Button("Reload lists") { model.reloadLists() }
            }
        }
    }
}

// MARK: - Token

private struct TokenSection: View {
    @Bindable var model: AppModel

    @State private var confirming = false

    var body: some View {
        Section("API token") {
            if let summary = model.tokenSummary {
                LabeledContent("Token id", value: summary.tokenId.rawValue)
                LabeledContent(
                    "Created",
                    value: summary.createdAt.formatted(date: .abbreviated, time: .shortened)
                )
            } else {
                Text("No token yet — every request will be refused with 401 until one exists.")
                    .font(.caption)
                    .foregroundStyle(.orange)
            }
            LabeledContent("Stored in", value: model.tokenBackendName)

            if let token = model.revealedToken {
                Text("Copy this now. It is shown once and stored nowhere — only a salted digest is kept.")
                    .font(.caption)
                    .foregroundStyle(.orange)
                Text(token)
                    .monospaced()
                    .textSelection(.enabled)
                    .padding(6)
                    .background(.quaternary, in: RoundedRectangle(cornerRadius: 6))
                HStack {
                    Button("Copy") { model.copyRevealedToken() }
                    Button("Done") { model.dismissRevealedToken() }
                }
            } else {
                Button(model.tokenSummary == nil ? "Generate token" : "Rotate token") {
                    if model.tokenSummary == nil {
                        model.rotateToken()
                    } else {
                        confirming = true
                    }
                }
            }
        }
        .confirmationDialog(
            "Rotate the API token?",
            isPresented: $confirming,
            titleVisibility: .visible
        ) {
            Button("Rotate", role: .destructive) { model.rotateToken() }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text(
                """
                The current token stops working immediately — every Erda request will get 401 \
                until you put the new token into AppleBridge__ApiKey in .env on the server and \
                restart it. The new token is shown once.
                """
            )
        }
    }
}

// MARK: - Files

private struct FilesSection: View {
    @Bindable var model: AppModel

    var body: some View {
        Section("Files") {
            LabeledContent("Database") {
                Text(model.databasePath).font(.caption).textSelection(.enabled)
            }
            LabeledContent("Audit log") {
                Text(model.auditLogPath).font(.caption).textSelection(.enabled)
            }
            if let error = model.startupError {
                Text(error).font(.caption).foregroundStyle(.red).textSelection(.enabled)
            }
        }
    }
}

// MARK: - Presentation

extension Readiness {
    var tint: Color {
        switch self {
        case .ready: .green
        case .degraded: .orange
        case .blocked: .red
        }
    }
}
