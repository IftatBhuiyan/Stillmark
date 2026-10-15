import AppKit
import SwiftUI
import Carbon

enum ShortcutAction: Int, CaseIterable, Identifiable {
    case area = 1, screen, window, recording
    var id: Int { rawValue }
    var title: String {
        switch self {
        case .area: "Capture area"
        case .screen: "Capture screen"
        case .window: "Capture window"
        case .recording: "Start / stop recording"
        }
    }
    var defaultBinding: ShortcutBinding {
        let keys = [kVK_ANSI_2, kVK_ANSI_3, kVK_ANSI_4, kVK_ANSI_5]
        return ShortcutBinding(keyCode: UInt32(keys[rawValue - 1]), modifiers: UInt32(cmdKey | shiftKey), label: "⌘⇧\(rawValue + 1)")
    }
    var menuTitle: String {
        let binding = ShortcutPreferences.binding(for: self)
        return binding.modifiers == 0 ? title : "\(title)   \(binding.label)"
    }
}

struct ShortcutBinding: Codable {
    var keyCode: UInt32
    var modifiers: UInt32
    var label: String
}

enum ShortcutPreferences {
    static func binding(for action: ShortcutAction) -> ShortcutBinding {
        guard let data = UserDefaults.standard.data(forKey: "shortcut.\(action.rawValue)"),
              let binding = try? JSONDecoder().decode(ShortcutBinding.self, from: data) else { return action.defaultBinding }
        return binding
    }
    static func save(_ binding: ShortcutBinding, for action: ShortcutAction) {
        if let data = try? JSONEncoder().encode(binding) { UserDefaults.standard.set(data, forKey: "shortcut.\(action.rawValue)") }
    }
    static func reset() {
        for action in ShortcutAction.allCases { UserDefaults.standard.removeObject(forKey: "shortcut.\(action.rawValue)") }
    }
}

struct ShortcutRecorder: NSViewRepresentable {
    var action: ShortcutAction
    var updated: () -> Void
    func makeNSView(context: Context) -> ShortcutButton {
        let button = ShortcutButton()
        button.actionID = action; button.changed = updated
        button.bezelStyle = .rounded
        button.setAccessibilityLabel("Shortcut for \(action.title)")
        button.refresh()
        return button
    }
    func updateNSView(_ button: ShortcutButton, context: Context) {
        button.changed = updated
        if !button.listening { button.refresh() }
    }
}

final class ShortcutButton: NSButton {
    var actionID: ShortcutAction = .area
    var changed: (() -> Void)?
    var listening = false
    override var acceptsFirstResponder: Bool { true }

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        target = self; action = #selector(beginRecording)
    }
    required init?(coder: NSCoder) {
        super.init(coder: coder)
        target = self; action = #selector(beginRecording)
    }

    func refresh() { title = ShortcutPreferences.binding(for: actionID).label }

    override func mouseDown(with event: NSEvent) {
        beginRecording()
    }

    @objc private func beginRecording() {
        listening = true; title = "Type shortcut…"
        window?.makeFirstResponder(self)
    }

    override func keyDown(with event: NSEvent) {
        guard listening else { super.keyDown(with: event); return }
        if event.keyCode == kVK_Escape { listening = false; refresh(); return }
        if event.keyCode == kVK_Delete || event.keyCode == kVK_ForwardDelete {
            ShortcutPreferences.save(ShortcutBinding(keyCode: 0, modifiers: 0, label: "Not set"), for: actionID)
            listening = false; refresh(); changed?(); return
        }
        let flags = event.modifierFlags.intersection(.deviceIndependentFlagsMask)
        guard !flags.intersection([.command, .control, .option]).isEmpty else {
            title = "Use ⌘, ⌃ or ⌥"; return
        }
        var modifiers: UInt32 = 0
        var label = ""
        for (flag, carbon, symbol) in [(NSEvent.ModifierFlags.control, controlKey, "⌃"), (.option, optionKey, "⌥"),
                                       (.shift, shiftKey, "⇧"), (.command, cmdKey, "⌘")] {
            if flags.contains(flag) { modifiers |= UInt32(carbon); label += symbol }
        }
        label += (event.charactersIgnoringModifiers ?? "Key \(event.keyCode)").uppercased()
        ShortcutPreferences.save(ShortcutBinding(keyCode: UInt32(event.keyCode), modifiers: modifiers, label: label), for: actionID)
        listening = false; refresh(); changed?()
    }

    override func resignFirstResponder() -> Bool {
        listening = false; refresh(); return super.resignFirstResponder()
    }
}
