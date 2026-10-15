import SwiftUI
import AppKit
import ServiceManagement

private enum PreferencePage: String, CaseIterable, Identifiable {
    case general = "General", shortcuts = "Shortcuts", quickAccess = "Quick Access"
    case screenshots = "Screenshots", recording = "Screen Recording", annotate = "Annotate"
    case advanced = "Advanced", about = "About"
    var id: Self { self }
    var symbol: String {
        switch self {
        case .general: "gearshape"
        case .shortcuts: "command"
        case .quickAccess: "rectangle.bottomthird.inset.filled"
        case .screenshots: "camera.fill"
        case .recording: "record.circle"
        case .annotate: "pencil.tip.crop.circle"
        case .advanced: "slider.horizontal.3"
        case .about: "info.circle"
        }
    }
    var color: Color {
        switch self {
        case .general, .shortcuts, .about: .gray
        case .quickAccess: .green
        case .screenshots: .blue
        case .recording: .pink
        case .annotate: .orange
        case .advanced: .purple
        }
    }
}

struct PreferencesView: View {
    @ObservedObject var model: CaptureModel
    @State private var page: PreferencePage? = .general
    @State private var launchAtLogin = false
    @State private var settingsError: String?
    @AppStorage("showMenuBarIcon") private var showMenuBarIcon = true

    var body: some View {
        NavigationSplitView {
            VStack(spacing: 0) {
                HStack(spacing: 10) {
                    Image(systemName: "viewfinder").font(.system(size: 23, weight: .medium)).foregroundStyle(.orange)
                    VStack(alignment: .leading, spacing: 3) {
                        Text("Stillmark").font(.headline)
                        Text("Free & open source").font(.caption).foregroundStyle(.secondary)
                    }
                    Spacer()
                }.padding(20)
                List(selection: $page) {
                    ForEach(PreferencePage.allCases.filter { $0 != .about }) { page in
                        row(page).tag(page)
                    }
                }.listStyle(.sidebar)
                Button { page = .about } label: { row(.about).frame(maxWidth: .infinity, alignment: .leading) }
                    .buttonStyle(.plain).padding(16)
            }
            .navigationSplitViewColumnWidth(220)
        } detail: {
            VStack(alignment: .leading, spacing: 0) {
                Text((page ?? .general).rawValue).font(.system(size: 14, weight: .semibold)).padding(.horizontal, 24).padding(.top, 20)
                Form { preferenceContent }.formStyle(.grouped)
            }
        }
        .frame(width: 820, height: 640)
        .onAppear { launchAtLogin = SMAppService.mainApp.status == .enabled }
        .alert("Settings", isPresented: Binding(get: { settingsError != nil }, set: { if !$0 { settingsError = nil } })) {
            Button("OK") { settingsError = nil }
        } message: { Text(settingsError ?? "") }
    }

    private func row(_ page: PreferencePage) -> some View {
        Label {
            Text(page.rawValue).font(.system(size: 13))
        } icon: {
            Image(systemName: page.symbol).font(.system(size: 13, weight: .semibold))
                .foregroundStyle(.white).frame(width: 23, height: 23)
                .background(page.color, in: RoundedRectangle(cornerRadius: 6))
        }.padding(.vertical, 3)
    }

    @ViewBuilder private var preferenceContent: some View {
        switch page ?? .general {
        case .general:
            Section("App") {
                Toggle("Launch at login", isOn: Binding(get: { launchAtLogin }, set: updateLaunchAtLogin))
                Toggle("Show menu bar icon", isOn: $showMenuBarIcon)
            }
            Section("Sounds") { Toggle("Play capture sound", isOn: $model.captureSound) }
            Section("After Capture") {
                Text("Captures are always saved locally in Pictures/Stillmark.").font(.caption).foregroundStyle(.secondary)
                Toggle("Show Quick Access Overlay", isOn: $model.showOverlayAfterCapture)
                Toggle("Copy screenshot to clipboard", isOn: $model.copyAfterCapture)
                Toggle("Open annotation editor", isOn: $model.openEditorAfterCapture)
                Toggle("Pin screenshot to the screen", isOn: $model.pinAfterCapture)
            }
        case .shortcuts:
            Section("Global shortcuts") {
                ForEach(ShortcutAction.allCases) { action in
                    LabeledContent {
                        HStack {
                            if model.unavailableShortcuts.contains(action.rawValue) { Text("In use").font(.caption).foregroundStyle(.orange) }
                            ShortcutRecorder(action: action) { model.shortcutConfigurationChanged?() }.frame(width: 145, height: 25)
                        }
                    } label: { Text(action.title) }
                }
                Text("Click a shortcut to record a new combination. Escape cancels; Delete clears it. Another capture app or macOS may reserve the defaults.")
                    .font(.caption).foregroundStyle(.secondary)
                Button("Restore Default Shortcuts") { ShortcutPreferences.reset(); model.shortcutConfigurationChanged?() }
            }
            Section("Editor") {
                LabeledContent("Copy image", value: "⇧ ⌘ C")
                LabeledContent("Done", value: "⌘ S")
                LabeledContent("Export image", value: "⇧ ⌘ S")
                LabeledContent("Undo / redo", value: "⌘ Z / ⇧ ⌘ Z")
            }
        case .quickAccess:
            Section("Appearance") {
                Picker("Position on screen", selection: $model.overlayPosition) {
                    Text("Left").tag("left")
                    Text("Right").tag("right")
                }
                Picker("Overlay size", selection: $model.overlaySize) {
                    Text("Small").tag(240)
                    Text("Medium").tag(288)
                    Text("Large").tag(360)
                }
            }
            Section("Behavior") {
                Picker("Auto-close", selection: $model.overlayTimeout) {
                    Text("10 seconds").tag(10)
                    Text("30 seconds").tag(30)
                    Text("Keep until dismissed").tag(0)
                }
                Text("Click the preview to annotate, drag it into another app, or use Copy, Pin, and Show in Finder. Restore the last overlay from the menu bar.")
                    .font(.caption).foregroundStyle(.secondary)
            }
        case .screenshots:
            Section("Output") {
                Picker("File format", selection: $model.screenshotFormat) {
                    Text("PNG · lossless").tag("png")
                    Text("JPEG · smaller files").tag("jpg")
                }
                Text("Screenshots retain native pixel resolution. Annotation exports use PNG.").font(.caption).foregroundStyle(.secondary)
            }
            Section("Capture") {
                Picker("Self-Timer interval", selection: $model.timerInterval) {
                    Text("3 seconds").tag(3)
                    Text("5 seconds").tag(5)
                    Text("10 seconds").tag(10)
                }
                Toggle("Show cursor on fullscreen screenshots", isOn: $model.screenshotCursor)
                Button("Capture with Timer") { model.capture(.screen, timed: true) }
            }
            Section("Window Screenshots") { Toggle("Capture window shadow", isOn: $model.windowShadow) }
        case .recording:
            Section("Video") {
                Picker("Frame rate", selection: $model.recordFPS) {
                    Text("30 fps").tag(30)
                    Text("60 fps").tag(60)
                }
                Picker("Maximum resolution", selection: $model.recordMaxResolution) {
                    Text("1080p").tag(1920)
                    Text("4K").tag(3840)
                }
                Text("H.264 MP4. The encoder preserves aspect ratio and uses even dimensions.").font(.caption).foregroundStyle(.secondary)
            }
            Section("Audio") {
                Toggle("Record system audio", isOn: $model.recordSystemAudio)
                Toggle("Record microphone", isOn: $model.recordMicrophone)
                Text("Audio is off by default. Microphone access is requested only when you enable it and start recording.").font(.caption).foregroundStyle(.secondary)
            }
            Section("Cursor & display") {
                Toggle("Show cursor", isOn: $model.showCursor)
                Picker("Display", selection: $model.displayID) {
                    ForEach(model.displays, id: \.displayID) { display in
                        Text("Display \(display.displayID) · \(display.width) × \(display.height)").tag(display.displayID)
                    }
                }
                Button("Load Displays") { Task { await model.refreshDisplays() } }
            }
        case .annotate:
            Section("Tools") {
                Picker("Default tool", selection: $model.tool) {
                    ForEach(AnnotationTool.allCases) { tool in Text(tool.rawValue).tag(tool) }
                }
                ColorPicker("Annotation color", selection: $model.ink, supportsOpacity: false)
                Text("Solid redaction is flattened into exported pixels. Blur should not be treated as a substitute for redaction.").font(.caption).foregroundStyle(.secondary)
            }
            Section("Window") { Toggle("Always on top when opening editor", isOn: $model.editorAlwaysOnTop) }
        case .advanced:
            Section("Permissions") {
                LabeledContent("Screen capture", value: CGPreflightScreenCaptureAccess() ? "Allowed" : "Not allowed")
                Button("Open Screen Recording Permissions") {
                    NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture")!)
                }
            }
            Section("Storage") {
                Text(model.captureDirectory.path).font(.caption).textSelection(.enabled)
                Button("Open Capture Folder") { NSWorkspace.shared.open(model.captureDirectory) }
                Text("Manage saved captures in Finder. Stillmark does not silently delete your history.").font(.caption).foregroundStyle(.secondary)
            }
        case .about:
            Section {
                HStack(spacing: 16) {
                    Image(systemName: "viewfinder").font(.system(size: 42, weight: .light)).foregroundStyle(.orange)
                    VStack(alignment: .leading, spacing: 4) {
                        Text("Stillmark").font(.title2.bold())
                        Text("1.1 · Mac development preview").foregroundStyle(.secondary)
                        Text("Eclipse Studios · Iftat Bhuiyan").font(.caption)
                    }
                }.padding(.vertical, 10)
                Text("Free software under GNU GPLv3. No account, activation key, or subscription.")
                Link("Source & contribute", destination: CaptureModel.repository)
                Link("Downloads & release notes", destination: CaptureModel.repository.appendingPathComponent("releases"))
                Link("Help & support", destination: CaptureModel.repository.appendingPathComponent("discussions"))
                Link("Eclipse Studios", destination: URL(string: "https://www.eclipsestudios.io/")!)
            }
        }
    }

    private func updateLaunchAtLogin(_ enabled: Bool) {
        Task { @MainActor in
            do {
                if enabled { try SMAppService.mainApp.register() }
                else { try await SMAppService.mainApp.unregister() }
                launchAtLogin = SMAppService.mainApp.status == .enabled
                if enabled && !launchAtLogin { SMAppService.openSystemSettingsLoginItems() }
            } catch { settingsError = error.localizedDescription; launchAtLogin = SMAppService.mainApp.status == .enabled }
        }
    }
}
