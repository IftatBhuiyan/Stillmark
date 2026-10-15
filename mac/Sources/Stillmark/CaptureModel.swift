import AppKit
import SwiftUI
import ScreenCaptureKit
import Vision
import UniformTypeIdentifiers
import AVFoundation

enum CaptureKind { case area, screen, window }
enum AnnotationTool: String, CaseIterable, Identifiable {
    case select = "Move", rectangle = "Box", arrow = "Arrow", highlight = "Highlight", redact = "Redact", text = "Text", crop = "Crop"
    var id: Self { self }
    var symbol: String {
        switch self {
        case .select: "cursorarrow"
        case .rectangle: "rectangle"
        case .arrow: "arrow.up.right"
        case .highlight: "highlighter"
        case .redact: "rectangle.fill"
        case .text: "textformat"
        case .crop: "crop"
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
    var rect: CGRect {
        CGRect(x: min(start.x, end.x), y: min(start.y, end.y), width: abs(start.x - end.x), height: abs(start.y - end.y))
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
    @Published var marks: [Annotation] = []
    @Published var redoMarks: [Annotation] = []
    @Published var tool: AnnotationTool = .arrow
    @Published var ink: Color = .orange
    @Published var text = ""
    @Published var message: String?
    @Published var isBusy = false
    @Published var isRecording = false
    @Published var displays: [SCDisplay] = []
    @Published var displayID: CGDirectDisplayID = CGMainDisplayID()
    @Published var status = "Everything stays on your Mac."
    @AppStorage("recordSystemAudio") var recordSystemAudio = false
    @AppStorage("recordMicrophone") var recordMicrophone = false
    @AppStorage("recordFPS") var recordFPS = 30
    @AppStorage("showCursor") var showCursor = true
    let captureDirectory: URL
    private var edits: [URL: [Annotation]] = [:]
    private var loadedURL: URL?
    private var recorder: ScreenRecorder?
    private var captureProcess: Process?
    private var pins: [NSPanel] = []

    var hasUnsavedAnnotations: Bool { !marks.isEmpty || edits.values.contains { !$0.isEmpty } }

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
        if let loadedURL { edits[loadedURL] = marks }
        loadedURL = selection
        marks = selection.flatMap { edits[$0] } ?? []
        redoMarks = []
        selectedImage = selection.flatMap { $0.pathExtension == "mp4" ? nil : NSImage(contentsOf: $0) }
    }

    func newURL(extension ext: String, prefix: String = "Stillmark") -> URL {
        let date = Date().formatted(.iso8601.year().month().day().time(includingFractionalSeconds: false).timeSeparator(.omitted))
        return captureDirectory.appendingPathComponent("\(prefix)-\(date)-\(UUID().uuidString.prefix(6)).\(ext)")
    }

    func capture(_ kind: CaptureKind) {
        guard !isBusy, !isRecording else { return }
        isBusy = true
        let url = newURL(extension: "png")
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
        var args = ["-x", "-t", "png"]
        switch kind {
        case .area: args += ["-i", "-s"]
        case .window: args += ["-i", "-w"]
        case .screen:
            let displayIndex = (NSScreen.screens.firstIndex { ($0.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value == displayID } ?? 0) + 1
            args += ["-D", String(displayIndex)]
        }
        args.append(url.path)
        process.arguments = args
        process.standardError = Pipe()
        captureProcess = process
        NSApp.hide(nil)
        process.terminationHandler = { [weak self] process in
            Task { @MainActor in
                guard let self else { return }
                self.isBusy = false
                self.captureProcess = nil
                NSApp.unhide(nil)
                NSApp.activate(ignoringOtherApps: true)
                if FileManager.default.fileExists(atPath: url.path) {
                    self.refreshHistory()
                    self.selection = url
                    self.status = "Capture saved locally."
                } else if process.terminationStatus != 0 && kind == .screen {
                    self.message = "Capture failed. Allow Stillmark in System Settings → Privacy & Security → Screen & System Audio Recording, then relaunch."
                }
            }
        }
        do { try process.run() }
        catch {
            captureProcess = nil; isBusy = false; NSApp.unhide(nil)
            message = error.localizedDescription
        }
    }

    func importImage() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.png, .jpeg, .tiff, .heic]
        panel.allowsMultipleSelection = false
        guard panel.runModal() == .OK, let source = panel.url,
              let image = NSImage(contentsOf: source), let data = image.pngData else { return }
        do {
            let url = newURL(extension: "png", prefix: "Imported")
            try data.write(to: url, options: .atomic)
            refreshHistory(); selection = url
        } catch { message = error.localizedDescription }
    }

    func addMark(from start: CGPoint, to end: CGPoint) {
        guard tool != .select else { return }
        if tool == .text && text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            status = "Enter annotation text in the toolbar first."
            return
        }
        let mark = Annotation(tool: tool, start: start, end: end, text: text, color: NSColor(ink))
        let validSize = tool == .arrow
            ? hypot(end.x - start.x, end.y - start.y) > 0.005
            : mark.rect.width > 0.002 && mark.rect.height > 0.002
        guard tool == .text || validSize else { return }
        marks.append(mark); redoMarks = []
    }

    func undo() { if let mark = marks.popLast() { redoMarks.append(mark) } }
    func redo() { if let mark = redoMarks.popLast() { marks.append(mark) } }

    func renderedImage() -> NSImage? {
        guard let original = selectedImage, let cg = original.cgImage(forProposedRect: nil, context: nil, hints: nil) else { return nil }
        let size = NSSize(width: cg.width, height: cg.height)
        let result = NSImage(size: size)
        result.lockFocusFlipped(true)
        original.draw(in: CGRect(origin: .zero, size: size), from: .zero, operation: .copy, fraction: 1, respectFlipped: true, hints: nil)
        for mark in marks where mark.tool != .crop { draw(mark, in: size) }
        result.unlockFocus()
        if let crop = marks.last(where: { $0.tool == .crop }),
           let rendered = result.cgImage(forProposedRect: nil, context: nil, hints: nil) {
            let rect = CGRect(x: crop.rect.minX * size.width, y: crop.rect.minY * size.height,
                              width: crop.rect.width * size.width, height: crop.rect.height * size.height).integral
                .intersection(CGRect(origin: .zero, size: size))
            if let cut = rendered.cropping(to: rect) { return NSImage(cgImage: cut, size: NSSize(width: cut.width, height: cut.height)) }
        }
        return result
    }

    private func draw(_ mark: Annotation, in size: NSSize) {
        let a = NSPoint(x: mark.start.x * size.width, y: mark.start.y * size.height)
        let b = NSPoint(x: mark.end.x * size.width, y: mark.end.y * size.height)
        let rect = CGRect(x: mark.rect.minX * size.width, y: mark.rect.minY * size.height,
                          width: mark.rect.width * size.width, height: mark.rect.height * size.height)
        let width = max(3, size.width / 450)
        mark.color.setStroke()
        let path = NSBezierPath(); path.lineWidth = width; path.lineCapStyle = .round; path.lineJoinStyle = .round
        switch mark.tool {
        case .rectangle: path.appendRect(rect); path.stroke()
        case .highlight: mark.color.withAlphaComponent(0.3).setFill(); NSBezierPath(rect: rect).fill()
        case .redact: NSColor.black.setFill(); NSBezierPath(rect: rect).fill()
        case .arrow:
            path.move(to: a); path.line(to: b)
            let angle = atan2(b.y - a.y, b.x - a.x), head = width * 5
            path.move(to: NSPoint(x: b.x - head * cos(angle - .pi / 6), y: b.y - head * sin(angle - .pi / 6)))
            path.line(to: b)
            path.line(to: NSPoint(x: b.x - head * cos(angle + .pi / 6), y: b.y - head * sin(angle + .pi / 6)))
            path.stroke()
        case .text:
            (mark.text as NSString).draw(at: a, withAttributes: [.font: NSFont.boldSystemFont(ofSize: max(18, size.width / 45)), .foregroundColor: mark.color])
        default: break
        }
    }

    func copyImage() {
        guard let image = renderedImage() else { return }
        NSPasteboard.general.clearContents(); NSPasteboard.general.writeObjects([image])
        status = "Image copied."
    }

    func exportImage() {
        guard let image = renderedImage(), let data = image.pngData else { return }
        let panel = NSSavePanel(); panel.allowedContentTypes = [.png]
        panel.nameFieldStringValue = newURL(extension: "png", prefix: "Edited").lastPathComponent
        panel.directoryURL = captureDirectory
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            try data.write(to: url, options: .atomic)
            if let selection { edits[selection] = [] }
            marks = []; redoMarks = []
            refreshHistory(); selection = url; status = "Image exported."
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
        guard !isBusy else { return }
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
                    self.recorder = nil; self.isRecording = false; self.isBusy = false
                    NSApp.unhide(nil)
                    switch result {
                    case .success(let url): self.refreshHistory(); self.selection = url; self.status = "Recording saved."
                    case .failure(let error): self.message = error.localizedDescription
                    }
                }
                self.recorder = recorder
                try await recorder.start(displayID: displayID, url: url, fps: recordFPS,
                                         systemAudio: recordSystemAudio, microphone: recordMicrophone, showCursor: showCursor)
                isRecording = true; isBusy = false; status = "Recording. Stop from the menu bar or ⌘⇧5."
                NSApp.hide(nil)
            } catch {
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
