import AppKit
import CoreImage

enum CanvasBackground: String, CaseIterable, Identifiable, Codable {
    case none = "None", light = "White", dark = "Charcoal", cream = "Warm", gradient = "Sunset"
    var id: Self { self }
    var color: NSColor {
        switch self {
        case .none: .clear
        case .light: .white
        case .dark: NSColor(srgbRed: 0.10, green: 0.11, blue: 0.13, alpha: 1)
        case .cream: NSColor(srgbRed: 0.96, green: 0.93, blue: 0.87, alpha: 1)
        case .gradient: NSColor(srgbRed: 0.94, green: 0.55, blue: 0.34, alpha: 1)
        }
    }
}

/// Renders at source pixel dimensions, independent of the monitor backing scale.
@MainActor
enum AnnotatedImageRenderer {
    private static let filters = CIContext(options: [.cacheIntermediates: false])
    static func pixelatedRegion(source: CGImage, rect: CGRect, blockSize: Double) -> CGImage? {
        let crop = rect.integral.intersection(CGRect(x: 0, y: 0, width: source.width, height: source.height))
        guard crop.width > 0, crop.height > 0, let region = source.cropping(to: crop) else { return nil }
        let input = CIImage(cgImage: region)
        let output = input.applyingFilter("CIPixellate", parameters: [kCIInputScaleKey: max(4, min(200, blockSize))])
        return filters.createCGImage(output, from: input.extent)
    }
    static func render(original: NSImage, marks: [Annotation], background: CanvasBackground = .none,
                       padding: Double = 0, radius: Double = 0) -> NSImage? {
        guard let cg = original.cgImage(forProposedRect: nil, context: nil, hints: nil),
              cg.width > 0, cg.height > 0, cg.width <= 32768, cg.height <= 32768,
              cg.width * cg.height <= 100_000_000,
              let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: cg.width, pixelsHigh: cg.height,
                                             bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                             colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0),
              let context = NSGraphicsContext(bitmapImageRep: bitmap) else { return nil }
        let size = NSSize(width: cg.width, height: cg.height)
        NSGraphicsContext.saveGraphicsState()
        context.cgContext.translateBy(x: 0, y: size.height)
        context.cgContext.scaleBy(x: 1, y: -1)
        NSGraphicsContext.current = NSGraphicsContext(cgContext: context.cgContext, flipped: true)
        original.draw(in: CGRect(origin: .zero, size: size), from: .zero, operation: .copy,
                      fraction: 1, respectFlipped: true, hints: nil)
        for mark in marks where mark.tool != .crop && mark.tool != .redact { draw(mark, in: size, source: cg) }
        // Solid redaction always wins over later effects that sample original pixels.
        for mark in marks where mark.tool == .redact { draw(mark, in: size, source: cg) }
        NSGraphicsContext.restoreGraphicsState()
        guard let rendered = bitmap.cgImage else { return nil }
        if let crop = marks.last(where: { $0.tool == .crop }) {
            let rect = CGRect(x: crop.rect.minX * size.width, y: crop.rect.minY * size.height,
                              width: crop.rect.width * size.width, height: crop.rect.height * size.height).integral
                .intersection(CGRect(origin: .zero, size: size))
            if let cut = rendered.cropping(to: rect) {
                return present(cut, background: background, padding: padding, radius: radius)
            }
        }
        return present(rendered, background: background, padding: padding, radius: radius)
    }

    private static func present(_ image: CGImage, background: CanvasBackground, padding: Double, radius: Double) -> NSImage? {
        guard background != .none else { return NSImage(cgImage: image, size: NSSize(width: image.width, height: image.height)) }
        let inset = Int(min(240, max(0, padding)))
        let width = image.width + inset * 2, height = image.height + inset * 2
        guard width * height <= 100_000_000,
              let context = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
                                      space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        context.setFillColor(background.color.cgColor)
        context.fill(CGRect(x: 0, y: 0, width: width, height: height))
        if background == .gradient,
           let gradient = CGGradient(colorsSpace: CGColorSpaceCreateDeviceRGB(),
                                     colors: [background.color.cgColor, NSColor(srgbRed: 0.48, green: 0.34, blue: 0.64, alpha: 1).cgColor] as CFArray,
                                     locations: [0, 1]) {
            context.drawLinearGradient(gradient, start: .zero, end: CGPoint(x: width, y: height), options: [])
        }
        let rect = CGRect(x: inset, y: inset, width: image.width, height: image.height)
        let corner = min(max(0, radius), Double(min(image.width, image.height)) / 2)
        let path = CGPath(roundedRect: rect, cornerWidth: corner, cornerHeight: corner, transform: nil)
        if inset > 0 {
            context.saveGState()
            context.setShadow(offset: CGSize(width: 0, height: -6), blur: 20, color: NSColor.black.withAlphaComponent(0.22).cgColor)
            context.setFillColor(NSColor.black.cgColor); context.addPath(path); context.fillPath()
            context.restoreGState()
        }
        context.addPath(path); context.clip(); context.draw(image, in: rect)
        guard let result = context.makeImage() else { return nil }
        return NSImage(cgImage: result, size: NSSize(width: width, height: height))
    }

    private static func draw(_ mark: Annotation, in size: NSSize, source: CGImage) {
        let a = NSPoint(x: mark.start.x * size.width, y: mark.start.y * size.height)
        let b = NSPoint(x: mark.end.x * size.width, y: mark.end.y * size.height)
        let rect = CGRect(x: mark.rect.minX * size.width, y: mark.rect.minY * size.height,
                          width: mark.rect.width * size.width, height: mark.rect.height * size.height)
        let width = max(1, min(40, mark.width))
        mark.color.setStroke()
        let path = NSBezierPath(); path.lineWidth = width; path.lineCapStyle = .round; path.lineJoinStyle = .round
        switch mark.tool {
        case .rectangle: path.appendRect(rect); path.stroke()
        case .filledRectangle: mark.color.setFill(); NSBezierPath(rect: rect).fill()
        case .ellipse: path.appendOval(in: rect); path.stroke()
        case .line: path.move(to: a); path.line(to: b); path.stroke()
        case .pencil:
            path.move(to: a)
            for point in mark.points { path.line(to: CGPoint(x: point.x * size.width, y: point.y * size.height)) }
            path.line(to: b); path.stroke()
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
            (mark.text as NSString).draw(at: a, withAttributes: [.font: NSFont.boldSystemFont(ofSize: max(8, min(200, mark.fontSize))), .foregroundColor: mark.color])
        case .counter:
            let diameter = max(28, width * 9)
            let circle = CGRect(x: a.x - diameter / 2, y: a.y - diameter / 2, width: diameter, height: diameter)
            mark.color.setFill(); NSBezierPath(ovalIn: circle).fill()
            let attributes: [NSAttributedString.Key: Any] = [.font: NSFont.boldSystemFont(ofSize: diameter * 0.58), .foregroundColor: NSColor.white]
            let text = mark.text as NSString, textSize = text.size(withAttributes: attributes)
            text.draw(at: CGPoint(x: a.x - textSize.width / 2, y: a.y - textSize.height / 2), withAttributes: attributes)
        case .spotlight:
            let dim = NSBezierPath(rect: CGRect(origin: .zero, size: size))
            dim.appendRect(rect); dim.windingRule = .evenOdd
            NSColor.black.withAlphaComponent(0.6).setFill(); dim.fill()
        case .pixelate:
            let crop = rect.integral.intersection(CGRect(origin: .zero, size: size))
            if let pixels = pixelatedRegion(source: source, rect: crop, blockSize: max(10, width * 5)) {
                NSImage(cgImage: pixels, size: crop.size).draw(in: crop, from: .zero, operation: .copy,
                                                           fraction: 1, respectFlipped: true, hints: nil)
            }
        default: break
        }
    }

}
