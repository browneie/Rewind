import Foundation
import AVFoundation
import CoreMedia
import VideoToolbox

/// `Rewind --selftest <dir>`: exercises encoder → replay buffer → clip writer and the live recorder
/// with synthetic frames and audio, then inspects the resulting files. No screen permission needed.
enum SelfTest {
    static func run(dir: URL) -> Int32 {
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let clipURL = dir.appendingPathComponent("selftest-replay.mp4")
        let recURL = dir.appendingPathComponent("selftest-recording.mp4")
        try? FileManager.default.removeItem(at: clipURL)
        try? FileManager.default.removeItem(at: recURL)

        let fps = 60, w = 1280, h = 720, seconds = 5
        let router = MediaRouter()
        router.replay.duration = 3
        router.bufferReplay = true
        let recorder = LiveRecorder(url: recURL, tracks: [.system, .mic])
        router.recorder = recorder

        let encoder: VideoEncoder
        do {
            encoder = try VideoEncoder(width: w, height: h, fps: fps, codec: .h264, bitrate: 8_000_000) { router.video($0) }
        } catch { print("FAIL encoder: \(error)"); return 1 }
        recorder.requestKeyframe = { encoder.forceKeyframe() }
        guard let pool = encoder.pixelBufferPool else { print("FAIL no pool"); return 1 }

        // SCK-style audio: 48 kHz float32 non-interleaved stereo.
        var asbd = AudioStreamBasicDescription(
            mSampleRate: 48_000, mFormatID: kAudioFormatLinearPCM,
            mFormatFlags: kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked | kAudioFormatFlagIsNonInterleaved,
            mBytesPerPacket: 4, mFramesPerPacket: 1, mBytesPerFrame: 4, mChannelsPerFrame: 2,
            mBitsPerChannel: 32, mReserved: 0)
        var afmt: CMAudioFormatDescription?
        CMAudioFormatDescriptionCreate(allocator: nil, asbd: &asbd, layoutSize: 0, layout: nil,
                                       magicCookieSize: 0, magicCookie: nil, extensions: nil, formatDescriptionOut: &afmt)
        let chunk = 1024
        var audioFrame: Int64 = 0
        let base = CMClockGetTime(CMClockGetHostTimeClock())

        for i in 0..<(fps * seconds) {
            let pts = base + CMTime(value: CMTimeValue(i), timescale: CMTimeScale(fps))
            var pb: CVPixelBuffer?
            CVPixelBufferPoolCreatePixelBuffer(nil, pool, &pb)
            guard let pb else { print("FAIL pixel buffer"); return 1 }
            fill(pb, luma: UInt8(16 + (i * 3) % 220))
            encoder.encode(pb, pts: pts)

            while Double(audioFrame) / 48_000 < Double(i + 1) / Double(fps) {
                let apts = base + CMTime(value: audioFrame, timescale: 48_000)
                if let s = makeAudio(fmt: afmt!, frames: chunk, startFrame: audioFrame, pts: apts, hz: 440)?.deepCopyAudio(),
                   let m = makeAudio(fmt: afmt!, frames: chunk, startFrame: audioFrame, pts: apts, hz: 660)?.deepCopyAudio() {
                    router.audio(s, .system)
                    router.audio(m, .mic)
                } else { print("FAIL audio copy"); return 1 }
                audioFrame += Int64(chunk)
            }
            usleep(UInt32(1_000_000 / fps)) // feed in real time for the live recorder
        }
        encoder.flush()

        let buffered = router.replay.bufferedSeconds
        let done = DispatchSemaphore(value: 0)
        nonisolated(unsafe) var failed = false
        ClipWriter.write(router.replay.snapshot(), to: clipURL) { r in
            if case .failure(let e) = r { print("FAIL clip: \(e)"); failed = true }
            done.signal()
        }
        done.wait()
        router.recorder = nil
        recorder.finish { r in
            if case .failure(let e) = r { print("FAIL recording: \(e)"); failed = true }
            done.signal()
        }
        done.wait()
        if failed { return 1 }

        print(String(format: "buffered %.2fs (replay length 3s)", buffered))
        for url in [clipURL, recURL] { if !inspect(url, audioTracks: 2) { failed = true } }

        // Mixdown: both files should end up with one audio track, same length, in sync.
        for url in [clipURL, recURL] {
            let sem = DispatchSemaphore(value: 0)
            nonisolated(unsafe) var mixError: Error?
            Task { do { try await AudioMixdown.mixIfNeeded(url) } catch { mixError = error }; sem.signal() }
            sem.wait()
            if let mixError { print("FAIL mixdown \(url.lastPathComponent): \(mixError)"); failed = true; continue }
            print("after mixdown:", terminator: " ")
            if !inspect(url, audioTracks: 1) { failed = true }
        }
        if !testTrim(recURL) { failed = true }
        if !testCleanup(dir: dir) { failed = true }
        return failed ? 1 : 0
    }

    /// Cutting 1.2s–3.7s out of the 5s recording should give a 2.5s clip, without re-encoding.
    private static func testTrim(_ source: URL) -> Bool {
        let dest = ClipTrimmer.outputURL(for: source)
        try? FileManager.default.removeItem(at: dest)
        let range = CMTimeRange(start: CMTime(seconds: 1.2, preferredTimescale: 600),
                                end: CMTime(seconds: 3.7, preferredTimescale: 600))
        let sem = DispatchSemaphore(value: 0)
        nonisolated(unsafe) var trimError: Error?
        nonisolated(unsafe) var length = 0.0
        Task {
            do {
                try await ClipTrimmer.trim(source, range: range, to: dest)
                length = try await AVURLAsset(url: dest).load(.duration).seconds
            } catch { trimError = error }
            sem.signal()
        }
        sem.wait()
        if let trimError { print("FAIL trim: \(trimError)"); return false }
        print(String(format: "trimmed to %.2fs (wanted 2.50s) → ", length), terminator: "")
        var ok = inspect(dest, audioTracks: 1)
        if abs(length - 2.5) > 0.1 { print("FAIL trim length"); ok = false }
        try? FileManager.default.removeItem(at: dest)
        return ok
    }

    /// Only Rewind's own files older than the limit go; everything else stays.
    private static func testCleanup(dir: URL) -> Bool {
        let fm = FileManager.default
        let d = dir.appendingPathComponent("cleanup-test")
        try? fm.removeItem(at: d)
        try? fm.createDirectory(at: d, withIntermediateDirectories: true)
        let now = Date()
        let files: [(String, Double, Bool)] = [ // name, age in days, should be removed
            ("Replay 2026-01-01 at 10.00.00.mp4", 20, true),
            ("Recording 2026-01-01 at 10.00.00.mp4", 15, true),
            ("Screenshot 2026-01-01 at 10.00.00.png", 30, true),
            ("Replay 2026-01-10 at 10.00.00.mp4", 13, false),
            ("Recording in progress.mp4", 40, false), // passed as `keep`
            ("Holiday video.mp4", 99, false),
            ("Replay notes.txt", 99, false),
            (".mixing-ABC.mp4", 99, false),
            ("ROBLOX 2026-01-01 at 10.00.00 (trimmed).mp4", 20, true), // app name Rewind used
            ("ROBLOX notes.mp4", 99, false),                         // no date stamp
            ("MINECRAFT 2026-01-01 at 10.00.00.mp4", 99, false),     // app name Rewind never used
        ]
        for (name, age, _) in files {
            let url = d.appendingPathComponent(name)
            fm.createFile(atPath: url.path, contents: Data(count: 1000))
            let date = now.addingTimeInterval(-age * 86_400)
            try? fm.setAttributes([.creationDate: date, .modificationDate: date], ofItemAtPath: url.path)
        }
        let r = Cleanup.run(in: d, olderThan: 14, now: now,
                            keep: [d.appendingPathComponent("Recording in progress.mp4")], appPrefixes: ["ROBLOX"],
                            remove: { try fm.removeItem(at: $0) })
        var ok = r.count == 4 && r.bytes == 4000
        let names: [(String, String?)] = [("ROBLOX 2026-11-13 at 16.53.58.mp4", "ROBLOX"),
                                          ("SEA OF THIEVES 2026-11-13 at 16.53.58 (2).png", "SEA OF THIEVES"),
                                          ("Replay 2026-11-13 at 16.53.58.mp4", nil), ("Holiday.mp4", nil)]
        for (name, want) in names where Cleanup.appPrefix(of: name) != want {
            print("FAIL app name of \(name): \(Cleanup.appPrefix(of: name) ?? "nil")"); ok = false
        }
        for (name, _, gone) in files where fm.fileExists(atPath: d.appendingPathComponent(name).path) == gone {
            print("FAIL cleanup: \(name) should \(gone ? "be removed" : "stay")"); ok = false
        }
        print("cleanup: removed \(r.count) of \(files.count) files (\(r.bytes) bytes)\(ok ? "" : " FAIL")")
        try? fm.removeItem(at: d)
        return ok
    }

    private static func fill(_ pb: CVPixelBuffer, luma: UInt8) {
        CVPixelBufferLockBaseAddress(pb, [])
        defer { CVPixelBufferUnlockBaseAddress(pb, []) }
        for plane in 0..<CVPixelBufferGetPlaneCount(pb) {
            guard let p = CVPixelBufferGetBaseAddressOfPlane(pb, plane) else { continue }
            memset(p, Int32(plane == 0 ? luma : 128),
                   CVPixelBufferGetBytesPerRowOfPlane(pb, plane) * CVPixelBufferGetHeightOfPlane(pb, plane))
        }
    }

    private static func makeAudio(fmt: CMAudioFormatDescription, frames: Int, startFrame: Int64, pts: CMTime, hz: Float) -> CMSampleBuffer? {
        let abl = AudioBufferList.allocate(maximumBuffers: 2)
        defer { free(abl.unsafeMutablePointer) }
        var storage: [UnsafeMutablePointer<Float>] = []
        for ch in 0..<2 {
            let p = UnsafeMutablePointer<Float>.allocate(capacity: frames)
            for n in 0..<frames { p[n] = 0.2 * sin(2 * .pi * hz * Float(startFrame + Int64(n)) / 48_000) }
            storage.append(p)
            abl[ch] = AudioBuffer(mNumberChannels: 1, mDataByteSize: UInt32(frames * 4), mData: p)
        }
        defer { storage.forEach { $0.deallocate() } }
        var timing = CMSampleTimingInfo(duration: CMTime(value: 1, timescale: 48_000), presentationTimeStamp: pts, decodeTimeStamp: .invalid)
        var sb: CMSampleBuffer?
        CMSampleBufferCreate(allocator: nil, dataBuffer: nil, dataReady: false, makeDataReadyCallback: nil, refcon: nil,
                             formatDescription: fmt, sampleCount: frames, sampleTimingEntryCount: 1, sampleTimingArray: &timing,
                             sampleSizeEntryCount: 0, sampleSizeArray: nil, sampleBufferOut: &sb)
        guard let sb, CMSampleBufferSetDataBufferFromAudioBufferList(sb, blockBufferAllocator: nil, blockBufferMemoryAllocator: nil,
                                                                      flags: 0, bufferList: abl.unsafePointer) == noErr else { return nil }
        return sb
    }

    private static func inspect(_ url: URL, audioTracks: Int) -> Bool {
        let sem = DispatchSemaphore(value: 0)
        var ok = false
        Task {
            do {
                let asset = AVURLAsset(url: url)
                let dur = try await asset.load(.duration).seconds
                let v = try await asset.loadTracks(withMediaType: .video)
                let a = try await asset.loadTracks(withMediaType: .audio)
                var frames = 0
                if let vt = v.first {
                    let reader = try AVAssetReader(asset: asset)
                    let out = AVAssetReaderTrackOutput(track: vt, outputSettings: [kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange])
                    reader.add(out); reader.startReading()
                    while out.copyNextSampleBuffer() != nil { frames += 1 }
                    if reader.status == .failed { throw reader.error! }
                }
                var audioDur = 0.0, videoDur = 0.0
                if let at = a.first { audioDur = try await at.load(.timeRange).duration.seconds }
                if let vt = v.first { videoDur = try await vt.load(.timeRange).duration.seconds }
                let size = (try? FileManager.default.attributesOfItem(atPath: url.path)[.size] as? Int) ?? 0
                print(String(format: "%@: %.2fs, %d video track(s), %d decoded frames, %d audio track(s) (audio %.2fs / video %.2fs), %d KB",
                             url.lastPathComponent, dur, v.count, frames, a.count, audioDur, videoDur, size / 1024))
                ok = v.count == 1 && a.count == audioTracks && frames > 0 && dur > 0.5 && abs(audioDur - videoDur) < 0.25
            } catch { print("FAIL inspect \(url.lastPathComponent): \(error)") }
            sem.signal()
        }
        sem.wait()
        return ok
    }
}
