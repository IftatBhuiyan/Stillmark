import Carbon

/// Carbon hotkeys need no Accessibility permission or continuous keyboard event monitoring.
@MainActor
final class GlobalShortcuts {
    private var refs: [EventHotKeyRef] = []
    private var handler: EventHandlerRef?

    init() {
        var type = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), { _, event, _ in
            guard let event else { return OSStatus(eventNotHandledErr) }
            var id = EventHotKeyID()
            let status = GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID), nil,
                                           MemoryLayout<EventHotKeyID>.size, nil, &id)
            guard status == noErr else { return status }
            let action = id.id
            Task { @MainActor in
                let model = CaptureModel.shared
                switch action {
                case 1: model.capture(.area)
                case 2: model.capture(.screen)
                case 3: model.capture(.window)
                case 4: model.toggleRecording()
                default: break
                }
            }
            return noErr
        }, 1, &type, nil, &handler)
        CaptureModel.shared.shortcutConfigurationChanged = { [weak self] in self?.reload() }
        reload()
    }

    private func reload() {
        refs.forEach { UnregisterEventHotKey($0) }
        refs = []
        var unavailable: Set<Int> = []
        for action in ShortcutAction.allCases {
            let binding = ShortcutPreferences.binding(for: action)
            guard binding.modifiers != 0 else { continue }
            var ref: EventHotKeyRef?
            let id = EventHotKeyID(signature: 0x53544D4B, id: UInt32(action.rawValue))
            if RegisterEventHotKey(binding.keyCode, binding.modifiers, id, GetApplicationEventTarget(), 0, &ref) == noErr,
               let ref { refs.append(ref) }
            else { unavailable.insert(action.rawValue) }
        }
        CaptureModel.shared.unavailableShortcuts = unavailable
    }

    deinit {
        refs.forEach { UnregisterEventHotKey($0) }
        if let handler { RemoveEventHandler(handler) }
    }
}
