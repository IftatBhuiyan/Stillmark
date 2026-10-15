import SwiftUI
import AppKit
import AVKit
import ImageIO

struct WorkspaceView: View {
    @ObservedObject var model: CaptureModel

    var body: some View {
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
                        Button("Capture an Area   ⌘⇧2") { model.capture(.area) }
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
        .alert("Stillmark", isPresented: Binding(get: { model.message != nil }, set: { if !$0 { model.message = nil } })) {
            Button("OK") { model.message = nil }
        } message: { Text(model.message ?? "") }
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
            Button { model.undo() } label: { Image(systemName: "arrow.uturn.backward") }.disabled(model.marks.isEmpty).help("Undo")
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
    @State private var dragStart: CGPoint?
    @State private var dragEnd: CGPoint?

    var body: some View {
        GeometryReader { geometry in
            let ratio = image.size.width / max(1, image.size.height)
            let width = min(geometry.size.width, geometry.size.height * ratio)
            let height = width / ratio
            ZStack {
                Image(nsImage: image).resizable().interpolation(.high)
                Canvas { context, size in
                    for mark in model.marks { render(mark, context: &context, size: size) }
                    if let dragStart, let dragEnd {
                        render(Annotation(tool: model.tool, start: dragStart, end: dragEnd, text: model.text, color: NSColor(model.ink)), context: &context, size: size)
                    }
                }
                .contentShape(Rectangle())
                .gesture(DragGesture(minimumDistance: 0)
                    .onChanged { value in
                        dragStart = normalized(value.startLocation, width: width, height: height)
                        dragEnd = normalized(value.location, width: width, height: height)
                    }
                    .onEnded { value in
                        model.addMark(from: normalized(value.startLocation, width: width, height: height),
                                      to: normalized(value.location, width: width, height: height))
                        dragStart = nil; dragEnd = nil
                    })
            }
            .frame(width: width, height: height).clipped()
            .shadow(color: .black.opacity(0.14), radius: 12, y: 4)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
    }

    private func normalized(_ point: CGPoint, width: Double, height: Double) -> CGPoint {
        CGPoint(x: min(1, max(0, point.x / max(1, width))), y: min(1, max(0, point.y / max(1, height))))
    }

    private func render(_ mark: Annotation, context: inout GraphicsContext, size: CGSize) {
        let a = CGPoint(x: mark.start.x * size.width, y: mark.start.y * size.height)
        let b = CGPoint(x: mark.end.x * size.width, y: mark.end.y * size.height)
        let rect = CGRect(x: mark.rect.minX * size.width, y: mark.rect.minY * size.height,
                          width: mark.rect.width * size.width, height: mark.rect.height * size.height)
        let color = Color(nsColor: mark.color)
        let line = max(2, size.width / 450)
        switch mark.tool {
        case .rectangle: context.stroke(Path(rect), with: .color(color), lineWidth: line)
        case .crop: context.stroke(Path(rect), with: .color(.white), style: StrokeStyle(lineWidth: 2, dash: [6, 4]))
        case .highlight: context.fill(Path(rect), with: .color(color.opacity(0.3)))
        case .redact: context.fill(Path(rect), with: .color(.black))
        case .arrow:
            var path = Path(); path.move(to: a); path.addLine(to: b)
            let angle = atan2(b.y - a.y, b.x - a.x), head = line * 5
            path.move(to: CGPoint(x: b.x - head * cos(angle - .pi / 6), y: b.y - head * sin(angle - .pi / 6)))
            path.addLine(to: b)
            path.addLine(to: CGPoint(x: b.x - head * cos(angle + .pi / 6), y: b.y - head * sin(angle + .pi / 6)))
            context.stroke(path, with: .color(color), style: StrokeStyle(lineWidth: line, lineCap: .round, lineJoin: .round))
        case .text:
            context.draw(Text(mark.text).font(.system(size: max(12, size.width / 45), weight: .bold)).foregroundColor(color), at: a, anchor: .topLeading)
        default: break
        }
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
    @State private var player: AVPlayer?
    var body: some View {
        VideoPlayer(player: player).padding(24)
            .onAppear { player = AVPlayer(url: url) }
            .onDisappear { player?.pause(); player = nil }
    }
}

struct PreferencesView: View {
    @ObservedObject var model: CaptureModel
    var body: some View {
        Form {
            Section("Screen recording") {
                Picker("Display", selection: $model.displayID) {
                    ForEach(model.displays, id: \.displayID) { display in
                        Text("Display \(display.displayID) · \(display.width) × \(display.height)").tag(display.displayID)
                    }
                }
                Button("Load Displays & Check Permission") { Task { await model.refreshDisplays() } }
                Picker("Frame rate", selection: $model.recordFPS) {
                    Text("30 fps · smaller files").tag(30)
                    Text("60 fps · smoother motion").tag(60)
                }
                Toggle("Include system audio", isOn: $model.recordSystemAudio)
                Toggle("Include microphone", isOn: $model.recordMicrophone)
                Toggle("Show pointer in recordings", isOn: $model.showCursor)
                Text("Recordings use H.264 MP4, up to 4K. Audio is off until you enable it.").font(.caption).foregroundStyle(.secondary)
            }
            Section("Privacy & storage") {
                Picker("Quick overlay", selection: $model.overlayTimeout) {
                    Text("Close after 12 seconds").tag(12)
                    Text("Close after 30 seconds").tag(30)
                    Text("Keep until dismissed").tag(0)
                }
                Text("Captures stay in Pictures/Stillmark. Nothing is uploaded. Delete files in Finder when you no longer need them.")
                Button("Open Capture Folder") { NSWorkspace.shared.open(model.captureDirectory) }
                Button("Open Screen Recording Permissions") {
                    NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture")!)
                }
            }
            Section("Open source") {
                Text("Stillmark 1.1 · Mac preview · GPLv3")
                Link("Source, releases, and support", destination: CaptureModel.repository)
            }
        }.formStyle(.grouped).padding(16).frame(width: 500)
    }
}
