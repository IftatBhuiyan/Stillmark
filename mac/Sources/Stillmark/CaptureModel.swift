import AppKit
import SwiftUI
import ScreenCaptureKit
import Vision
import UniformTypeIdentifiers
import AVFoundation

enum CaptureKind { case area, screen, window }
enum AnnotationTool: String, CaseIterable, Identifiable, Codable {
    case select = "Move", rectangle = "Rectangle", filledRectangle = "Filled Rectangle", ellipse = "Ellipse"
    case line = "Line", arrow = "Arrow", text = "Text", pixelate = "Pixelate", highlight = "Highlight"
    case counter = "Counter", pencil = "Pencil", redact = "Redact", spotlight = "Spotlight", crop = "Crop"
    var id: Self { self }
    var symbol: String {
        switch self {
        case .select: "cursorarrow"
        case .rectangle: "rectangle"
        case .filledRectangle: "rectangle.fill"
        case .ellipse: "circle"
        case .line: "line.diagonal"
        case .arrow: "arrow.up.right"
        case .highlight: "highlighter"
        case .redact: "rectangle.fill"
        case .text: "textformat"
        case .crop: "crop"
        case .pixelate: "square.grid.3x3.fill"
        case .counter: "1.circle.fill"
        case .pencil: "pencil.tip"
        case .spotlight: "rectangle.center.inset.filled"
        }
    }
}

struct Annotation: Identifiable {
    let id = UUID()
    var tool: AnnotationTool
    var start: CGPoint
    var end: CGPoint
    var text = ""
    var color: NSColor
    var points: [CGPoint] = []
    var width: CGFloat = 4
    var fontSize: CGFloat = 32
    var rect: CGRect {
        if tool == .pencil, !points.isEmpty {
            let all = points + [start, end]
            let xs = all.map(\.x), ys = all.map(\.y)
            return CGRect(x: xs.min()!, y: ys.min()!, width: xs.max()! - xs.min()!, height: ys.max()! - ys.min()!)
        }
        return CGRect(x: min(start.x, end.x), y: min(start.y, end.y), width: abs(start.x - end.x), height: abs(start.y - end.y))
    }
}

struct CaptureItem: Identifiable, Hashable {
    var id: URL { url }
    let url: URL
    let date: Date
    var isVideo: Bool { url.pathExtension.lowercased() == "mp4" }
    var title: String { url.deletingPathExtension().lastPathComponent }
}

@MainActor
final class CaptureModel: NSObject, ObservableObject {
    static let shared = CaptureModel()
    static let repository = URL(string: "https://github.com/IftatBhuiyan/Stillmark")!
    @Published var captures: [CaptureItem] = []
    @Published var selection: URL? { didSet { loadSelection() } }
    @Published var selectedImage: NSImage?
    @Published var marks: [Annotation] = [] { didSet { if !loadingSelection { currentDirty = true } } }
    @Published private var undoStates: [[Annotation]] = []
    @Published private var redoStates: [[Annotation]] = []
    @Published var selectedMarkID: UUID?
    @Published var tool: AnnotationTool = .arrow
    @Published var ink: Color = .orange
    @Published var text = ""
    @Published var strokeWidth: Double = 4
    @Published var textSize: Double = 32
    @Published var canvasBackground: CanvasBackground = .none { didSet { if !loadingSelection { currentDirty = true } } }
    @Published var canvasPadding: Double = 80 { didSet { if !loadingSelection && canvasBackground != .none { currentDirty = true } } }
    @Published var canvasRadius: Double = 16 { didSet { if !loadingSelection && canvasBackground != .none { currentDirty = true } } }
    @Published var message: String?
    @Published var isBusy = false
    @Published var isRecording = false
    @Published var displays: [SCDisplay] = []
    @Published var displayID: CGDirectDisplayID = CGMainDisplayID()
    @Published var status = "Everything stays on your Mac."
    @Published var unavailableShortcuts: Set<Int> = []
    @AppStorage("recordSystemAudio") var recordSystemAudio = false
    @AppStorage("recordMicrophone") var recordMicrophone = false
    @AppStorage("recordFPS") var recordFPS = 30
    @AppStorage("showCursor") var showCursor = true
    @AppStorage("overlayTimeout") var overlayTimeout = 10
    @AppStorage("overlayPosition") var overlayPosition = "left"
    @AppStorage("overlaySize") var overlaySize = 288
    @AppStorage("showOverlayAfterCapture") var showOverlayAfterCapture = true
    @AppStorage("copyAfterCapture") var copyAfterCapture = true
    @AppStorage("openEditorAfterCapture") var openEditorAfterCapture = false
    @AppStorage("pinAfterCapture") var pinAfterCapture = false
    @AppStorage("screenshotFormat") var screenshotFormat = "png"
    @AppStorage("screenshotCursor") var screenshotCursor = false
    @AppStorage("windowShadow") var windowShadow = true
    @AppStorage("captureSound") var captureSound = false
    @AppStorage("timerInterval") var timerInterval = 5
    @AppStorage("recordMaxResolution") var recordMaxResolution = 3840
    @AppStorage("editorAlwaysOnTop") var editorAlwaysOnTop = false
    let captureDirectory: URL
    private struct DocumentEdits {
        var marks: [Annotation] = []
        var background: CanvasBackground = .none
        var padding: Double = 80
        var radius: Double = 16
        var isDirty = false
    }
    private var edits: [URL: DocumentEdits] = [:]
    private var loadedURL: URL?
    private var recorder: ScreenRecorder?
    private let recordingControls = RecordingControls()
    private var captureProcess: Process?
    private var pins: [NSPanel] = []
    private let overlay = QuickAccessOverlay()
    private var recentCapture: URL?
    var requestMainWindow: (() -> Void)?
    var shortcutConfigurationChanged: (() -> Void)?
    private var loadingSelection = false
    private var currentDirty = false
    private struct HiddenCaptureWindow {
        weak var window: NSWindow?
        let restoreOnSuccess: Bool
    }
    private var hiddenCaptureWindows: [HiddenCaptureWindow] = []
    private var captureStartedInForeground = false

    var hasUnsavedAnnotations: Bool {
        currentDirty || edits.contains { $0.key != selection && $0.value.isDirty }
    }
    var canUndo: Bool { !undoStates.isEmpty }
    var canRedo: Bool { !redoStates.isEmpty }

    override init() {
        let pictures = FileManager.default.urls(for: .picturesDirectory, in: .userDomainMask).first!
        captureDirectory = pictures.appendingPathComponent("Stillmark", isDirectory: true)
        super.init()
        do { try FileManager.default.createDirectory(at: captureDirectory, withIntermediateDirectories: true) }
        catch { message = "Cannot open capture folder: \(error.localizedDescription)" }
        refreshHistory()
    }

    func refreshHistory() {
        do {
            let urls = try FileManager.default.contentsOfDirectory(at: captureDirectory, includingPropertiesForKeys: [.creationDateKey], options: [.skipsHiddenFiles])
            captures = urls.filter { ["png", "jpg", "jpeg", "mp4"].contains($0.pathExtension.lowercased()) }.map {
                CaptureItem(url: $0, date: (try? $0.resourceValues(forKeys: [.creationDateKey]).creationDate) ?? .distantPast)
            }.sorted { $0.date > $1.date }
        } catch { message = "Cannot read capture history: \(error.localizedDescription)" }
    }

    private func loadSelection() {
        if let loadedURL { edits[loadedURL] = DocumentEdits(marks: marks, background: canvasBackground, padding: canvasPadding, radius: canvasRadius, isDirty: currentDirty) }
        loadingSelection = true
        defer { loadingSelection = false }
        loadedURL = selection
        let document = selection.flatMap { edits[$0] } ?? DocumentEdits()
        marks = document.marks; canvasBackground = document.background
        canvasPadding = document.padding; canvasRadius = document.radius
        currentDirty = document.isDirty
        undoStates = []; redoStates = []; selectedMarkID = nil
        selectedImage = selection.flatMap { $0.pathExtension == "mp4" ? nil : NSImage(contentsOf: $0) }
    }

    func newURL(extension ext: String, prefix: String = "Stillmark") -> URL {
        let date = Date().formatted(.iso8601.year().month().day().time(includingFractionalSeconds: false).timeSeparator(.omitted))
        return captureDirectory.appendingPathComponent("\(prefix)-\(date)-\(UUID().uuidString.prefix(6)).\(ext)")
    }

    func capture(_ kind: CaptureKind, timed: Bool = false) {
        guard !isBusy, !isRecording, NSApp.modalWindow == nil else { return }
        guard CGPreflightScreenCaptureAccess() || CGRequestScreenCaptureAccess() else {
            message = "Allow Stillmark in System Settings → Privacy & Security → Screen & System Audio Recording, then relaunch."
            return
        }
        overlay.dismiss()
        isBusy = true
        let format = screenshotFormat == "jpg" ? "jpg" : "png"
        let url = newURL(extension: format)
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
        var args = ["-t", format]
        if !captureSound { args.append("-x") }
        if timed { args += ["-T", String(max(1, min(timerInterval, 30)))] }
        if !windowShadow { args.append("-o") }
        switch kind {
        case .area: args += ["-i", "-s"]
        case .window: args += ["-i", "-w"]
        case .screen:
            if screenshotCursor { args.append("-C") }
            let displayIndex = (NSScreen.screens.firstIndex { ($0.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value == displayID } ?? 0) + 1
            args += ["-D", String(displayIndex)]
        }
        args.append(url.path)
        process.arguments = args
        process.standardError = Pipe()
        captureProcess = process
        hideUIForCapture()
        process.terminationHandler = { [weak self] process in
            Task { @MainActor in
                guard let self else { return }
                self.isBusy = false
                self.captureProcess = nil
                if FileManager.default.fileExists(atPath: url.path) {
                    self.restoreCaptureUI(succeeded: true)
                    self.refreshHistory()
                    self.selection = url
                    self.status = "Capture saved locally."
                    if self.copyAfterCapture { self.copyImage() }
                    if self.pinAfterCapture { self.pinImage() }
                    self.showOverlay(url)
                    if self.openEditorAfterCapture { self.showEditor() }
                } else if process.terminationStatus != 0 && kind == .screen {
                    self.restoreCaptureUI(succeeded: false)
                    self.message = "Capture failed. Allow Stillmark in System Settings → Privacy & Security → Screen & System Audio Recording, then relaunch."
                } else {
                    self.restoreCaptureUI(succeeded: false)
                }
            }
        }
        do { try process.run() }
        catch {
            captureProcess = nil; isBusy = false; restoreCaptureUI(succeeded: false)
            message = error.localizedDescription
        }
    }

    private func hideUIForCapture() {
        captureStartedInForeground = NSApp.isActive
        hiddenCaptureWindows = NSApp.windows.filter(\.isVisible).map {
            HiddenCaptureWindow(window: $0, restoreOnSuccess: $0.title != "Stillmark")
        }
        hiddenCaptureWindows.forEach { $0.window?.orderOut(nil) }
        // Yield focus without leaving the application hidden: new nonactivating overlays must remain visible.
        NSApp.hide(nil)
        NSApp.unhideWithoutActivation()
    }

    private func restoreCaptureUI(succeeded: Bool) {
        for record in hiddenCaptureWindows where !succeeded || record.restoreOnSuccess {
            record.window?.orderFront(nil)
        }
        hiddenCaptureWindows = []
        if !succeeded && captureStartedInForeground { showEditor() }
    }

    private func showOverlay(_ url: URL) {
        recentCapture = url
        if showOverlayAfterCapture { overlay.show(url: url, model: self) }
    }

    func restoreOverlay() {
        if let url = recentCapture ?? captures.first?.url { overlay.show(url: url, model: self) }
    }

    func showEditor() {
        requestMainWindow?()
        NSApp.unhide(nil)
        NSApp.activate(ignoringOtherApps: true)
        Task { @MainActor in
            await Task.yield()
            let window = NSApp.windows.first { $0.title == "Stillmark" }
            window?.level = editorAlwaysOnTop ? .floating : .normal
            window?.makeKeyAndOrderFront(nil)
        }
    }

    func importImage() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.png, .jpeg, .tiff, .heic, StillmarkProject.contentType]
        panel.allowsMultipleSelection = false
        guard panel.runModal() == .OK, let source = panel.url else { return }
        openFile(source)
    }

    func openFile(_ source: URL) {
        guard source.isFileURL else { message = "Open a local image or Stillmark project file."; return }
        do {
            if source.pathExtension.lowercased() == "stillmark" {
                let (project, image) = try StillmarkProject.read(from: source)
                let url = newURL(extension: "png", prefix: "Project")
                try project.sourcePNG.write(to: url, options: .atomic)
                refreshHistory(); selection = url; selectedImage = image
                marks = project.annotations; canvasBackground = project.background
                canvasPadding = project.padding; canvasRadius = project.radius
                currentDirty = false
                showEditor()
                return
            }
            guard let image = NSImage(contentsOf: source), let data = image.pngData else {
                message = "This file is not a supported image or Stillmark project."
                return
            }
            let url = newURL(extension: "png", prefix: "Imported")
            try data.write(to: url, options: .atomic)
            refreshHistory(); selection = url
            showEditor()
        } catch { message = error.localizedDescription }
    }

    func saveProject() {
        guard let data = selectedImage?.pngData else { return }
        guard data.count <= 64 * 1024 * 1024, marks.count <= 10_000,
              marks.reduce(0, { $0 + $1.points.count }) <= 250_000 else {
            message = "This project exceeds the supported editable-document size. Export a PNG instead."
            return
        }
        let panel = NSSavePanel(); panel.allowedContentTypes = [StillmarkProject.contentType]
        panel.nameFieldStringValue = "Capture.stillmark"
        panel.message = "Editable projects retain the original pixels. Share an exported PNG when redacting sensitive information."
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            let project = StillmarkProject(sourcePNG: data, annotations: marks, background: canvasBackground,
                                           padding: canvasPadding, radius: canvasRadius)
            let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys]
            let encoded = try encoder.encode(project)
            guard encoded.count <= 128 * 1024 * 1024 else { throw ProjectFileError.invalid }
            try encoded.write(to: url, options: .atomic)
            currentDirty = false
            status = "Editable project saved."
        } catch { message = error.localizedDescription }
    }

    func addMark(from start: CGPoint, to end: CGPoint, points: [CGPoint] = []) {
        guard tool != .select else { return }
        if tool == .text && text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            status = "Enter annotation text in the toolbar first."
            return
        }
        let label = tool == .counter ? String(marks.filter { $0.tool == .counter }.count + 1) : text
        let mark = Annotation(tool: tool, start: start, end: end, text: label, color: NSColor(ink), points: points, width: strokeWidth, fontSize: textSize)
        let validSize = tool == .pencil ? points.count > 1 : tool == .arrow || tool == .line
            ? hypot(end.x - start.x, end.y - start.y) > 0.005
            : mark.rect.width > 0.002 && mark.rect.height > 0.002
        guard tool == .text || tool == .counter || validSize else { return }
        rememberForUndo()
        marks.append(mark); selectedMarkID = mark.id
    }

    private func rememberForUndo() {
        undoStates.append(marks)
        if undoStates.count > 50 { undoStates.removeFirst() }
        redoStates = []
    }

    func undo() {
        guard let previous = undoStates.popLast() else { return }
        redoStates.append(marks); marks = previous; selectedMarkID = nil
    }

    func redo() {
        guard let next = redoStates.popLast() else { return }
        undoStates.append(marks); marks = next; selectedMarkID = nil
    }

    func mark(at point: CGPoint) -> Annotation? {
        marks.last { bounds(for: $0).insetBy(dx: -0.01, dy: -0.01).contains(point) }
    }

    func bounds(for mark: Annotation) -> CGRect {
        let cg = selectedImage?.cgImage(forProposedRect: nil, context: nil, hints: nil)
        return AnnotationGeometry.bounds(of: mark, sourceSize: CGSize(width: cg?.width ?? 1, height: cg?.height ?? 1))
    }

    func moveMark(id: UUID, offset: CGPoint) {
        guard let index = marks.firstIndex(where: { $0.id == id }) else { return }
        rememberForUndo()
        let rect = bounds(for: marks[index])
        let dx = min(1 - rect.maxX, max(-rect.minX, offset.x))
        let dy = min(1 - rect.maxY, max(-rect.minY, offset.y))
        marks[index].start.x += dx; marks[index].start.y += dy
        marks[index].end.x += dx; marks[index].end.y += dy
        marks[index].points = marks[index].points.map { CGPoint(x: $0.x + dx, y: $0.y + dy) }
    }

    func deleteSelectedMark() {
        guard let id = selectedMarkID, marks.contains(where: { $0.id == id }) else { return }
        rememberForUndo(); marks.removeAll { $0.id == id }; selectedMarkID = nil
    }

    func renderedImage() -> NSImage? {
        guard let image = selectedImage else { return nil }
        return AnnotatedImageRenderer.render(original: image, marks: marks, background: canvasBackground,
                                             padding: canvasPadding, radius: canvasRadius)
    }

    func copyImage() {
        guard let image = renderedImage() else { return }
        NSPasteboard.general.clearContents(); NSPasteboard.general.writeObjects([image])
        status = "Image copied."
    }

    func temporaryExport() -> URL? {
        guard let data = renderedImage()?.pngData else { return nil }
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("Stillmark-\(UUID().uuidString).png")
        do { try data.write(to: url, options: .atomic); return url }
        catch { message = error.localizedDescription; return nil }
    }

    func exportImage() {
        guard let image = renderedImage(), let data = image.pngData else { return }
        let panel = NSSavePanel(); panel.allowedContentTypes = [.png]
        panel.nameFieldStringValue = newURL(extension: "png", prefix: "Edited").lastPathComponent
        panel.directoryURL = captureDirectory
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            try data.write(to: url, options: .atomic)
            if let selection { edits[selection] = DocumentEdits() }
            marks = []; undoStates = []; redoStates = []
            canvasBackground = .none
            currentDirty = false
            refreshHistory(); selection = url; status = "Image exported."
        } catch { message = error.localizedDescription }
    }

    func saveAndCloseEditor() {
        guard let selected = selection else { return }
        do {
            var saved = selected
            if !marks.isEmpty || canvasBackground != .none {
                guard let data = renderedImage()?.pngData else {
                    message = "This image could not be rendered. Try a smaller image."
                    return
                }
                saved = newURL(extension: "png", prefix: "Edited")
                try data.write(to: saved, options: .atomic)
                edits[selected] = DocumentEdits()
                marks = []; undoStates = []; redoStates = []; canvasBackground = .none
                currentDirty = false
                refreshHistory(); selection = saved
            }
            NSApp.windows.first { $0.title == "Stillmark" }?.orderOut(nil)
            currentDirty = false
            showOverlay(saved)
        } catch { message = error.localizedDescription }
    }

    func recognizeText() {
        guard !isBusy, let image = renderedImage()?.cgImage(forProposedRect: nil, context: nil, hints: nil) else { return }
        isBusy = true
        Task {
            do {
                let text = try await Task.detached(priority: .userInitiated) {
                    let request = VNRecognizeTextRequest(); request.recognitionLevel = .accurate
                    try VNImageRequestHandler(cgImage: image).perform([request])
                    return (request.results ?? []).compactMap { $0.topCandidates(1).first?.string }.joined(separator: "\n")
                }.value
                NSPasteboard.general.clearContents(); NSPasteboard.general.setString(text, forType: .string)
                status = text.isEmpty ? "No text found." : "Recognized text copied to clipboard."
            } catch { message = error.localizedDescription }
            isBusy = false
        }
    }

    func pinImage() {
        guard let image = renderedImage() else { return }
        pins.removeAll { !$0.isVisible }
        let scale = min(1, 600 / max(image.size.width, image.size.height))
        let panel = NSPanel(contentRect: CGRect(x: 200, y: 200, width: image.size.width * scale, height: image.size.height * scale),
                            styleMask: [.titled, .closable, .resizable, .utilityWindow], backing: .buffered, defer: false)
        panel.title = "Pinned Capture"; panel.level = .floating; panel.isReleasedWhenClosed = false
        let view = NSImageView(); view.image = image; view.imageScaling = .scaleProportionallyUpOrDown
        panel.contentView = view; panel.makeKeyAndOrderFront(nil); pins.append(panel)
    }

    func refreshDisplays() async {
        do {
            displays = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true).displays
            if !displays.contains(where: { $0.displayID == displayID }), let first = displays.first { displayID = first.displayID }
        } catch { message = "Screen recording permission is needed: \(error.localizedDescription)" }
    }

    func toggleRecording() {
        guard !isBusy, NSApp.modalWindow == nil else { return }
        isBusy = true
        Task { [self] in
            if let recorder {
                await recorder.stop()
                // The recording delegate owns finalization; don't expose an incomplete MP4.
                return
            }
            do {
                let url = newURL(extension: "mp4")
                let recorder = ScreenRecorder { [weak self] result in
                    guard let self else { return }
                    self.recordingControls.hide()
                    self.recorder = nil; self.isRecording = false; self.isBusy = false
                    switch result {
                    case .success(let url):
                        self.restoreCaptureUI(succeeded: true)
                        self.refreshHistory(); self.selection = url; self.status = "Recording saved."
                        self.showOverlay(url)
                    case .failure(let error): self.restoreCaptureUI(succeeded: false); self.message = error.localizedDescription
                    }
                }
                self.recorder = recorder
                try await recorder.start(displayID: displayID, url: url, fps: recordFPS,
                                         systemAudio: recordSystemAudio, microphone: recordMicrophone, showCursor: showCursor,
                                         maxResolution: recordMaxResolution)
                isRecording = true; isBusy = false; status = "Recording. Use the floating Stop control or the menu bar to finish."
                hideUIForCapture()
                recordingControls.show(model: self, startedAt: Date())
            } catch {
                recordingControls.hide()
                restoreCaptureUI(succeeded: false)
                recorder = nil; isBusy = false; isRecording = false; message = error.localizedDescription
            }
        }
    }
}

extension NSImage {
    var pngData: Data? {
        guard let cg = cgImage(forProposedRect: nil, context: nil, hints: nil) else { return nil }
        return NSBitmapImageRep(cgImage: cg).representation(using: .png, properties: [:])
    }
}
