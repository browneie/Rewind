import AVFoundation

/// What Trim saves: the original stream (no re-encode) or a new encode in another codec.
enum ExportFormat: String, CaseIterable, Identifiable {
    case original, h264, hevc
    var id: String { rawValue }

    var label: String {
        switch self {
        case .original: "Original"
        case .h264: "H.264"
        case .hevc: "HEVC"
        }
    }

    var codecType: AVVideoCodecType? {
        switch self {
        case .original: nil
        case .h264: .h264
        case .hevc: .hevc
        }
    }
}

/// Re-encodes (part of) a clip into H.264 or HEVC at a chosen quality, on the hardware encoder.
/// Audio is copied as-is.
enum ClipConverter {
    /// The slider's words, from the lowest setting up.
    static let qualityWords: [(Int, String)] = [
        (0, "much smaller file"),
        (25, "smaller file"),
        (45, "balanced"),
        (65, "high quality"),
        (85, "very high quality"),
        (100, "original"),
    ]

    static func word(for quality: Int) -> String {
        qualityWords.last { quality >= $0.0 }?.1 ?? qualityWords[0].1
    }

    /// Slider 0–99 → the encoder's constant-quality setting. The bottom is kept above 0, where hardware
    /// encoders fall apart. 100 ("original") is handled separately: see `convert`.
    static func encoderQuality(_ slider: Int) -> Double {
        0.15 + 0.85 * Double(min(max(slider, 0), 100)) / 100
    }

    /// Writes `range` of `source` to `dest` in `format` at `quality` (0–100). `progress` gets 0…1 on the main actor.
    /// At 100 ("original"): a clip already in `format` is copied without re-encoding (exactly the original
    /// quality), and anything else is re-encoded at the original's bitrate (about the same size).
    static func convert(_ source: URL, range: CMTimeRange, format: ExportFormat, quality: Int, to dest: URL,
                        progress: @escaping @MainActor (Double) -> Void) async throws {
        guard let codec = format.codecType else { throw RewindError("Nothing to convert to.") }
        let asset = AVURLAsset(url: source)
        guard let vTrack = try await asset.loadTracks(withMediaType: .video).first else {
            throw RewindError("This clip has no video.")
        }
        let aTracks = try await asset.loadTracks(withMediaType: .audio)
        let natural = try await vTrack.load(.naturalSize)
        let transform = try await vTrack.load(.preferredTransform)
        let fps = try await vTrack.load(.nominalFrameRate)
        let sourceBitrate = try await vTrack.load(.estimatedDataRate)

        if quality >= 100 {
            let subtype = try await vTrack.load(.formatDescriptions).first.map { CMFormatDescriptionGetMediaSubType($0) }
            let same = switch codec {
            case .h264: subtype == kCMVideoCodecType_H264
            case .hevc: subtype == kCMVideoCodecType_HEVC || subtype == kCMVideoCodecType_HEVCWithAlpha
            default: false
            }
            if same { // already this format: copy it, like Trim with Original
                try await ClipTrimmer.trim(source, range: range, to: dest)
                Task { @MainActor in progress(1) }
                return
            }
        }

        let tmp = dest.deletingLastPathComponent().appendingPathComponent(".converting-\(UUID().uuidString).mp4")
        defer { try? FileManager.default.removeItem(at: tmp) }

        let reader = try AVAssetReader(asset: asset)
        reader.timeRange = range
        let writer = try AVAssetWriter(outputURL: tmp, fileType: .mp4)
        writer.shouldOptimizeForNetworkUse = true

        // Video: decode to YUV, re-encode on the hardware encoder.
        let vOut = AVAssetReaderTrackOutput(track: vTrack, outputSettings: [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange,
        ])
        vOut.alwaysCopiesSampleData = false
        guard reader.canAdd(vOut) else { throw RewindError("Can't read the video.") }
        reader.add(vOut)
        var compression: [String: Any] = [
            AVVideoMaxKeyFrameIntervalDurationKey: 2,
            AVVideoAllowFrameReorderingKey: true, // B-frames: smaller files, fine for a finished clip
        ]
        if quality >= 100, sourceBitrate > 0 {
            compression[AVVideoAverageBitRateKey] = Int(sourceBitrate) // match the original
        } else {
            compression[AVVideoQualityKey] = encoderQuality(min(quality, 99))
        }
        if fps > 0 { compression[AVVideoExpectedSourceFrameRateKey] = fps }
        if codec == .h264 { compression[AVVideoProfileLevelKey] = AVVideoProfileLevelH264HighAutoLevel }
        let vIn = AVAssetWriterInput(mediaType: .video, outputSettings: [
            AVVideoCodecKey: codec,
            AVVideoWidthKey: Int(natural.width),
            AVVideoHeightKey: Int(natural.height),
            AVVideoCompressionPropertiesKey: compression,
        ])
        vIn.transform = transform
        vIn.expectsMediaDataInRealTime = false
        guard writer.canAdd(vIn) else { throw RewindError("Can't write \(format.label) at this size.") }
        writer.add(vIn)

        // Audio: copied as-is (already AAC).
        var jobs: [(AVAssetReaderOutput, AVAssetWriterInput, Bool)] = [(vOut, vIn, true)]
        for track in aTracks {
            let out = AVAssetReaderTrackOutput(track: track, outputSettings: nil)
            out.alwaysCopiesSampleData = false
            let hint = try await track.load(.formatDescriptions).first
            let input = AVAssetWriterInput(mediaType: .audio, outputSettings: nil, sourceFormatHint: hint)
            input.expectsMediaDataInRealTime = false
            guard reader.canAdd(out), writer.canAdd(input) else { continue }
            reader.add(out)
            writer.add(input)
            jobs.append((out, input, false))
        }

        guard reader.startReading() else { throw reader.error ?? RewindError("Couldn't read the clip.") }
        guard writer.startWriting() else { throw writer.error ?? RewindError("Couldn't start writing.") }
        writer.startSession(atSourceTime: range.start)

        let total = max(range.duration.seconds, 0.001), start = range.start.seconds
        await withCheckedContinuation { (done: CheckedContinuation<Void, Never>) in
            let group = DispatchGroup()
            for (i, job) in jobs.enumerated() {
                let (output, input, isVideo) = job
                var finished = false
                var lastReport = 0.0
                group.enter()
                input.requestMediaDataWhenReady(on: DispatchQueue(label: "rewind.convert.\(i)")) {
                    guard !finished else { return }
                    while input.isReadyForMoreMediaData {
                        guard let sample = output.copyNextSampleBuffer(), writer.status == .writing, input.append(sample) else {
                            finished = true
                            input.markAsFinished()
                            group.leave()
                            return
                        }
                        if isVideo {
                            let p = min(max((sample.presentationTimeStamp.seconds - start) / total, 0), 1)
                            if p - lastReport >= 0.01 { lastReport = p; Task { @MainActor in progress(p) } }
                        }
                    }
                }
            }
            group.notify(queue: .global()) { done.resume() }
        }

        if reader.status == .failed { writer.cancelWriting(); throw reader.error ?? RewindError("Reading the clip failed.") }
        await writer.finishWriting()
        guard writer.status == .completed else { throw writer.error ?? RewindError("Writing the new file failed.") }
        try FileManager.default.moveItem(at: tmp, to: dest)
        Task { @MainActor in progress(1) } // don't wait for the main thread
    }
}
