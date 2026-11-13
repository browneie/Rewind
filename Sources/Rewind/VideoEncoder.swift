import Foundation
import VideoToolbox
import CoreMedia
import AVFoundation

/// Hardware (Apple Media Engine) H.264/HEVC encoder. Produces encoded CMSampleBuffers.
final class VideoEncoder: @unchecked Sendable {
    private var session: VTCompressionSession?
    private let handler: (CMSampleBuffer) -> Void
    private let lock = NSLock()
    private var forceKey = false

    init(width: Int, height: Int, fps: Int, codec: VideoCodec, bitrate: Int,
         handler: @escaping (CMSampleBuffer) -> Void) throws {
        self.handler = handler
        let spec: [CFString: Any] = [
            kVTVideoEncoderSpecification_EnableHardwareAcceleratedVideoEncoder: true,
            kVTVideoEncoderSpecification_RequireHardwareAcceleratedVideoEncoder: true,
        ]
        var s: VTCompressionSession?
        let status = VTCompressionSessionCreate(
            allocator: nil, width: Int32(width), height: Int32(height),
            codecType: codec == .hevc ? kCMVideoCodecType_HEVC : kCMVideoCodecType_H264,
            encoderSpecification: spec as CFDictionary, imageBufferAttributes: nil,
            compressedDataAllocator: nil, outputCallback: nil, refcon: nil, compressionSessionOut: &s)
        guard status == noErr, let s else {
            throw RewindError("Couldn't start the hardware \(codec.short) encoder at \(width)×\(height) (error \(status)).")
        }
        let props: [CFString: Any] = [
            kVTCompressionPropertyKey_RealTime: true,
            kVTCompressionPropertyKey_ProfileLevel: codec == .hevc
                ? kVTProfileLevel_HEVC_Main_AutoLevel : kVTProfileLevel_H264_High_AutoLevel,
            // No B-frames: keeps PTS == DTS so clips can be cut and muxed without re-encoding.
            kVTCompressionPropertyKey_AllowFrameReordering: false,
            kVTCompressionPropertyKey_AverageBitRate: bitrate,
            kVTCompressionPropertyKey_DataRateLimits: [Double(bitrate) * 1.5 / 8, 1.0] as CFArray,
            kVTCompressionPropertyKey_ExpectedFrameRate: fps,
            // A keyframe every second, so a replay can start within 1s of the requested length.
            kVTCompressionPropertyKey_MaxKeyFrameInterval: fps,
            kVTCompressionPropertyKey_MaxKeyFrameIntervalDuration: 1.0,
        ]
        for (k, v) in props { VTSessionSetProperty(s, key: k, value: v as CFTypeRef) }
        VTCompressionSessionPrepareToEncodeFrames(s)
        session = s
    }

    deinit { invalidate() }

    var pixelBufferPool: CVPixelBufferPool? { session.flatMap { VTCompressionSessionGetPixelBufferPool($0) } }

    func forceKeyframe() {
        lock.lock(); forceKey = true; lock.unlock()
    }

    func encode(_ pixelBuffer: CVPixelBuffer, pts: CMTime) {
        guard let session else { return }
        lock.lock(); let key = forceKey; forceKey = false; lock.unlock()
        let props: CFDictionary? = key ? [kVTEncodeFrameOptionKey_ForceKeyFrame: true] as CFDictionary : nil
        let handler = self.handler
        VTCompressionSessionEncodeFrame(session, imageBuffer: pixelBuffer, presentationTimeStamp: pts,
                                        duration: .invalid, frameProperties: props, infoFlagsOut: nil) { status, _, sample in
            guard status == noErr, let sample, sample.isValid else { return }
            handler(sample)
        }
    }

    func flush() {
        guard let session else { return }
        VTCompressionSessionCompleteFrames(session, untilPresentationTimeStamp: .invalid)
    }

    func invalidate() {
        guard let s = session else { return }
        session = nil
        VTCompressionSessionCompleteFrames(s, untilPresentationTimeStamp: .invalid)
        VTCompressionSessionInvalidate(s)
    }
}

/// An encoded frame with the metadata the replay buffer needs, cached up front.
struct EncodedFrame {
    let sample: CMSampleBuffer
    let pts: CMTime
    let isKey: Bool
    let size: Int

    init(_ sample: CMSampleBuffer) {
        self.sample = sample
        pts = sample.presentationTimeStamp
        size = sample.totalSampleSize
        if let arr = CMSampleBufferGetSampleAttachmentsArray(sample, createIfNecessary: false) as? [[CFString: Any]],
           let first = arr.first {
            isKey = !((first[kCMSampleAttachmentKey_NotSync] as? Bool) ?? false)
        } else {
            isKey = true
        }
    }
}

extension CMSampleBuffer {
    /// Copies an LPCM audio buffer into memory we own, so ScreenCaptureKit's buffer pool isn't held hostage
    /// by the replay buffer.
    func deepCopyAudio() -> CMSampleBuffer? {
        guard let fmt = formatDescription, numSamples > 0,
              let asbd = CMAudioFormatDescriptionGetStreamBasicDescription(fmt)?.pointee else { return nil }
        var timing = CMSampleTimingInfo(duration: CMTime(value: 1, timescale: CMTimeScale(asbd.mSampleRate)),
                                        presentationTimeStamp: presentationTimeStamp, decodeTimeStamp: .invalid)
        var out: CMSampleBuffer?
        guard CMSampleBufferCreate(allocator: kCFAllocatorDefault, dataBuffer: nil, dataReady: false,
                                   makeDataReadyCallback: nil, refcon: nil, formatDescription: fmt,
                                   sampleCount: numSamples, sampleTimingEntryCount: 1, sampleTimingArray: &timing,
                                   sampleSizeEntryCount: 0, sampleSizeArray: nil, sampleBufferOut: &out) == noErr,
              let out else { return nil }
        let status = try? withAudioBufferList(flags: .audioBufferListAssure16ByteAlignment) { abl, _ in
            CMSampleBufferSetDataBufferFromAudioBufferList(out, blockBufferAllocator: kCFAllocatorDefault,
                                                           blockBufferMemoryAllocator: kCFAllocatorDefault,
                                                           flags: 0, bufferList: abl.unsafePointer)
        }
        return status == noErr ? out : nil
    }
}

func aacSettings(for fmt: CMFormatDescription?) -> [String: Any] {
    let asbd = fmt.flatMap { CMAudioFormatDescriptionGetStreamBasicDescription($0)?.pointee }
    let rate = (asbd?.mSampleRate ?? 0) > 0 ? asbd!.mSampleRate : 48_000
    let channels = min(max(Int(asbd?.mChannelsPerFrame ?? 2), 1), 2)
    return [
        AVFormatIDKey: kAudioFormatMPEG4AAC,
        AVSampleRateKey: rate,
        AVNumberOfChannelsKey: channels,
        AVEncoderBitRateKey: channels == 1 ? 96_000 : 192_000,
    ]
}

