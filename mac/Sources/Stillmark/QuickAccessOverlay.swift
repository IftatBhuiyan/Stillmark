import AppKit
import SwiftUI

@MainActor
final class QuickAccessOverlay {
    private var panel: NSPanel?
    private var dismissal: Task<Void, Never>?

    func show(url: URL, model: CaptureModel) {
        dismiss()
        guard let screen = NSScreen.screens.first(where: {
            ($0.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value == model.displayID
        }) ?? NSScreen.main else { return }
        let area = screen.visibleFrame
        let width = CGFloat(min(360, max(240, model.overlaySize)))
        let x = model.overlayPosition == "left" ? area.minX + 24 : area.maxX - width - 24
        let panel = NSPanel(contentRect: CGRect(x: x, y: area.minY + 24, width: width, height: 224),
                            styleMask: [.nonactivatingPanel, .fullSizeContentView], backing: .buffered, defer: false)
        panel.level = .floating
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        panel.isMovableByWindowBackground = true
        panel.hidesOnDeactivate = false
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.contentView = NSHostingView(rootView: QuickAccessView(url: url, model: model, close: { [weak self] in self?.dismiss() }))
        self.panel = panel
        panel.orderFrontRegardless()
        if model.overlayTimeout > 0 {
            dismissal = Task { [weak self] in
                try? await Task.sleep(for: .seconds(model.overlayTimeout))
                guard !Task.isCancelled else { return }
                self?.dismiss()
            }
        }
    }

    func dismiss() {
        dismissal?.cancel(); dismissal = nil
        panel?.orderOut(nil); panel = nil
    }
}

private struct QuickAccessView: View {
    let url: URL
    @ObservedObject var model: CaptureModel
    let close: () -> Void

    var body: some View {
        VStack(spacing: 10) {
            HStack {
                Image(systemName: url.pathExtension == "mp4" ? "video" : "checkmark.circle.fill").foregroundStyle(.orange)
                Text(url.pathExtension == "mp4" ? "Recording saved" : "Capture saved").font(.system(size: 12, weight: .medium))
                Spacer()
                Button(action: close) { Image(systemName: "xmark") }.buttonStyle(.plain).help("Dismiss overlay")
            }
            Group {
                if url.pathExtension == "mp4" {
                    Image(systemName: "play.rectangle").font(.system(size: 48, weight: .light)).foregroundStyle(.secondary)
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                } else if let image = NSImage(contentsOf: url) {
                    Image(nsImage: image).resizable().scaledToFit()
                }
            }
            .frame(maxWidth: .infinity).frame(height: 124)
            .contentShape(Rectangle())
            .onTapGesture { openEditor() }
            .onDrag { NSItemProvider(contentsOf: url) ?? NSItemProvider() }
            .help("Drag the capture into another app")
            HStack(spacing: 8) {
                Button(url.pathExtension == "mp4" ? "Open" : "Annotate") { openEditor() }
                    .buttonStyle(.borderedProminent).tint(.orange)
                if url.pathExtension != "mp4" {
                    Button("Copy") { model.selection = url; model.copyImage(); close() }
                    Button { model.selection = url; model.pinImage() } label: { Image(systemName: "pin") }.help("Pin")
                }
                Button { NSWorkspace.shared.activateFileViewerSelecting([url]) } label: { Image(systemName: "folder") }.help("Show in Finder")
            }.buttonStyle(.bordered).controlSize(.small)
        }
        .padding(14)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 14))
        .overlay(RoundedRectangle(cornerRadius: 14).strokeBorder(.primary.opacity(0.1)))
    }

    private func openEditor() {
        model.selection = url
        model.showEditor()
        close()
    }
}
