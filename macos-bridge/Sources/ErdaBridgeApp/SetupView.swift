import AppKit
import BridgeCore
import BridgeEventKit
import SwiftUI

enum SetupWindow {
    static let id = "setup"
}

/// The whole local control surface: authorization, bind address, allowlist, broken bindings and
/// the token. None of it is reachable over HTTP — the remote API has no route that can change any
/// of these, which is why this window exists at all.
struct SetupView: View {
    @Bindable var model: AppModel

    var body: some View {
        Form {
            StatusSection(model: model)
            AccessSection(model: model)
            ListenerSection(model: model)
            ListsSection(model: model)
            if !model.brokenBindings.isEmpty {
                BrokenSection(model: model)
            }
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
            LabeledContent(
                "Allowlist",
                value: "\(model.healthyBindingCount) usable, \(model.brokenBindings.count) broken"
            )
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

private struct ListsSection: View {
    @Bindable var model: AppModel

    var body: some View {
        Section("Reminder lists") {
            if !model.authorization.isUsable {
                Text("Grant Reminders access to see the lists on this Mac.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            } else if model.listRows.isEmpty {
                Text("No reminder lists found.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            } else {
                ForEach(model.listRows) { row in
                    ListRowView(
                        row: row,
                        rejection: { model.aliasRejection($0, for: row.list) },
                        onAllow: { model.allow(row.list, alias: $0) },
                        onUnbind: { model.unbind($0) }
                    )
                    .id(row.id)
                }
                Button("Reload lists") { model.reloadLists() }
            }
        }
    }
}

private struct ListRowView: View {
    let row: ListRow
    let rejection: (String) -> String?
    let onAllow: (String) -> Void
    let onUnbind: (Alias) -> Void

    @State private var draft = ""
    @State private var confirmingUnbind = false

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(alignment: .firstTextBaseline) {
                VStack(alignment: .leading, spacing: 2) {
                    Text(row.list.title)
                    Text("\(row.list.source) · \(row.list.isWritable ? "writable" : "read-only")")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                Spacer()
                if let binding = row.binding {
                    Text(binding.alias.rawValue).monospaced()
                    Button("Unbind") { confirmingUnbind = true }
                }
            }

            if row.binding == nil {
                HStack {
                    TextField("alias", text: $draft)
                        .textFieldStyle(.roundedBorder)
                        .frame(maxWidth: 180)
                    Button("Allow") {
                        onAllow(draft)
                        draft = ""
                    }
                    .disabled(draft.isEmpty || rejection(draft) != nil)
                }
                if let message = rejection(draft) {
                    Text(message).font(.caption).foregroundStyle(.red)
                }
                if !row.list.isWritable {
                    Text("Read-only: reminders here can be listed and completed, but not created.")
                        .font(.caption)
                        .foregroundStyle(.orange)
                }
            }
        }
        .confirmationDialog(
            "Remove the alias “\(row.binding?.alias.rawValue ?? "")”?",
            isPresented: $confirmingUnbind,
            titleVisibility: .visible
        ) {
            Button("Remove", role: .destructive) {
                if let binding = row.binding { onUnbind(binding.alias) }
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("Erda will get alias_unknown for this list until you allow it again. No reminders are changed.")
        }
    }
}

// MARK: - Broken bindings

private struct BrokenSection: View {
    @Bindable var model: AppModel

    var body: some View {
        Section("Broken bindings") {
            Text(
                """
                A list identifier is not sync-proof, so an iCloud resync can leave an alias \
                pointing at nothing. ErdaBridge will never re-point one by matching the title — \
                that is how it would end up writing into somebody else's shared list. Pick the \
                right list yourself.
                """
            )
            .font(.caption)
            .foregroundStyle(.secondary)

            ForEach(model.brokenBindings) { broken in
                BrokenRowView(
                    broken: broken,
                    candidates: model.rebindCandidates(for: broken.entry),
                    onRebind: { model.rebind(broken.entry.alias, to: $0) },
                    onRemove: { model.unbind(broken.entry.alias) }
                )
                .id(broken.id)
            }
        }
    }
}

private struct BrokenRowView: View {
    let broken: BrokenBinding
    let candidates: [ReminderListInfo]
    let onRebind: (ReminderListInfo) -> Void
    let onRemove: () -> Void

    /// Starts empty and is never pre-filled: the confirmation has to name a list a human chose.
    @State private var choice: String = ""
    @State private var confirming = false

    private var chosen: ReminderListInfo? {
        candidates.first { $0.calendarId == choice }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(alignment: .firstTextBaseline) {
                Text(broken.entry.alias.rawValue).monospaced()
                Spacer()
                Text("was “\(broken.entry.titleAtBind)” in \(broken.entry.sourceAtBind)")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            Text(broken.explanation).font(.caption).foregroundStyle(.orange)

            if candidates.isEmpty {
                Text("No lists are visible to re-bind to.").font(.caption).foregroundStyle(.secondary)
            } else {
                Picker("Re-bind to", selection: $choice) {
                    Text("Choose a list…").tag("")
                    ForEach(candidates, id: \.calendarId) { list in
                        Text("\(list.title) — \(list.source)").tag(list.calendarId)
                    }
                }
            }

            HStack {
                Button("Re-bind…") { confirming = true }
                    .disabled(chosen == nil)
                Button("Remove alias", role: .destructive) { onRemove() }
            }
        }
        .confirmationDialog(
            "Re-bind “\(broken.entry.alias.rawValue)” to “\(chosen?.title ?? "")”?",
            isPresented: $confirming,
            titleVisibility: .visible
        ) {
            Button("Re-bind") {
                if let chosen { onRebind(chosen) }
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text(
                "Erda will read and write reminders in \(chosen.map { "“\($0.title)” (\($0.source))" } ?? "this list") "
                    + "under the alias “\(broken.entry.alias.rawValue)”. Check it is the list you mean."
            )
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
