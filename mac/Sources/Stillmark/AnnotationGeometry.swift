import AppKit

@MainActor
enum AnnotationGeometry {
    static func bounds(of mark: Annotation, sourceSize: CGSize) -> CGRect {
        guard sourceSize.width > 0, sourceSize.height > 0 else { return mark.rect }
        if mark.tool == .text {
            let font = NSFont.boldSystemFont(ofSize: max(8, min(200, mark.fontSize)))
            let size = (mark.text as NSString).size(withAttributes: [.font: font])
            return CGRect(origin: mark.start, size: CGSize(width: size.width / sourceSize.width, height: size.height / sourceSize.height))
        }
        if mark.tool == .counter {
            let radius = max(28, mark.width * 9) / 2
            return CGRect(x: mark.start.x - radius / sourceSize.width, y: mark.start.y - radius / sourceSize.height,
                          width: radius * 2 / sourceSize.width, height: radius * 2 / sourceSize.height)
        }
        return mark.rect
    }
}
