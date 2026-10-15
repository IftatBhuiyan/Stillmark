import AppKit
import SwiftUI

/// A dedicated image-first editor, separate from history and capture navigation.
struct EditorWorkspaceView: View {
    @ObservedObject var model: CaptureModel
    let image: NSImage
    @State private var showBackground = false
    @State private var zoom: Double? = nil
    @State private var showColors = false
    @Environment(\.displayScale) private var displayScale

    private var sourceSize: CGSize {
        guard let cg = image.cgImage(forProposedRect: nil, context: nil, hints: nil) else { return image.size }
        return CGSize(width: cg.width, height: cg.height)
    }

    var body: some View {
        VStack(spacing: 0) {
            GeometryReader { viewport in
                let pad = model.canvasBackground == .none ? 0 : model.canvasPadding * 2
                let requested = CGSize(width: (sourceSize.width + pad) * (zoom ?? 1) / displayScale,
                                       height: (sourceSize.height + pad) * (zoom ?? 1) / displayScale)
                ScrollView([.horizontal, .vertical]) {
                    ImageEditor(model: model, image: image, zoom: zoom)
                        .frame(width: zoom == nil ? viewport.size.width : max(viewport.size.width, requested.width),
                               height: zoom == nil ? viewport.size.height : max(viewport.size.height, requested.height))
                }
            }
            .background(Color(nsColor: .underPageBackgroundColor))
            Divider()
            bottomBar
        }
        .tint(.orange)
        .toolbarRole(.editor)
        .toolbar {
            ToolbarItem(placement: .navigation) { captureTools }
            ToolbarItem(placement: .principal) { annotationTools }
            ToolbarItemGroup(placement: .primaryAction) {
                styleTools
                Button("Save as…") { model.exportImage() }
                Button("Done") { model.saveAndCloseEditor() }.buttonStyle(.borderedProminent)
            }
        }
        .onChange(of: model.selection) { zoom = nil }
    }

    private var captureTools: some View {
        HStack(spacing: 10) {
            Button { choose(.crop) } label: { Image(systemName: "crop").frame(width: 26, height: 26) }.help("Crop")
            Button { model.importImage() } label: { Image(systemName: "photo.badge.plus").frame(width: 26, height: 26) }.help("Open another image")
            Button { showBackground.toggle() } label: { Image(systemName: "photo.on.rectangle").frame(width: 26, height: 26) }
                .help("Background")
                .popover(isPresented: $showBackground, arrowEdge: .bottom) { backgroundControls }
        }.buttonStyle(.plain)
    }

    private var annotationTools: some View {
        HStack(spacing: 2) {
            ForEach(AnnotationTool.allCases.filter { $0 != .crop }) { tool in
                Button { choose(tool) } label: {
                    Image(systemName: tool.symbol).font(.system(size: 15, weight: .medium))
                        .foregroundStyle(model.tool == tool ? Color.primary : Color.secondary)
                        .frame(width: 25, height: 28)
                        .background(model.tool == tool ? Color.primary.opacity(0.13) : .clear, in: Capsule())
                }.help(tool.rawValue).accessibilityLabel(tool.rawValue)
            }
        }
        .buttonStyle(.plain).padding(3)
        .background(.quaternary.opacity(0.45), in: Capsule())
    }

    private var styleTools: some View {
        HStack(spacing: 10) {
            Button { showColors.toggle() } label: {
                HStack(spacing: 4) {
                    Circle().fill(model.ink).frame(width: 18, height: 18)
                        .overlay(Circle().strokeBorder(.secondary.opacity(0.4)))
                    Image(systemName: "chevron.down").font(.system(size: 9, weight: .semibold))
                }
            }
            .help("Color").accessibilityLabel("Annotation color")
            .popover(isPresented: $showColors, arrowEdge: .bottom) { colorControls }
            Menu {
                Picker("Thickness", selection: $model.strokeWidth) {
                    ForEach([2.0, 4, 6, 8, 12], id: \.self) { width in Text("\(Int(width)) px").tag(width) }
                }
            } label: { Image(systemName: "line.diagonal").font(.system(size: 17, weight: .bold)) }.help("Thickness")
            if model.tool == .text {
                TextField("Text", text: $model.text).textFieldStyle(.roundedBorder).frame(width: 100)
                Menu {
                    Picker("Text size", selection: $model.textSize) {
                        ForEach([12.0, 16, 24, 32, 48, 64], id: \.self) { size in Text("\(Int(size)) px").tag(size) }
                    }
                } label: { Image(systemName: "textformat.size") }.help("Text size")
            }
        }.buttonStyle(.plain)
    }

    private var colorControls: some View {
        let colors: [(String, Color)] = [("Orange", .orange), ("Red", .red), ("Yellow", .yellow), ("Green", .green),
                                         ("Cyan", .cyan), ("Blue", .blue), ("Purple", .purple), ("Pink", .pink),
                                         ("White", .white), ("Black", .black)]
        return VStack(spacing: 14) {
            LazyVGrid(columns: Array(repeating: GridItem(.fixed(28)), count: 5), spacing: 10) {
                ForEach(colors, id: \.0) { name, color in
                    Button { model.ink = color; showColors = false } label: {
                        Circle().fill(color).frame(width: 25, height: 25)
                            .overlay(Circle().strokeBorder(.secondary.opacity(0.3)))
                    }.buttonStyle(.plain).help(name).accessibilityLabel(name)
                }
            }
            ColorPicker("Custom color", selection: $model.ink, supportsOpacity: false)
        }.padding(16).frame(width: 210)
    }

    private var bottomBar: some View {
        HStack(spacing: 14) {
            Menu {
                Button("Fit to Window") { zoom = nil }
                ForEach([0.25, 0.5, 0.75, 1, 1.5, 2], id: \.self) { value in
                    Button("\(Int(value * 100))%") { zoom = value }
                }
            } label: { Text(zoom.map { "\(Int($0 * 100))%" } ?? "Fit").monospacedDigit() }.fixedSize()
            Button { model.undo() } label: { Image(systemName: "arrow.uturn.backward") }.disabled(!model.canUndo).help("Undo")
            Button { model.redo() } label: { Image(systemName: "arrow.uturn.forward") }.disabled(!model.canRedo).help("Redo")
            Spacer()
            Label("Drag Me", systemImage: "line.3.horizontal")
                .padding(.horizontal, 18).padding(.vertical, 8)
                .background(.quaternary.opacity(0.3), in: Capsule())
                .overlay(Capsule().strokeBorder(.primary.opacity(0.08)))
                .onDrag { model.temporaryExport().flatMap { NSItemProvider(contentsOf: $0) } ?? NSItemProvider() }
            Spacer()
            Button { model.selection = nil } label: { Image(systemName: "clock.arrow.circlepath") }.help("Capture history")
            ShareImageButton(model: model).frame(width: 28, height: 28)
            Button { model.pinImage() } label: { Image(systemName: "pin.fill") }.help("Pin to screen")
            Button { model.copyImage() } label: { Image(systemName: "square.on.square.fill") }.help("Copy image (⇧⌘C)")
            Menu {
                Button("Copy Text from Image") { model.recognizeText() }
                Button("Delete Selected Annotation") { model.deleteSelectedMark() }.disabled(model.selectedMarkID == nil)
                Button("Source & Support") { NSWorkspace.shared.open(CaptureModel.repository) }
            } label: { Image(systemName: "ellipsis.circle") }.fixedSize()
        }
        .buttonStyle(.borderless).foregroundStyle(.secondary)
        .padding(.horizontal, 20).padding(.vertical, 10).background(.bar)
    }

    private var backgroundControls: some View {
        Form {
            Section("Background") {
                Picker("Style", selection: $model.canvasBackground) {
                    ForEach(CanvasBackground.allCases) { style in Text(style.rawValue).tag(style) }
                }
                LabeledContent("Padding") { Slider(value: $model.canvasPadding, in: 0...240, step: 8) }
                LabeledContent("Corners") { Slider(value: $model.canvasRadius, in: 0...64, step: 2) }
                Text("Padding and corner radius are measured in exported pixels. Your original capture stays unchanged.")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }.formStyle(.grouped).padding(8).frame(width: 330, height: 250)
    }

    private func choose(_ tool: AnnotationTool) { model.tool = tool; model.selectedMarkID = nil }
}

private struct ShareImageButton: NSViewRepresentable {
    let model: CaptureModel
    func makeCoordinator() -> Coordinator { Coordinator(model: model) }
    func makeNSView(context: Context) -> NSButton {
        let button = NSButton(image: NSImage(systemSymbolName: "square.and.arrow.up", accessibilityDescription: "Share image")!,
                              target: context.coordinator, action: #selector(Coordinator.share(_:)))
        button.isBordered = false; button.bezelStyle = .accessoryBarAction
        return button
    }
    func updateNSView(_ view: NSButton, context: Context) {}
    @MainActor final class Coordinator: NSObject {
        let model: CaptureModel
        private var picker: NSSharingServicePicker?
        init(model: CaptureModel) { self.model = model }
        @objc func share(_ sender: NSButton) {
            guard let image = model.renderedImage() else { return }
            picker = NSSharingServicePicker(items: [image])
            picker?.show(relativeTo: sender.bounds, of: sender, preferredEdge: .minY)
        }
    }
}
