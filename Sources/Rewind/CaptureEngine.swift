import Foundation
import ScreenCaptureKit
import CoreMedia
import AppKit
import ImageIO
import UniformTypeIdentifiers

/// Wraps an SCStream: raw frames go into the hardware encoder, audio is copied out.
final class CaptureEngine: NSObject, SCStreamOutput, SCStreamDelegate, @unchecked Sendable {
    var onVideo: ((CMSampleBuffer) -> Void)?
    var onAudio: ((CMSampleBuffer, AudioTrack) -> Void)?
    var onStop: ((Error) -> Void)?

    private(set) var info = ""
    private var stream: SCStream?
    private var encoder: VideoEncoder?
    private let videoQueue = DispatchQueue(label: "rewind.capture.video", qos: .userInteractive)
    private let audioQueue = DispatchQueue(label: "rewind.capture.audio", qos: .userInteractive)

    /// The chosen display, with Rewind's own windows (overlay, toasts) left out of the picture.
    static func filter(for displayID: UInt32) async throws -> SCContentFilter {
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        let wanted = displayID == 0 ? CGMainDisplayID() : displayID
        guard let display = content.displays.first(where: { $0.displayID == wanted }) ?? content.displays.first else {
            throw RewindError("No display found to capture.")
        }
        let pid = ProcessInfo.processInfo.processIdentifier
        let mine = content.applications.filter { $0.processID == pid }
        return SCContentFilter(display: display, excludingApplications: mine, exceptingWindows: [])
    }

    func start(_ sig: CaptureSignature) async throws {
        let filter = try await Self.filter(for: sig.displayID)
        let scale = CGFloat(filter.pointPixelScale)
        let native = CGSize(width: filter.contentRect.width * scale, height: filter.contentRect.height * scale)
        let size = CaptureMath.outputSize(native: native, resolution: sig.resolution, codec: sig.codec)
        let bitrate = CaptureMath.bitrate(size: size, fps: sig.fps, quality: sig.quality, codec: sig.codec)

        let cfg = SCStreamConfiguration()
        cfg.width = Int(size.width)
        cfg.height = Int(size.height)
        cfg.minimumFrameInterval = CMTime(value: 1, timescale: CMTimeScale(sig.fps))
        cfg.pixelFormat = kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange
        cfg.colorSpaceName = CGColorSpace.itur_709
        cfg.colorMatrix = CGDisplayStream.yCbCrMatrix_ITU_R_709_2
        cfg.queueDepth = 8
        cfg.showsCursor = sig.showCursor
        cfg.capturesAudio = sig.systemAudio
        cfg.sampleRate = 48_000
        cfg.channelCount = 2
        cfg.excludesCurrentProcessAudio = true
        cfg.captureMicrophone = sig.microphone

        let enc = try VideoEncoder(width: Int(size.width), height: Int(size.height), fps: sig.fps,
                                   codec: sig.codec, bitrate: bitrate) { [weak self] sample in
            self?.onVideo?(sample)
        }
        let s = SCStream(filter: filter, configuration: cfg, delegate: self)
        try s.addStreamOutput(self, type: .screen, sampleHandlerQueue: videoQueue)
        if sig.systemAudio { try s.addStreamOutput(self, type: .audio, sampleHandlerQueue: audioQueue) }
        if sig.microphone { try s.addStreamOutput(self, type: .microphone, sampleHandlerQueue: audioQueue) }

        encoder = enc
        stream = s
        do {
            try await s.startCapture()
        } catch {
            stream = nil
            encoder = nil
            enc.invalidate()
            throw error
        }
        info = "\(Int(size.width))×\(Int(size.height)) · \(sig.fps) fps · \(sig.codec.short) · \(bitrate / 1_000_000) Mbps"
    }

    func stop() async {
        guard let s = stream else { return }
        stream = nil
        try? await s.stopCapture()
        videoQueue.sync {}
        let enc = encoder
        encoder = nil
        enc?.invalidate()
        info = ""
    }

    func forceKeyframe() { encoder?.forceKeyframe() }

    // MARK: SCStreamOutput

    func stream(_ stream: SCStream, didOutputSampleBuffer sample: CMSampleBuffer, of type: SCStreamOutputType) {
        guard sample.isValid else { return }
        switch type {
        case .screen:
            guard let attachments = CMSampleBufferGetSampleAttachmentsArray(sample, createIfNecessary: false)
                    as? [[SCStreamFrameInfo: Any]],
                  let raw = attachments.first?[.status] as? Int,
                  SCFrameStatus(rawValue: raw) == .complete,
                  let pixelBuffer = sample.imageBuffer else { return }
            encoder?.encode(pixelBuffer, pts: sample.presentationTimeStamp)
        case .audio:
            if let copy = sample.deepCopyAudio() { onAudio?(copy, .system) }
        case .microphone:
            if let copy = sample.deepCopyAudio() { onAudio?(copy, .mic) }
        @unknown default:
            break
        }
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        guard stream === self.stream else { return }
        self.stream = nil
        encoder?.invalidate()
        encoder = nil
        onStop?(error)
    }

    // MARK: Screenshots

    static func screenshot(displayID: UInt32, showCursor: Bool, to url: URL) async throws {
        let filter = try await filter(for: displayID)
        let scale = CGFloat(filter.pointPixelScale)
        let cfg = SCStreamConfiguration()
        cfg.width = Int(filter.contentRect.width * scale)
        cfg.height = Int(filter.contentRect.height * scale)
        cfg.showsCursor = showCursor
        cfg.captureResolution = .best
        let image = try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: cfg)
        guard let dest = CGImageDestinationCreateWithURL(url as CFURL, UTType.png.identifier as CFString, 1, nil) else {
            throw RewindError("Couldn't create \(url.lastPathComponent).")
        }
        CGImageDestinationAddImage(dest, image, nil)
        guard CGImageDestinationFinalize(dest) else { throw RewindError("Couldn't write \(url.lastPathComponent).") }
    }
}
