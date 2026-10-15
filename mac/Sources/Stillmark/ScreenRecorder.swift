import ScreenCaptureKit
import AVFoundation
import AppKit

@MainActor
final class ScreenRecorder: NSObject, SCRecordingOutputDelegate, SCStreamDelegate {
    private var stream: SCStream?
    private var output: SCRecordingOutput?
    private var url: URL?
    private var completion: ((Result<URL, Error>) -> Void)?

    init(completion: @escaping (Result<URL, Error>) -> Void) { self.completion = completion }

    func start(displayID: CGDirectDisplayID, url: URL, fps: Int, systemAudio: Bool, microphone: Bool, showCursor: Bool) async throws {
        if microphone {
            guard await AVCaptureDevice.requestAccess(for: .audio) else {
                throw NSError(domain: "Stillmark", code: 1, userInfo: [NSLocalizedDescriptionKey: "Microphone permission was denied. Turn off microphone recording or allow it in System Settings."])
            }
        }
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        guard let display = content.displays.first(where: { $0.displayID == displayID }) ?? content.displays.first else {
            throw NSError(domain: "Stillmark", code: 2, userInfo: [NSLocalizedDescriptionKey: "No display is available."])
        }
        let ownApps = content.applications.filter { $0.processID == ProcessInfo.processInfo.processIdentifier }
        let filter = SCContentFilter(display: display, excludingApplications: ownApps, exceptingWindows: [])
        let config = SCStreamConfiguration()
        // Bound the encoder to 4K and retain aspect ratio, using even H.264 dimensions.
        let scale = min(1.0, 3840.0 / Double(max(display.width, display.height)))
        config.width = max(2, Int(Double(display.width) * scale) / 2 * 2)
        config.height = max(2, Int(Double(display.height) * scale) / 2 * 2)
        config.minimumFrameInterval = CMTime(value: 1, timescale: CMTimeScale(fps == 60 ? 60 : 30))
        config.queueDepth = 3
        config.showsCursor = showCursor
        config.capturesAudio = systemAudio
        config.excludesCurrentProcessAudio = true
        config.captureMicrophone = microphone
        let recording = SCRecordingOutputConfiguration()
        recording.outputURL = url; recording.outputFileType = .mp4; recording.videoCodecType = .h264
        let output = SCRecordingOutput(configuration: recording, delegate: self)
        let stream = SCStream(filter: filter, configuration: config, delegate: self)
        try stream.addRecordingOutput(output)
        self.output = output; self.stream = stream; self.url = url
        try await stream.startCapture()
    }

    func stop() async {
        do { try await stream?.stopCapture() }
        catch { finish(.failure(error)) }
    }

    nonisolated func recordingOutputDidStartRecording(_ recordingOutput: SCRecordingOutput) {}
    nonisolated func recordingOutputDidFinishRecording(_ recordingOutput: SCRecordingOutput) {
        Task { @MainActor in if let url = self.url { self.finish(.success(url)) } }
    }
    nonisolated func recordingOutput(_ recordingOutput: SCRecordingOutput, didFailWithError error: Error) {
        Task { @MainActor in self.finish(.failure(error)) }
    }
    nonisolated func stream(_ stream: SCStream, didStopWithError error: Error) {
        Task { @MainActor in self.finish(.failure(error)) }
    }

    private func finish(_ result: Result<URL, Error>) {
        let callback = completion; completion = nil
        stream = nil; output = nil
        callback?(result)
    }
}
