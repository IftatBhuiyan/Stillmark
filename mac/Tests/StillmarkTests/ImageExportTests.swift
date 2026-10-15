import XCTest
import AppKit
@testable import Stillmark

final class ImageExportTests: XCTestCase {
    func testExportRetainsSourcePixelDimensionsOnRetinaDisplays() async throws {
        try await MainActor.run {
            let image = try Self.sourceImage()
            let exported = try XCTUnwrap(AnnotatedImageRenderer.render(original: image, marks: []))
            let cg = try XCTUnwrap(exported.cgImage(forProposedRect: nil, context: nil, hints: nil))
            XCTAssertEqual(cg.width, 320)
            XCTAssertEqual(cg.height, 200)
        }
    }

    func testSolidRedactionIsFlattenedIntoExportAndCropUsesPixelCoordinates() async throws {
        try await MainActor.run {
            let original = try Self.sourceImage()
            let redact = Annotation(tool: .redact, start: CGPoint(x: 0.25, y: 0.25), end: CGPoint(x: 0.75, y: 0.75), color: .red)
            let crop = Annotation(tool: .crop, start: CGPoint(x: 0.25, y: 0.25), end: CGPoint(x: 0.75, y: 0.75), color: .red)
            let exported = try XCTUnwrap(AnnotatedImageRenderer.render(original: original, marks: [redact, crop]))
            let cg = try XCTUnwrap(exported.cgImage(forProposedRect: nil, context: nil, hints: nil))
            XCTAssertEqual(cg.width, 160)
            XCTAssertEqual(cg.height, 100)
            let bitmap = NSBitmapImageRep(cgImage: cg)
            let center = try XCTUnwrap(bitmap.colorAt(x: 80, y: 50)?.usingColorSpace(.deviceRGB))
            XCTAssertEqual(center.redComponent, 0, accuracy: 0.01)
            XCTAssertEqual(center.greenComponent, 0, accuracy: 0.01)
            XCTAssertEqual(center.blueComponent, 0, accuracy: 0.01)
            XCTAssertEqual(center.alphaComponent, 1, accuracy: 0.01)
        }
    }

    @MainActor private static func sourceImage() throws -> NSImage {
        let context = try XCTUnwrap(CGContext(data: nil, width: 320, height: 200, bitsPerComponent: 8,
                                            bytesPerRow: 0, space: CGColorSpaceCreateDeviceRGB(),
                                            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        context.setFillColor(CGColor(red: 1, green: 1, blue: 1, alpha: 1))
        context.fill(CGRect(x: 0, y: 0, width: 320, height: 200))
        let cg = try XCTUnwrap(context.makeImage())
        // A point size different from pixel size catches implicit-resolution exports.
        return NSImage(cgImage: cg, size: NSSize(width: 160, height: 100))
    }
}
