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
        let discord = AudioTrack.app("Discord") // an app with a track of its own (AAC, like AppTap's)
        let recorder = LiveRecorder(url: recURL, tracks: [.system, .mic, discord])
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
        let pcmFormat = AVAudioFormat(cmAudioFormatDescription: afmt!)
        guard let appEncoder = try? AACTrackEncoder(input: pcmFormat) else { print("FAIL AAC encoder"); return 1 }
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
                    if let app = makeAudio(fmt: afmt!, frames: chunk, startFrame: audioFrame, pts: apts, hz: 880),
                       let pcm = AVAudioPCMBuffer(pcmFormat: pcmFormat, frameCapacity: AVAudioFrameCount(chunk)) {
                        pcm.frameLength = AVAudioFrameCount(chunk)
                        CMSampleBufferCopyPCMDataIntoAudioBufferList(app, at: 0, frameCount: Int32(chunk), into: pcm.mutableAudioBufferList)
                        for sample in appEncoder.encode(pcm, pts: apts) { router.audio(sample, discord) }
                    }
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
        for url in [clipURL, recURL] {
            if !inspect(url, audioTracks: 3) || !checkNames(url, ["All sound", "Mic", "Discord"]) { failed = true }
        }

        // Mixing the mic in: the first track becomes "All sound + Mic", the mic and the app keep their own.
        for url in [clipURL, recURL] {
            let sem = DispatchSemaphore(value: 0)
            nonisolated(unsafe) var mixError: Error?
            Task { do { try await SoundTracks.mixMic(url) } catch { mixError = error }; sem.signal() }
            sem.wait()
            if let mixError { print("FAIL mixing the mic in \(url.lastPathComponent): \(mixError)"); failed = true; continue }
            print("after mixing the mic in:", terminator: " ")
            if !inspect(url, audioTracks: 3) || !checkNames(url, ["All sound + Mic", "Mic", "Discord"]) { failed = true }
        }
        if !testSounds(recURL) { failed = true }
        if !testTrim(recURL) { failed = true }
        if !testConvert(recURL) { failed = true }
        if !testShare(recURL) { failed = true }
        if !testShrink(recURL) { failed = true }
        if !testCleanup(dir: dir) { failed = true }
        if !testGames(dir: dir) { failed = true }
        if !testRename(dir: dir) { failed = true }
        return failed ? 1 : 0
    }

    /// Re-encoding the recording to H.264 and HEVC at low, middle and top quality: every file intact and
    /// full length. (The synthetic frames are too plain to compare sizes; that was checked on real clips.)
    private static func testConvert(_ source: URL) -> Bool {
        var ok = true
        for format in [ExportFormat.h264, .hevc] {
            for q in [0, 65, 100] {
                let dest = ClipTrimmer.outputURL(for: source, label: "\(format.label) q\(q)")
                try? FileManager.default.removeItem(at: dest)
                let sem = DispatchSemaphore(value: 0)
                nonisolated(unsafe) var err: Error?
                Task {
                    do {
                        let d = try await AVURLAsset(url: source).load(.duration)
                        try await ClipConverter.convert(source, range: CMTimeRange(start: .zero, duration: d), format: format,
                                                        quality: q, to: dest) { _ in }
                    } catch { err = error }
                    sem.signal()
                }
                sem.wait()
                if let err { print("FAIL convert \(format.label) q\(q): \(err)"); ok = false; continue }
                print("convert \(format.label) q\(q) (\(ClipConverter.word(for: q))) → ", terminator: "")
                if !inspect(dest, audioTracks: 3) { ok = false }
                try? FileManager.default.removeItem(at: dest)
            }
        }
        return ok
    }

    /// Copy's fit-to-size: the size steps for a low bitrate, a shrunk copy under a limit, and the scale-down.
    private static func testShare(_ source: URL) -> Bool {
        var ok = true
        let stepped = ClipConverter.outputSize(CGSize(width: 2560, height: 1600), bitrate: 2_000_000, fps: 60)
        if stepped != CGSize(width: 1152, height: 720) { print("FAIL share: 2560×1600 at 2 Mbps → \(stepped), expected 1152×720"); ok = false }
        let sem = DispatchSemaphore(value: 0)
        nonisolated(unsafe) var result: Result<(URL, Int64, CGSize), Error> = .failure(RewindError("didn't run"))
        Task {
            do {
                let asset = AVURLAsset(url: source)
                let seconds = try await asset.load(.duration).seconds
                var audio = 0.0
                for t in try await asset.loadTracks(withMediaType: .audio) { audio += Double(try await t.load(.estimatedDataRate)) }
                // A limit that leaves about 100 kbps for video: low enough to force a scale-down.
                let limit = Int64((max(audio, 0) + 160_000) * seconds / 8 / 0.92)
                let out = try await ClipSharer.fit(source, limit: limit) { _ in }
                let size = try await AVURLAsset(url: out).loadTracks(withMediaType: .video).first!.load(.naturalSize)
                result = .success((out, limit, size))
            } catch { result = .failure(error) }
            sem.signal()
        }
        sem.wait()
        switch result {
        case .failure(let e): print("FAIL share fit: \(e)"); ok = false
        case .success(let (out, limit, size)):
            let bytes = ClipSharer.fileSize(out)
            print("share fit → \(bytes) bytes (limit \(limit)), \(Int(size.width))×\(Int(size.height)): ", terminator: "")
            if bytes > limit { print("FAIL over the limit"); ok = false }
            else if !inspect(out, audioTracks: 1) { ok = false }
            try? FileManager.default.removeItem(at: out)
        }
        return ok
    }

    /// Free up space: an old clip is listed, then shrunk to HEVC in place (same name, its sound tracks and game
    /// kept, the original "in the Trash"), and not listed again.
    private static func testShrink(_ source: URL) -> Bool {
        let fm = FileManager.default
        let d = source.deletingLastPathComponent().appendingPathComponent("shrink-test")
        try? fm.removeItem(at: d)
        try? fm.createDirectory(at: d.appendingPathComponent("trash"), withIntermediateDirectories: true)
        let url = d.appendingPathComponent("Replay 2026-01-01 at 10.00.00.mp4")
        try? fm.copyItem(at: source, to: url)
        ClipTags.set(url, .game, "Minecraft")
        let old = Date().addingTimeInterval(-30 * 86_400)
        try? fm.setAttributes([.creationDate: old, .modificationDate: old], ofItemAtPath: url.path)
        let before = ClipStorage.scan(d, prefixes: [], busy: nil)
        let trash = d.appendingPathComponent("trash")
        let result: Result<Int64, Error> = wait {
            do {
                // The synthetic clip barely shrinks, so any new file counts here: this checks the swap.
                return .success(try await ClipStorage.shrinkOne(url, worthIt: 100) { u in
                    let to = trash.appendingPathComponent(u.lastPathComponent)
                    try FileManager.default.moveItem(at: u, to: to)
                    return to
                })
            } catch { return .failure(error) }
        }
        let after = ClipStorage.scan(d, prefixes: [], busy: nil)
        defer { try? fm.removeItem(at: d) }
        switch result {
        case .failure(let e): print("FAIL shrink: \(e)"); return false
        case .success(var saved):
            saved = max(saved, fm.fileExists(atPath: trash.appendingPathComponent(url.lastPathComponent).path) ? 1 : 0)
            let codec = wait { () -> String in
                let t = try? await AVURLAsset(url: url).loadTracks(withMediaType: .video).first
                let f = try? await t?.load(.formatDescriptions).first
                return f.map { ClipLibrary.codecName(CMFormatDescriptionGetMediaSubType($0)) } ?? "?"
            }
            print("shrink: \(before.candidates.count) candidate, saved \(saved / 1024) KB, now \(codec), " +
                  "original kept in trash: \(fm.fileExists(atPath: trash.appendingPathComponent(url.lastPathComponent).path)), " +
                  "candidates after: \(after.candidates.count) → ", terminator: "")
            // A clip that wouldn't get much smaller is left as it is (and not offered again); either way it's not listed again.
            var ok = before.candidates.count == 1 && after.candidates.isEmpty && fm.fileExists(atPath: url.path)
            if saved > 0 {
                ok = ok && codec == "HEVC" && ClipTags.get(url, .game) == "Minecraft"
                    && fm.fileExists(atPath: trash.appendingPathComponent(url.lastPathComponent).path)
                ok = inspect(url, audioTracks: 3) && checkNames(url, ["All sound + Mic", "Mic", "Discord"]) && ok
            } else { print("kept as it was") }
            if !ok { print("FAIL shrink") }
            return ok
        }
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
        var ok = inspect(dest, audioTracks: 3) && checkNames(dest, ["All sound + Mic", "Mic", "Discord"])
        if abs(length - 2.5) > 0.1 { print("FAIL trim length"); ok = false }
        try? FileManager.default.removeItem(at: dest)
        return ok
    }

    /// Only Rewind's own files older than the limit go; everything else stays.
    /// Game grouping: clips tagged with a game (or named after an app) are listed under it, the rest under Other.
    private static func testGames(dir: URL) -> Bool {
        let fm = FileManager.default
        let d = dir.appendingPathComponent("games-test")
        try? fm.removeItem(at: d)
        try? fm.createDirectory(at: d, withIntermediateDirectories: true)
        let files: [(String, String?)] = [
            ("Replay 2026-01-03 at 10.00.00.mp4", "Minecraft"),
            ("Roblox 2026-01-02 at 10.00.00.mp4", nil), // named after the app
            ("Replay 2026-01-01 at 10.00.00.mp4", nil),
        ]
        for (i, (name, game)) in files.enumerated() {
            let url = d.appendingPathComponent(name)
            fm.createFile(atPath: url.path, contents: Data([0]))
            try? fm.setAttributes([.creationDate: Date().addingTimeInterval(-Double(i) * 60)], ofItemAtPath: url.path)
            ClipTags.set(url, .game, game)
        }
        let all = ClipLibrary.recent(in: d, excluding: nil, appPrefixes: ["Roblox"])
        let roblox = ClipLibrary.recent(in: d, excluding: nil, appPrefixes: ["Roblox"], game: "Roblox").items.map(\.url.lastPathComponent)
        let other = ClipLibrary.recent(in: d, excluding: nil, appPrefixes: ["Roblox"], game: ClipLibrary.other).items.map(\.url.lastPathComponent)
        print("games: \(all.games) other: \(all.hasOther); Roblox → \(roblox); Other → \(other)")
        try? fm.removeItem(at: d)
        let ok = all.games == ["Minecraft", "Roblox"] && all.hasOther && roblox == [files[1].0] && other == [files[2].0]
        if !ok { print("FAIL games") }
        return ok
    }

    /// A renamed clip is still listed as a Rewind replay of its game, shows its new name, and cleanup keeps it.
    private static func testRename(dir: URL) -> Bool {
        let fm = FileManager.default
        let d = dir.appendingPathComponent("rename-test")
        try? fm.removeItem(at: d)
        try? fm.createDirectory(at: d, withIntermediateDirectories: true)
        let url = d.appendingPathComponent("Replay 2026-01-01 at 10.00.00.mp4")
        fm.createFile(atPath: url.path, contents: Data([0]))
        ClipTags.set(url, .game, "Minecraft")
        let renamed = MainActor.assumeIsolated {
            ClipLibrary.shared.rename(ClipItem(url: url, date: Date()), to: "insane: clutch")
        }
        let listed = ClipLibrary.recent(in: d, excluding: nil).items.first
        let removed = Cleanup.run(in: d, olderThan: 0, now: Date().addingTimeInterval(86_400 * 30)).count
        print("renamed → \(renamed?.lastPathComponent ?? "nil"): \(listed?.title ?? "not listed"), \(listed.map { "\($0.kind)" } ?? "-"), " +
              "game \(listed?.game ?? "none"), cleanup removed \(removed)")
        // (a renamed clip's name and game live on the file, so they're checked before it's removed)
        let ok = renamed?.lastPathComponent == "insane- clutch.mp4" && listed?.title == "insane- clutch" && listed?.kind == .replay
            && listed?.game == "Minecraft" && removed == 0
        try? fm.removeItem(at: d)
        if !ok { print("FAIL rename") }
        return ok
    }

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
            ("Roblox 2026-01-01 at 10.00.00 (trimmed).mp4", 20, true), // app name Rewind used
            ("Roblox notes.mp4", 99, false),                         // no date stamp
            ("Minecraft 2026-01-01 at 10.00.00.mp4", 99, false),     // app name Rewind never used
        ]
        for (name, age, _) in files {
            let url = d.appendingPathComponent(name)
            fm.createFile(atPath: url.path, contents: Data(count: 1000))
            let date = now.addingTimeInterval(-age * 86_400)
            try? fm.setAttributes([.creationDate: date, .modificationDate: date], ofItemAtPath: url.path)
        }
        let r = Cleanup.run(in: d, olderThan: 14, now: now,
                            keep: [d.appendingPathComponent("Recording in progress.mp4")], appPrefixes: ["Roblox"],
                            remove: { try fm.removeItem(at: $0) })
        var ok = r.count == 4 && r.bytes == 4000
        let names: [(String, String?)] = [("Roblox 2026-11-13 at 16.53.58.mp4", "Roblox"),
                                          ("Sea of Thieves 2026-11-13 at 16.53.58 (2).png", "Sea of Thieves"),
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

    /// The trimmer's Sound row on a clip with the clip's own sound + mic, the mic, and Discord: the chips it
    /// offers, and saving only the mic, only Discord, or the two mixed.
    private static func testSounds(_ source: URL) -> Bool {
        var ok = true
        let names = wait { await SoundTracks.names(of: source) }
        let sources = SoundTracks.sources(names)
        let shown = sources.map { "\($0.name)\($0.on ? "" : " (off)")" }
        print("sound chips:", shown.joined(separator: ", "))
        if shown != ["Discord", "Mic"] { print("FAIL sound chips"); ok = false }
        if SoundTracks.choose(sources, on: [1, 2]) != nil { print("FAIL as recorded should keep every track"); ok = false }
        // [1, 2] is "as recorded" in the trimmer; mixing two tracks is checked through the plan directly.
        for (on, want) in [([1], ["Mic"]), ([2], ["Discord"]), ([1, 2], ["Mic + Discord"]), ([], [])] as [([Int], [String])] {
            let choice = on.count == 2 ? on : SoundTracks.choose(sources, on: Set(on)) ?? [-1]
            let dest = source.deletingLastPathComponent().appendingPathComponent("selftest-sound-\(want.first ?? "none").mp4")
            try? FileManager.default.removeItem(at: dest)
            let err: Error? = wait {
                do { try await SoundTracks.rewrite(source, to: dest, plan: SoundTracks.plan(for: choice, names: names)); return nil }
                catch { return error }
            }
            if let err { print("FAIL saving sound \(want): \(err)"); ok = false; continue }
            print("sound \(want.isEmpty ? "none" : want[0]):", terminator: " ")
            if !inspect(dest, audioTracks: want.count, allowNoAudio: want.isEmpty) || !checkNames(dest, want) { ok = false }
            try? FileManager.default.removeItem(at: dest)
        }
        return ok
    }

    private static func checkNames(_ url: URL, _ want: [String]) -> Bool {
        let names = wait { await SoundTracks.names(of: url) }
        if names != want { print("FAIL track names \(names), wanted \(want)"); return false }
        return true
    }

    private static func wait<T>(_ work: @escaping @Sendable () async -> T) -> T {
        let sem = DispatchSemaphore(value: 0)
        nonisolated(unsafe) var result: T?
        Task { result = await work(); sem.signal() }
        sem.wait()
        return result!
    }

    private static func inspect(_ url: URL, audioTracks: Int, allowNoAudio: Bool = false) -> Bool {
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
                ok = v.count == 1 && a.count == audioTracks && frames > 0 && dur > 0.5 && (allowNoAudio && a.isEmpty || abs(audioDur - videoDur) < 0.25)
            } catch { print("FAIL inspect \(url.lastPathComponent): \(error)") }
            sem.signal()
        }
        sem.wait()
        return ok
    }
}
