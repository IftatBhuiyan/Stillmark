import Carbon

/// Carbon hotkeys need no Accessibility permission or continuous keyboard event monitoring.
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
        for (offset, code) in [kVK_ANSI_2, kVK_ANSI_3, kVK_ANSI_4, kVK_ANSI_5].enumerated() {
            var ref: EventHotKeyRef?
            let id = EventHotKeyID(signature: 0x53544D4B, id: UInt32(offset + 1))
            if RegisterEventHotKey(UInt32(code), UInt32(cmdKey | shiftKey), id, GetApplicationEventTarget(), 0, &ref) == noErr,
               let ref { refs.append(ref) }
        }
    }

    deinit {
        refs.forEach { UnregisterEventHotKey($0) }
        if let handler { RemoveEventHandler(handler) }
    }
}
