import SwiftUI
import AppKit

@main
struct StillmarkApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate
    @StateObject private var model = CaptureModel.shared
    @AppStorage("showMenuBarIcon") private var showMenuBarIcon = true

    var body: some Scene {
        Window("Stillmark", id: "main") {
            WorkspaceView(model: model)
                .frame(minWidth: 1080, minHeight: 620)
                .onAppear { delegate.model = model }
        }
        .defaultSize(width: 1180, height: 780)
        .windowStyle(.hiddenTitleBar)
        .windowToolbarStyle(.unified(showsTitle: false))
        .commands {
            CommandGroup(after: .newItem) {
                Button(ShortcutAction.area.menuTitle) { model.capture(.area) }
                Button(ShortcutAction.screen.menuTitle) { model.capture(.screen) }
                Button(ShortcutAction.window.menuTitle) { model.capture(.window) }
                Button("Capture with Timer") { model.capture(.screen, timed: true) }
                Button(model.isRecording ? "Stop Recording" : ShortcutAction.recording.menuTitle) { model.toggleRecording() }
                Divider()
                Button("Open Image or Project…") { model.importImage() }.keyboardShortcut("o")
                Button("Done") { model.saveAndCloseEditor() }.keyboardShortcut("s")
                    .disabled(model.selectedImage == nil)
                Button("Export Image…") { model.exportImage() }.keyboardShortcut("s", modifiers: [.command, .shift])
                    .disabled(model.selectedImage == nil)
                Button("Save Editable Project…") { model.saveProject() }.keyboardShortcut("s", modifiers: [.command, .option])
                    .disabled(model.selectedImage == nil)
                Button("Copy Image") { model.copyImage() }.keyboardShortcut("c", modifiers: [.command, .shift])
                    .disabled(model.selectedImage == nil)
            }
            CommandGroup(replacing: .undoRedo) {
                Button("Undo Annotation") { model.undo() }.keyboardShortcut("z").disabled(!model.canUndo)
                Button("Redo Annotation") { model.redo() }.keyboardShortcut("z", modifiers: [.command, .shift])
                    .disabled(!model.canRedo)
            }
            CommandGroup(replacing: .help) {
                Link("Stillmark Support & Contributions", destination: CaptureModel.repository)
            }
        }
        MenuBarExtra(isInserted: $showMenuBarIcon) {
            Button(ShortcutAction.area.menuTitle) { model.capture(.area) }
            Button(ShortcutAction.screen.menuTitle) { model.capture(.screen) }
            Button(ShortcutAction.window.menuTitle) { model.capture(.window) }
            Button("Capture with Timer") { model.capture(.screen, timed: true) }
            Divider()
            Button(model.isRecording ? "Stop Recording" : ShortcutAction.recording.menuTitle) { model.toggleRecording() }
            Button("Open Stillmark") {
                model.showEditor()
            }
            Button("Show Capture Folder") { NSWorkspace.shared.open(model.captureDirectory) }
            Button("Restore Last Capture Overlay") { model.restoreOverlay() }
            Divider()
            Button("Quit Stillmark") { NSApp.terminate(nil) }
        } label: {
            Image(systemName: model.isRecording ? "record.circle.fill" : "viewfinder")
                .symbolRenderingMode(.hierarchical)
        }
        Settings { PreferencesView(model: model) }
    }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    weak var model: CaptureModel?
    private var shortcuts: GlobalShortcuts?

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.regular)
        shortcuts = GlobalShortcuts()
        NSApp.activate(ignoringOtherApps: true)
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    func application(_ application: NSApplication, open urls: [URL]) {
        if let url = urls.first { CaptureModel.shared.openFile(url) }
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        CaptureModel.shared.showEditor()
        return true
    }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        guard let model else { return .terminateNow }
        if model.isRecording || model.isBusy {
            let alert = NSAlert()
            alert.messageText = "Finish the current capture before quitting"
            alert.informativeText = "Use Stop Recording so Stillmark can finish and save your video."
            alert.runModal()
            return .terminateCancel
        }
        if model.hasUnsavedAnnotations {
            let alert = NSAlert()
            alert.messageText = "Quit with unexported annotations?"
            alert.informativeText = "Your original captures are saved. Export annotated images before quitting to keep your edits."
            alert.addButton(withTitle: "Keep Editing")
            alert.addButton(withTitle: "Quit")
            return alert.runModal() == .alertSecondButtonReturn ? .terminateNow : .terminateCancel
        }
        return .terminateNow
    }
}
