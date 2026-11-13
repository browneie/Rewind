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

    struct Target {
        let filter: SCContentFilter
        /// Name of the app being captured, or nil when capturing the whole display.
        let appName: String?
    }

    /// What to capture. With an app chosen and running: only that app's windows (and audio), on the display
    /// where its biggest window is. Otherwise the chosen display, with Rewind's own windows (overlay,
    /// toasts) left out of the picture.
    static func target(displayID: UInt32, appID: String) async throws -> Target {
        // All windows, not just on-screen ones: Rewind must be in `applications` to be left out of the
        // picture, and right after launch it may have nothing on screen yet.
        await MainActor.run { CaptureAnchor.ensure() }
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
        let wanted = displayID == 0 ? CGMainDisplayID() : displayID
        guard let chosenDisplay = content.displays.first(where: { $0.displayID == wanted }) ?? content.displays.first else {
            throw RewindError("No display found to capture.")
        }
        if !appID.isEmpty,
           let app = content.applications.first(where: {
               AppTarget.key(bundleID: $0.bundleIdentifier, name: $0.applicationName) == appID
           }) {
            let windows = content.windows.filter {
                $0.owningApplication?.processID == app.processID && $0.isOnScreen && $0.frame.width > 50 && $0.frame.height > 50
            }
            if let biggest = windows.max(by: { $0.frame.width * $0.frame.height < $1.frame.width * $1.frame.height }) {
                let center = CGPoint(x: biggest.frame.midX, y: biggest.frame.midY)
                let display = content.displays.first { $0.frame.contains(center) } ?? chosenDisplay
                // Include every process with the same bundle ID / name (e.g. helper processes).
                let apps = content.applications.filter {
                    AppTarget.key(bundleID: $0.bundleIdentifier, name: $0.applicationName) == appID
                }
                return Target(filter: SCContentFilter(display: display, including: apps, exceptingWindows: []),
                              appName: app.applicationName)
            }
        }
        let pid = ProcessInfo.processInfo.processIdentifier
        let mine = content.applications.filter { $0.processID == pid }
        if mine.isEmpty { NSLog("Rewind: couldn't find itself to exclude from capture") }
        return Target(filter: SCContentFilter(display: chosenDisplay, excludingApplications: mine, exceptingWindows: []),
                      appName: nil)
    }

    /// Name of the app being captured right now, or nil for the whole display.
    private(set) var capturedApp: String?

    func start(_ sig: CaptureSignature) async throws {
        let target = try await Self.target(displayID: sig.displayID, appID: sig.appID)
        let filter = target.filter
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
        capturedApp = target.appName
        info = (target.appName.map { "\($0) · " } ?? "") + "\(Int(size.width))×\(Int(size.height)) · \(sig.fps) fps · \(sig.codec.short) · \(bitrate / 1_000_000) Mbps"
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
        capturedApp = nil
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

    static func screenshot(displayID: UInt32, appID: String, showCursor: Bool, to url: URL) async throws {
        let filter = try await target(displayID: displayID, appID: appID).filter
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

/// A permanent, invisible 1×1 window. ScreenCaptureKit only lists apps that own a window, and Rewind
/// has to be listed to be left out of the capture; without this it isn't until the overlay first opens.
@MainActor
enum CaptureAnchor {
    private static var window: NSWindow?

    static func ensure() {
        guard window == nil else { return }
        let w = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 1, height: 1),
                        styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        w.isOpaque = false
        w.backgroundColor = .clear
        w.alphaValue = 0
        w.hasShadow = false
        w.ignoresMouseEvents = true
        w.isReleasedWhenClosed = false
        w.level = .normal
        w.collectionBehavior = [.canJoinAllSpaces, .stationary, .ignoresCycle, .fullScreenAuxiliary]
        w.orderFrontRegardless()
        window = w
    }
}
