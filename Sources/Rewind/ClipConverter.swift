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
    /// With `bitrate` (bits per second), `quality` is ignored: the video is encoded at that average bitrate and
    /// scaled down when it's too low for the clip's size (for fitting a size limit, see ClipSharer).
    /// `audio`: which sound tracks to keep (indexes; nil keeps every one, with their names).
    static func convert(_ source: URL, range: CMTimeRange, format: ExportFormat, quality: Int, to dest: URL,
                        bitrate: Int? = nil, audio: [Int]? = nil, progress: @escaping @MainActor (Double) -> Void) async throws {
        guard let codec = format.codecType else { throw RewindError("Nothing to convert to.") }
        let asset = AVURLAsset(url: source)
        guard let vTrack = try await asset.loadTracks(withMediaType: .video).first else {
            throw RewindError("This clip has no video.")
        }
        let allAudio = try await asset.loadTracks(withMediaType: .audio)
        let aTracks = audio.map { keep in keep.compactMap { allAudio.indices.contains($0) ? allAudio[$0] : nil } } ?? allAudio
        let names = await SoundTracks.names(of: source)
        let kept = audio ?? Array(allAudio.indices)
        let natural = try await vTrack.load(.naturalSize)
        let transform = try await vTrack.load(.preferredTransform)
        let fps = try await vTrack.load(.nominalFrameRate)
        let sourceBitrate = try await vTrack.load(.estimatedDataRate)

        if quality >= 100, bitrate == nil, audio == nil {
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
        let size = bitrate.map { outputSize(natural, bitrate: $0, fps: Double(fps)) } ?? natural
        var readSettings: [String: Any] = [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange,
        ]
        if size != natural { // the decoder scales it down
            readSettings[kCVPixelBufferWidthKey as String] = Int(size.width)
            readSettings[kCVPixelBufferHeightKey as String] = Int(size.height)
        }
        let vOut = AVAssetReaderTrackOutput(track: vTrack, outputSettings: readSettings)
        vOut.alwaysCopiesSampleData = false
        guard reader.canAdd(vOut) else { throw RewindError("Can't read the video.") }
        reader.add(vOut)
        var compression: [String: Any] = [
            AVVideoMaxKeyFrameIntervalDurationKey: 2,
            AVVideoAllowFrameReorderingKey: true, // B-frames: smaller files, fine for a finished clip
        ]
        if let bitrate {
            compression[AVVideoAverageBitRateKey] = bitrate
        } else if quality >= 100, sourceBitrate > 0 {
            compression[AVVideoAverageBitRateKey] = Int(sourceBitrate) // match the original
        } else {
            compression[AVVideoQualityKey] = encoderQuality(min(quality, 99))
        }
        if fps > 0 { compression[AVVideoExpectedSourceFrameRateKey] = fps }
        if codec == .h264 { compression[AVVideoProfileLevelKey] = AVVideoProfileLevelH264HighAutoLevel }
        let vIn = AVAssetWriterInput(mediaType: .video, outputSettings: [
            AVVideoCodecKey: codec,
            AVVideoWidthKey: Int(size.width),
            AVVideoHeightKey: Int(size.height),
            AVVideoCompressionPropertiesKey: compression,
        ])
        vIn.transform = transform
        vIn.expectsMediaDataInRealTime = false
        guard writer.canAdd(vIn) else { throw RewindError("Can't write \(format.label) at this size.") }
        writer.add(vIn)

        // Audio: copied as-is (already AAC), with the tracks' names; only the first plays by default.
        var jobs: [(AVAssetReaderOutput, AVAssetWriterInput, Bool)] = [(vOut, vIn, true)]
        var audioInputs: [AVAssetWriterInput] = []
        for (n, track) in aTracks.enumerated() {
            let out = AVAssetReaderTrackOutput(track: track, outputSettings: nil)
            out.alwaysCopiesSampleData = false
            let hint = try await track.load(.formatDescriptions).first
            let input = AVAssetWriterInput(mediaType: .audio, outputSettings: nil, sourceFormatHint: hint)
            input.expectsMediaDataInRealTime = false
            if kept.indices.contains(n), names.indices.contains(kept[n]) { input.metadata = SoundTracks.metadata(name: names[kept[n]]) }
            guard reader.canAdd(out), writer.canAdd(input) else { continue }
            reader.add(out)
            writer.add(input)
            audioInputs.append(input)
            jobs.append((out, input, false))
        }
        SoundTracks.group(audioInputs, in: writer)

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

    /// The clip's size, or smaller (1440p, 1080p, 720p, 480p) while `bitrate` is under about 0.04 bits per pixel
    /// per frame, below which H.264 turns to blocks. Even numbers, same shape.
    static func outputSize(_ natural: CGSize, bitrate: Int, fps: Double) -> CGSize {
        var w = natural.width, h = natural.height
        let f = fps > 0 ? fps : 60
        for target in [1440.0, 1080, 720, 480] where min(w, h) > target && Double(bitrate) / (w * h * f) < 0.04 {
            let s = target / min(w, h)
            w = (w * s / 2).rounded() * 2
            h = (h * s / 2).rounded() * 2
        }
        return CGSize(width: w, height: h)
    }
}
