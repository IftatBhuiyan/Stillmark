import SwiftUI
import AppKit

@main
struct StillmarkApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate
    @StateObject private var model = CaptureModel.shared

    var body: some Scene {
        WindowGroup("Stillmark", id: "main") {
            WorkspaceView(model: model)
                .frame(minWidth: 920, minHeight: 620)
                .onAppear { delegate.model = model }
        }
        .defaultSize(width: 1180, height: 780)
        .commands {
            CommandGroup(after: .newItem) {
                Button("Capture Area") { model.capture(.area) }.keyboardShortcut("2", modifiers: [.command, .shift])
                Button("Capture Screen") { model.capture(.screen) }.keyboardShortcut("3", modifiers: [.command, .shift])
                Button("Capture Window") { model.capture(.window) }.keyboardShortcut("4", modifiers: [.command, .shift])
                Button(model.isRecording ? "Stop Recording" : "Record Screen") { model.toggleRecording() }
                    .keyboardShortcut("5", modifiers: [.command, .shift])
                Divider()
                Button("Import Image…") { model.importImage() }.keyboardShortcut("o")
                Button("Export Image…") { model.exportImage() }.keyboardShortcut("s")
                    .disabled(model.selectedImage == nil)
            }
            CommandGroup(replacing: .undoRedo) {
                Button("Undo Annotation") { model.undo() }.keyboardShortcut("z").disabled(model.marks.isEmpty)
                Button("Redo Annotation") { model.redo() }.keyboardShortcut("z", modifiers: [.command, .shift])
                    .disabled(model.redoMarks.isEmpty)
            }
            CommandGroup(replacing: .help) {
                Link("Stillmark Support & Contributions", destination: CaptureModel.repository)
            }
        }
        MenuBarExtra {
            Button("Capture Area   ⌘⇧2") { model.capture(.area) }
            Button("Capture Screen   ⌘⇧3") { model.capture(.screen) }
            Button("Capture Window   ⌘⇧4") { model.capture(.window) }
            Divider()
            Button(model.isRecording ? "Stop Recording" : "Record Screen   ⌘⇧5") { model.toggleRecording() }
            Button("Open Stillmark") {
                NSApp.activate(ignoringOtherApps: true)
                NSApp.windows.first { $0.title == "Stillmark" }?.makeKeyAndOrderFront(nil)
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
