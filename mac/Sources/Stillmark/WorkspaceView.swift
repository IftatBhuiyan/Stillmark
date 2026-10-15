import SwiftUI
import AppKit
import AVKit
import ImageIO

struct WorkspaceView: View {
    @ObservedObject var model: CaptureModel
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        Group {
            if let image = model.selectedImage {
                EditorWorkspaceView(model: model, image: image)
            } else {
                library
            }
        }
        .onAppear { model.requestMainWindow = { openWindow(id: "main") } }
        .alert("Stillmark", isPresented: Binding(get: { model.message != nil }, set: { if !$0 { model.message = nil } })) {
            Button("OK") { model.message = nil }
        } message: { Text(model.message ?? "") }
    }

    private var library: some View {
        NavigationSplitView {
            VStack(alignment: .leading, spacing: 16) {
                VStack(alignment: .leading, spacing: 4) {
                    Text("STILLMARK").font(.system(size: 12, weight: .bold, design: .monospaced)).tracking(2)
                    Text("Your screen. Your files.").font(.callout).foregroundStyle(.secondary)
                }.padding(.horizontal, 16).padding(.top, 20)
                HStack {
                    Text("CAPTURES").font(.system(size: 10, weight: .semibold)).tracking(1.5).foregroundStyle(.secondary)
                    Spacer()
                    Button { model.refreshHistory() } label: { Image(systemName: "arrow.clockwise") }
                        .buttonStyle(.borderless).help("Refresh history")
                }.padding(.horizontal, 16)
                if model.captures.isEmpty {
                    ContentUnavailableView("A clean slate", systemImage: "photo.on.rectangle.angled",
                                           description: Text("Capture an area or import an image to get started."))
                        .frame(maxHeight: .infinity)
                } else {
                    List(selection: $model.selection) {
                        ForEach(model.captures) { item in
                            HStack(spacing: 10) {
                                CaptureThumbnail(item: item).frame(width: 62, height: 44)
                                    .background(.quaternary.opacity(0.3), in: RoundedRectangle(cornerRadius: 5))
                                VStack(alignment: .leading, spacing: 4) {
                                    Text(item.isVideo ? "Screen Recording" : "Screenshot").font(.system(size: 12, weight: .medium))
                                    Text(item.date, style: .date).font(.caption).foregroundStyle(.secondary)
                                }
                            }.padding(.vertical, 5).tag(item.url)
                                .contextMenu {
                                    Button("Show in Finder") { NSWorkspace.shared.activateFileViewerSelecting([item.url]) }
                                    Button("Open") { NSWorkspace.shared.open(item.url) }
                                }
                        }
                    }.listStyle(.sidebar)
                }
                Button { NSWorkspace.shared.open(model.captureDirectory) } label: {
                    Label("Capture Folder", systemImage: "folder")
                }.buttonStyle(.borderless).padding(16)
            }
            .frame(maxHeight: .infinity, alignment: .top)
            .navigationSplitViewColumnWidth(min: 220, ideal: 250, max: 320)
        } detail: {
            VStack(spacing: 0) {
                captureBar
                Divider()
                if let image = model.selectedImage {
                    annotationBar
                    Divider()
                    ImageEditor(model: model, image: image)
                        .padding(28).background(Color(nsColor: .underPageBackgroundColor))
                } else if let url = model.selection, url.pathExtension == "mp4" {
                    RecordingPreview(url: url).id(url)
                } else {
                    VStack(alignment: .leading, spacing: 22) {
                        Image(systemName: "viewfinder").font(.system(size: 46, weight: .ultraLight)).foregroundStyle(.orange)
                        Text("Make your point.").font(.system(size: 36, weight: .semibold, design: .rounded))
                        Text("Capture, annotate, and share.\nNo account. No subscription. No upload.")
                            .font(.system(size: 17)).foregroundStyle(.secondary).lineSpacing(5)
                        Button(ShortcutAction.area.menuTitle) { model.capture(.area) }
                            .buttonStyle(.borderedProminent).tint(.orange).controlSize(.large)
                        Text("Mac preview · Windows remains the more complete edition.")
                            .font(.caption).foregroundStyle(.secondary)
                    }.frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .center)
                }
                Divider()
                HStack(spacing: 8) {
                    if model.isBusy { ProgressView().controlSize(.small) }
                    if model.isRecording { Image(systemName: "record.circle.fill").foregroundStyle(.red) }
                    Text(model.status).font(.caption).foregroundStyle(.secondary).lineLimit(2)
                    Spacer()
                    if let image = model.selectedImage,
                       let cg = image.cgImage(forProposedRect: nil, context: nil, hints: nil) {
                        Text("\(cg.width) × \(cg.height)").font(.system(.caption, design: .monospaced)).foregroundStyle(.secondary)
                    }
                }.padding(.horizontal, 20).padding(.vertical, 10)
            }
        }
        .tint(.orange)
    }

    private var captureBar: some View {
        HStack(spacing: 8) {
            Button { model.capture(.area) } label: { Label("Area", systemImage: "viewfinder") }
            Button { model.capture(.window) } label: { Label("Window", systemImage: "macwindow") }
            Button { model.capture(.screen) } label: { Label("Screen", systemImage: "display") }
            Divider().frame(height: 18)
            Button { model.toggleRecording() } label: {
                Label(model.isRecording ? "Stop" : "Record", systemImage: model.isRecording ? "stop.circle.fill" : "record.circle")
                    .foregroundStyle(model.isRecording ? Color.red : Color.primary)
            }
            Spacer()
            Button { model.importImage() } label: { Image(systemName: "square.and.arrow.down") }.help("Import image")
            SettingsLink { Image(systemName: "slider.horizontal.3") }.help("Capture and recording settings")
        }.buttonStyle(.bordered).controlSize(.regular).padding(16).disabled(model.isBusy)
    }

    private var annotationBar: some View {
        HStack(spacing: 8) {
            ForEach(AnnotationTool.allCases.filter { $0 != .select }) { tool in
                Button { model.tool = tool } label: {
                    Image(systemName: tool.symbol).frame(width: 22, height: 22)
                        .foregroundStyle(model.tool == tool ? Color.orange : Color.primary)
                }.help(tool.rawValue).accessibilityLabel(tool.rawValue)
            }
            ColorPicker("Ink", selection: $model.ink, supportsOpacity: false).labelsHidden().frame(width: 24)
            if model.tool == .text {
                TextField("Annotation text", text: $model.text).frame(minWidth: 80, maxWidth: 180)
                    .textFieldStyle(.roundedBorder)
            }
            Spacer(minLength: 8)
            Button { model.undo() } label: { Image(systemName: "arrow.uturn.backward") }.disabled(!model.canUndo).help("Undo")
            Menu {
                Button("Copy Image") { model.copyImage() }
                Button("Copy Text from Image") { model.recognizeText() }
                Button("Pin Image") { model.pinImage() }
                Button("Export PNG…") { model.exportImage() }
            } label: { Image(systemName: "square.and.arrow.up") }.menuStyle(.borderlessButton).fixedSize()
            Button("Copy") { model.copyImage() }
            Button("Export…") { model.exportImage() }.buttonStyle(.borderedProminent)
        }.buttonStyle(.borderless).padding(.horizontal, 18).padding(.vertical, 10)
    }
}

struct ImageEditor: View {
    @ObservedObject var model: CaptureModel
    let image: NSImage
    var zoom: Double? = nil
    @Environment(\.displayScale) private var displayScale
    @State private var dragStart: CGPoint?
    @State private var dragEnd: CGPoint?
    @State private var dragPoints: [CGPoint] = []
    @State private var movingMark: Annotation?
    @FocusState private var canvasFocused: Bool

    private var source: CGImage? { image.cgImage(forProposedRect: nil, context: nil, hints: nil) }

    var body: some View {
        GeometryReader { geometry in
            let pixels = CGSize(width: source?.width ?? Int(image.size.width), height: source?.height ?? Int(image.size.height))
            let inset = model.canvasBackground == .none ? 0 : model.canvasPadding
            let total = CGSize(width: pixels.width + inset * 2, height: pixels.height + inset * 2)
            let fit = max(0.01, min(1 / displayScale, (geometry.size.width - 40) / total.width, (geometry.size.height - 40) / total.height))
            let scale = zoom.map { $0 / displayScale } ?? fit
            let width = max(1, pixels.width * scale), height = max(1, pixels.height * scale)
            ZStack {
                Image(nsImage: image).resizable().interpolation(.high)
                Canvas { context, size in
                    for mark in model.marks where mark.tool != .redact {
                        var preview = mark
                        if movingMark?.id == mark.id, let start = dragStart, let end = dragEnd {
                            let dx = end.x - start.x, dy = end.y - start.y
                            preview.start.x += dx; preview.start.y += dy; preview.end.x += dx; preview.end.y += dy
                            preview.points = preview.points.map { CGPoint(x: $0.x + dx, y: $0.y + dy) }
                        }
                        render(preview, context: &context, size: size, sourcePixels: pixels)
                        if model.tool == .select && model.selectedMarkID == mark.id {
                            let bounds = model.bounds(for: preview)
                            let rect = CGRect(x: bounds.minX * size.width, y: bounds.minY * size.height,
                                              width: max(12, bounds.width * size.width), height: max(12, bounds.height * size.height))
                            context.stroke(Path(rect.insetBy(dx: -4, dy: -4)), with: .color(.orange), style: StrokeStyle(lineWidth: 1, dash: [4, 3]))
                        }
                    }
                    if model.tool != .select, let dragStart, let dragEnd {
                        let label = model.tool == .counter ? String(model.marks.filter { $0.tool == .counter }.count + 1) : model.text
                        render(Annotation(tool: model.tool, start: dragStart, end: dragEnd, text: label,
                                          color: NSColor(model.ink), points: dragPoints, width: model.strokeWidth, fontSize: model.textSize),
                               context: &context, size: size, sourcePixels: pixels)
                    }
                    for mark in model.marks where mark.tool == .redact {
                        var preview = mark
                        if movingMark?.id == mark.id, let start = dragStart, let end = dragEnd {
                            preview.start.x += end.x - start.x; preview.start.y += end.y - start.y
                            preview.end.x += end.x - start.x; preview.end.y += end.y - start.y
                        }
                        render(preview, context: &context, size: size, sourcePixels: pixels)
                    }
                }
                .contentShape(Rectangle())
                .focusable().focused($canvasFocused)
                .onKeyPress(.delete) { model.deleteSelectedMark(); return .handled }
                .onKeyPress("v") { model.tool = .select; return .handled }
                .onKeyPress("a") { model.tool = .arrow; return .handled }
                .onKeyPress("r") { model.tool = .rectangle; return .handled }
                .onKeyPress("t") { model.tool = .text; return .handled }
                .onKeyPress("k") { model.tool = .crop; return .handled }
                .gesture(DragGesture(minimumDistance: 0)
                    .onChanged { value in
                        canvasFocused = true
                        if dragStart == nil {
                            let start = normalized(value.startLocation, width: width, height: height)
                            dragStart = start
                            if model.tool == .select {
                                movingMark = model.mark(at: start); model.selectedMarkID = movingMark?.id
                            }
                        }
                        let current = normalized(value.location, width: width, height: height)
                        dragEnd = current
                        if model.tool == .pencil && dragPoints.count < 5000 {
                            if let last = dragPoints.last {
                                if hypot(current.x - last.x, current.y - last.y) > 0.001 { dragPoints.append(current) }
                            } else { dragPoints.append(current) }
                        }
                    }
                    .onEnded { value in
                        let start = normalized(value.startLocation, width: width, height: height)
                        let end = normalized(value.location, width: width, height: height)
                        if model.tool == .select, let movingMark {
                            model.moveMark(id: movingMark.id, offset: CGPoint(x: end.x - start.x, y: end.y - start.y))
                        } else { model.addMark(from: start, to: end, points: dragPoints) }
                        dragStart = nil; dragEnd = nil; dragPoints = []; movingMark = nil
                    })
            }
            .frame(width: width, height: height)
            .clipShape(RoundedRectangle(cornerRadius: model.canvasBackground == .none ? 0 : model.canvasRadius * scale))
            .shadow(color: model.canvasBackground == .none ? .clear : .black.opacity(0.2), radius: 10 * scale, y: 4 * scale)
            .padding(inset * scale)
            .background { CanvasBackdrop(style: model.canvasBackground) }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
    }

    private func normalized(_ point: CGPoint, width: Double, height: Double) -> CGPoint {
        CGPoint(x: min(1, max(0, point.x / max(1, width))), y: min(1, max(0, point.y / max(1, height))))
    }

    private func render(_ mark: Annotation, context: inout GraphicsContext, size: CGSize, sourcePixels: CGSize) {
        let a = CGPoint(x: mark.start.x * size.width, y: mark.start.y * size.height)
        let b = CGPoint(x: mark.end.x * size.width, y: mark.end.y * size.height)
        let rect = CGRect(x: mark.rect.minX * size.width, y: mark.rect.minY * size.height,
                          width: mark.rect.width * size.width, height: mark.rect.height * size.height)
        let color = Color(nsColor: mark.color), scale = size.width / max(1, sourcePixels.width)
        let line = max(0.5, mark.width * scale)
        switch mark.tool {
        case .rectangle: context.stroke(Path(rect), with: .color(color), lineWidth: line)
        case .filledRectangle: context.fill(Path(rect), with: .color(color))
        case .ellipse: context.stroke(Path(ellipseIn: rect), with: .color(color), lineWidth: line)
        case .line:
            var path = Path(); path.move(to: a); path.addLine(to: b)
            context.stroke(path, with: .color(color), style: StrokeStyle(lineWidth: line, lineCap: .round))
        case .pencil:
            var path = Path(); path.move(to: a)
            for point in mark.points { path.addLine(to: CGPoint(x: point.x * size.width, y: point.y * size.height)) }
            path.addLine(to: b)
            context.stroke(path, with: .color(color), style: StrokeStyle(lineWidth: line, lineCap: .round, lineJoin: .round))
        case .crop: context.stroke(Path(rect), with: .color(.white), style: StrokeStyle(lineWidth: 2, dash: [6, 4]))
        case .highlight: context.fill(Path(rect), with: .color(color.opacity(0.3)))
        case .redact: context.fill(Path(rect), with: .color(.black))
        case .pixelate:
            if let source {
                let crop = CGRect(x: mark.rect.minX * sourcePixels.width, y: mark.rect.minY * sourcePixels.height,
                                  width: mark.rect.width * sourcePixels.width, height: mark.rect.height * sourcePixels.height).integral
                if let pixels = AnnotatedImageRenderer.pixelatedRegion(source: source, rect: crop, blockSize: max(10, mark.width * 5)) {
                    context.draw(Image(decorative: pixels, scale: 1).interpolation(.none), in: rect)
                }
            }
        case .spotlight:
            var path = Path(CGRect(origin: .zero, size: size)); path.addRect(rect)
            context.fill(path, with: .color(.black.opacity(0.6)), style: FillStyle(eoFill: true))
        case .arrow:
            var path = Path(); path.move(to: a); path.addLine(to: b)
            let angle = atan2(b.y - a.y, b.x - a.x), head = line * 5
            path.move(to: CGPoint(x: b.x - head * cos(angle - .pi / 6), y: b.y - head * sin(angle - .pi / 6)))
            path.addLine(to: b)
            path.addLine(to: CGPoint(x: b.x - head * cos(angle + .pi / 6), y: b.y - head * sin(angle + .pi / 6)))
            context.stroke(path, with: .color(color), style: StrokeStyle(lineWidth: line, lineCap: .round, lineJoin: .round))
        case .text:
            context.draw(Text(mark.text).font(.system(size: mark.fontSize * scale, weight: .bold)).foregroundColor(color), at: a, anchor: .topLeading)
        case .counter:
            let diameter = max(28, mark.width * 9) * scale
            context.fill(Path(ellipseIn: CGRect(x: a.x - diameter / 2, y: a.y - diameter / 2, width: diameter, height: diameter)), with: .color(color))
            context.draw(Text(mark.text).font(.system(size: diameter * 0.58, weight: .bold)).foregroundColor(.white), at: a)
        default: break
        }
    }
}

struct CanvasBackdrop: View {
    var style: CanvasBackground
    var body: some View {
        if style == .gradient {
            LinearGradient(colors: [Color(nsColor: style.color), Color(red: 0.48, green: 0.34, blue: 0.64)], startPoint: .bottomLeading, endPoint: .topTrailing)
        } else { Color(nsColor: style.color) }
    }
}

private struct CaptureThumbnail: View {
    let item: CaptureItem
    @State private var image: NSImage?
    var body: some View {
        Group {
            if let image { Image(nsImage: image).resizable().scaledToFit() }
            else { Image(systemName: item.isVideo ? "video" : "photo").foregroundStyle(.secondary) }
        }.task(id: item.url) {
            guard !item.isVideo else { return }
            let url = item.url
            image = await Task.detached(priority: .utility) {
                guard let source = CGImageSourceCreateWithURL(url as CFURL, nil),
                      let thumb = CGImageSourceCreateThumbnailAtIndex(source, 0, [kCGImageSourceCreateThumbnailFromImageAlways: true,
                                                                               kCGImageSourceThumbnailMaxPixelSize: 160,
                                                                               kCGImageSourceCreateThumbnailWithTransform: true] as CFDictionary) else { return nil as NSImage? }
                return NSImage(cgImage: thumb, size: NSSize(width: thumb.width, height: thumb.height))
            }.value
        }
    }
}

private struct RecordingPreview: View {
    let url: URL
    var body: some View {
        NativeRecordingPlayer(url: url).padding(24)
    }
}

// Keep AVKit's native player lifecycle explicit. The SwiftUI VideoPlayer bridge
// can abort while initializing its superclass metadata on macOS 27.
private struct NativeRecordingPlayer: NSViewRepresentable {
    let url: URL

    final class Coordinator { var url: URL? }
    func makeCoordinator() -> Coordinator { Coordinator() }

    func makeNSView(context: Context) -> AVPlayerView {
        let view = AVPlayerView()
        view.controlsStyle = .inline
        view.videoGravity = .resizeAspect
        return view
    }

    func updateNSView(_ view: AVPlayerView, context: Context) {
        guard context.coordinator.url != url else { return }
        view.player?.pause()
        view.player = AVPlayer(url: url)
        context.coordinator.url = url
    }

    static func dismantleNSView(_ view: AVPlayerView, coordinator: Coordinator) {
        view.player?.pause()
        view.player = nil
    }
}
