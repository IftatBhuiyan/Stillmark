import AppKit
import ImageIO
import UniformTypeIdentifiers

struct StillmarkProject: Codable {
    static let contentType = UTType(exportedAs: "io.eclipsestudios.stillmark.project", conformingTo: .data)
    var version = 1
    var sourcePNG: Data
    var annotations: [Annotation]
    var background: CanvasBackground
    var padding: Double
    var radius: Double

    static func read(from url: URL) throws -> (StillmarkProject, NSImage) {
        let size = try url.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? 0
        guard size > 0, size <= 128 * 1024 * 1024 else { throw ProjectFileError.invalid }
        let project = try JSONDecoder().decode(StillmarkProject.self, from: Data(contentsOf: url))
        guard project.version == 1 else { throw ProjectFileError.unsupportedVersion }
        guard project.sourcePNG.count <= 64 * 1024 * 1024, project.annotations.count <= 10_000,
              project.annotations.reduce(0, { $0 + $1.points.count }) <= 250_000,
              project.padding.isFinite, (0...240).contains(project.padding),
              project.radius.isFinite, (0...64).contains(project.radius),
              let source = CGImageSourceCreateWithData(project.sourcePNG as CFData, nil),
              CGImageSourceGetType(source) as String? == UTType.png.identifier,
              let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
              let width = properties[kCGImagePropertyPixelWidth] as? Int,
              let height = properties[kCGImagePropertyPixelHeight] as? Int,
              width > 0, height > 0, width <= 32768, height <= 32768, width * height <= 100_000_000,
              let image = CGImageSourceCreateImageAtIndex(source, 0, nil) else { throw ProjectFileError.invalid }
        return (project, NSImage(cgImage: image, size: NSSize(width: width, height: height)))
    }
}

enum ProjectFileError: LocalizedError {
    case invalid, unsupportedVersion
    var errorDescription: String? {
        switch self {
        case .invalid: "This project is invalid or exceeds Stillmark’s image/document limits."
        case .unsupportedVersion: "This project was saved by an unsupported Stillmark version."
        }
    }
}

extension Annotation: Codable {
    private enum CodingKeys: String, CodingKey { case tool, start, end, text, color, points, width, fontSize }

    init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        let tool = try values.decode(AnnotationTool.self, forKey: .tool)
        let start = try values.decode(CGPoint.self, forKey: .start)
        let end = try values.decode(CGPoint.self, forKey: .end)
        let text = try values.decode(String.self, forKey: .text)
        let color = try values.decode([Double].self, forKey: .color)
        let points = try values.decode([CGPoint].self, forKey: .points)
        let width = try values.decode(Double.self, forKey: .width)
        let fontSize = try values.decode(Double.self, forKey: .fontSize)
        func valid(_ p: CGPoint) -> Bool { p.x.isFinite && p.y.isFinite && (0...1).contains(p.x) && (0...1).contains(p.y) }
        guard valid(start), valid(end), points.count <= 5000, points.allSatisfy(valid), text.count <= 20_000,
              color.count == 4, color.allSatisfy({ $0.isFinite && (0...1).contains($0) }),
              width.isFinite, (1...40).contains(width), fontSize.isFinite, (8...200).contains(fontSize) else { throw ProjectFileError.invalid }
        self.init(tool: tool, start: start, end: end, text: text,
                  color: NSColor(srgbRed: color[0], green: color[1], blue: color[2], alpha: color[3]),
                  points: points, width: width, fontSize: fontSize)
    }

    func encode(to encoder: Encoder) throws {
        var values = encoder.container(keyedBy: CodingKeys.self)
        try values.encode(tool, forKey: .tool)
        try values.encode(start, forKey: .start); try values.encode(end, forKey: .end)
        try values.encode(text, forKey: .text); try values.encode(points, forKey: .points)
        try values.encode(width, forKey: .width); try values.encode(fontSize, forKey: .fontSize)
        let color = color.usingColorSpace(.sRGB) ?? .black
        try values.encode([color.redComponent, color.greenComponent, color.blueComponent, color.alphaComponent], forKey: .color)
    }
}
