import AppKit
import SwiftUI

/// Keeps Stop accessible while the editor is hidden during recording.
@MainActor
final class RecordingControls {
    private var panel: NSPanel?

    func show(model: CaptureModel, startedAt: Date) {
        hide()
        guard let screen = NSScreen.screens.first(where: {
            ($0.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value == model.displayID
        }) ?? NSScreen.main else { return }

        let size = NSSize(width: 300, height: 64)
        let area = screen.visibleFrame
        let panel = NSPanel(
            contentRect: CGRect(x: area.midX - size.width / 2, y: area.minY + 24,
                                width: size.width, height: size.height),
            styleMask: [.borderless, .nonactivatingPanel, .fullSizeContentView],
            backing: .buffered, defer: false
        )
        panel.title = "Stillmark Recording Controls"
        panel.isReleasedWhenClosed = false
        panel.level = .floating
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        panel.isMovableByWindowBackground = true
        panel.hidesOnDeactivate = false
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.contentView = NSHostingView(rootView: RecordingControlsView(model: model, startedAt: startedAt))
        self.panel = panel
        panel.orderFrontRegardless()
    }

    func hide() {
        panel?.orderOut(nil)
        panel?.contentView = nil
        panel = nil
    }
}

private struct RecordingControlsView: View {
    @ObservedObject var model: CaptureModel
    let startedAt: Date
    @Environment(\.accessibilityReduceTransparency) private var reduceTransparency

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: "record.circle.fill")
                .font(.system(size: 22, weight: .regular))
                .foregroundStyle(.red)
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 2) {
                Text(model.isBusy ? "Saving recording" : "Recording")
                    .font(.system(size: 11, weight: .medium))
                    .foregroundStyle(.secondary)
                RecordingElapsedTime(startedAt: startedAt)
            }
            Spacer(minLength: 4)
            Button {
                model.toggleRecording()
            } label: {
                HStack(spacing: 6) {
                    Image(systemName: "stop.fill").font(.system(size: 10, weight: .bold))
                    Text(model.isBusy ? "Finishing…" : "Stop")
                        .font(.system(size: 12, weight: .semibold))
                }
                .frame(minWidth: 80, minHeight: 28)
            }
            .buttonStyle(.borderedProminent)
            .tint(.red)
            .disabled(model.isBusy || !model.isRecording)
            .accessibilityLabel(model.isBusy ? "Finishing recording" : "Stop recording")
            .help("Finish and save this recording")
        }
        .padding(.horizontal, 16)
        .frame(width: 300, height: 64)
        .background {
            if reduceTransparency {
                RoundedRectangle(cornerRadius: 14).fill(Color(nsColor: .windowBackgroundColor))
            } else {
                RoundedRectangle(cornerRadius: 14).fill(.regularMaterial)
            }
        }
        .overlay(RoundedRectangle(cornerRadius: 14).strokeBorder(.primary.opacity(0.1)))
    }
}

/// Only this subtree refreshes once per second; elapsed time is not model state.
private struct RecordingElapsedTime: View {
    let startedAt: Date

    var body: some View {
        TimelineView(.periodic(from: startedAt, by: 1)) { timeline in
            let elapsed = max(0, Int(timeline.date.timeIntervalSince(startedAt)))
            Text(Self.formatted(elapsed))
                .font(.system(size: 15, weight: .semibold, design: .monospaced))
                .monospacedDigit()
                .accessibilityLabel("Recording duration")
                .accessibilityValue(Self.formatted(elapsed))
        }
    }

    private static func formatted(_ seconds: Int) -> String {
        let hours = seconds / 3600
        let minutes = (seconds / 60) % 60
        let remainder = seconds % 60
        return hours > 0
            ? String(format: "%02d:%02d:%02d", hours, minutes, remainder)
            : String(format: "%02d:%02d", minutes, remainder)
    }
}
