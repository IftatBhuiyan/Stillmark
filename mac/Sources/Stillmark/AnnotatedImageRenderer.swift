import AppKit

/// Renders at source pixel dimensions, independent of the monitor backing scale.
@MainActor
enum AnnotatedImageRenderer {
    static func render(original: NSImage, marks: [Annotation]) -> NSImage? {
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
        for mark in marks where mark.tool != .crop { draw(mark, in: size) }
        NSGraphicsContext.restoreGraphicsState()
        guard let rendered = bitmap.cgImage else { return nil }
        if let crop = marks.last(where: { $0.tool == .crop }) {
            let rect = CGRect(x: crop.rect.minX * size.width, y: crop.rect.minY * size.height,
                              width: crop.rect.width * size.width, height: crop.rect.height * size.height).integral
                .intersection(CGRect(origin: .zero, size: size))
            if let cut = rendered.cropping(to: rect) {
                return NSImage(cgImage: cut, size: NSSize(width: cut.width, height: cut.height))
            }
        }
        return NSImage(cgImage: rendered, size: size)
    }

    private static func draw(_ mark: Annotation, in size: NSSize) {
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

}
