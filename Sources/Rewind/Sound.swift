import Foundation
import AVFoundation
import CoreMedia

/// What sound tracks are called in the files Rewind writes (the same names as the Windows app). The first
/// track is the clip's own sound: "All sound" (or the captured app's name), with " + Mic" when the microphone
/// is mixed in. Then "Mic" and one track per app ("Discord", "Spotify"). Only the first plays by default.
enum SoundNames {
    static let all = "All sound", mic = "Mic", withMic = " + Mic"
}

/// A sound the trimmer offers: a track of the clip, and whether it's part of the clip's own sound.
struct ClipSound: Identifiable, Equatable {
    let name: String
    let track: Int
    let on: Bool
    let isMic: Bool
    var id: Int { track }
}

enum SoundTracks {
    // MARK: Names

    /// Each audio track's name, in order ("" for tracks without one, like clips from before names).
    static func names(of url: URL) async -> [String] {
        let asset = AVURLAsset(url: url)
        guard let tracks = try? await asset.loadTracks(withMediaType: .audio) else { return [] }
        var names: [String] = []
        for t in tracks {
            var name = ""
            for item in (try? await t.load(.metadata)) ?? [] where isName(item) {
                name = (try? await item.load(.stringValue)) ?? ""
            }
            names.append(name)
        }
        return names
    }

    /// AVFoundation writes a track name into an MP4 as ISO user data "titl".
    private static func isName(_ item: AVMetadataItem) -> Bool {
        item.identifier == .quickTimeUserDataTrackName || item.identifier?.rawValue == "uiso/titl"
    }

    static func metadata(name: String) -> [AVMetadataItem] {
        guard !name.isEmpty else { return [] }
        let m = AVMutableMetadataItem()
        m.identifier = .quickTimeUserDataTrackName
        m.value = name as NSString
        m.dataType = kCMMetadataBaseDataType_UTF8 as String
        return [m]
    }

    /// Puts several audio inputs in one group so only the first plays by default (the rest are alternatives),
    /// and players that play every enabled track don't play the same sound twice.
    static func group(_ inputs: [AVAssetWriterInput], in writer: AVAssetWriter) {
        guard inputs.count > 1 else { return }
        let g = AVAssetWriterInputGroup(inputs: inputs, defaultInput: inputs[0])
        if writer.canAdd(g) { writer.add(g) }
    }

    // MARK: The trimmer's Sound row

    /// The sounds the trimmer offers for a clip with these track names, the ones in its own sound switched on.
    static func sources(_ names: [String]) -> [ClipSound] {
        var list: [ClipSound] = []
        guard !names.isEmpty else { return list }
        if names.allSatisfy(\.isEmpty) {
            // Clips from before tracks had names: the first is the sound, a second one the microphone.
            list.append(ClipSound(name: "Sound", track: 0, on: true, isMic: false))
            for i in 1..<names.count {
                list.append(names.count == 2 ? ClipSound(name: SoundNames.mic, track: i, on: false, isMic: true)
                                             : ClipSound(name: "Track \(i + 1)", track: i, on: false, isMic: false))
            }
            return list
        }
        let mic = names.indices.dropFirst().first { names[$0] == SoundNames.mic }
        let mixed = mic != nil && names[0].hasSuffix(SoundNames.withMic)
        let main = mixed ? String(names[0].dropLast(SoundNames.withMic.count)) : names[0]
        let others = names.indices.dropFirst().filter { $0 != mic }
        if others.isEmpty {
            if !mixed { list.append(ClipSound(name: main, track: 0, on: true, isMic: false)) }
        } else {
            // The captured app has no track of its own on the Mac (its sound is the clip's own): offer that too.
            if main != SoundNames.all, !others.contains(where: { names[$0] == main }) {
                list.append(ClipSound(name: main, track: 0, on: true, isMic: false))
            }
            let all = main == SoundNames.all
            for i in others { list.append(ClipSound(name: names[i], track: i, on: all || names[i] == main, isMic: false)) }
        }
        if let mic { list.append(ClipSound(name: SoundNames.mic, track: mic, on: mixed || list.isEmpty, isMic: true)) }
        return list
    }

    /// The tracks to save: nil keeps the clip's sound as it is (every track), empty saves no sound,
    /// several are mixed into one.
    static func choose(_ sources: [ClipSound], on: Set<Int>) -> [Int]? {
        if sources.allSatisfy({ $0.on == on.contains($0.track) }) { return nil }
        return sources.filter { on.contains($0.track) }.map(\.track)
    }

    /// The plan `rewrite` follows for a trimmer choice.
    static func plan(for choice: [Int], names: [String]) -> [(sources: [Int], name: String)] {
        switch choice.count {
        case 0: return []
        case 1: return [([choice[0]], names.indices.contains(choice[0]) ? names[choice[0]] : "")]
        default:
            let picked = choice.compactMap { names.indices.contains($0) && !names[$0].isEmpty ? names[$0] : nil }
            return [(choice, picked.joined(separator: " + "))]
        }
    }

    // MARK: Rewriting

    /// Folds the microphone into the clip's own sound when that setting is on: the first track becomes
    /// "<sound> + Mic", and the microphone keeps a track of its own as well. Rewrites `url` in place.
    static func mixMic(_ url: URL) async throws {
        let names = await names(of: url)
        guard names.count > 1, let mic = names.indices.dropFirst().first(where: { names[$0] == SoundNames.mic }),
              !names[0].hasSuffix(SoundNames.withMic) else { return }
        var plan: [(sources: [Int], name: String)] = [([0, mic], names[0] + SoundNames.withMic), ([mic], SoundNames.mic)]
        for i in names.indices.dropFirst() where i != mic { plan.append(([i], names[i])) }
        try await rewrite(url, to: url, plan: plan)
    }

    /// Writes `source`'s video (copied as-is) and the audio tracks in `plan` to `dest` (which may be `source`).
    /// Each entry lists source audio track indexes: one is copied as it is, several are mixed and encoded to AAC.
    /// Tracks get their names, and only the first plays by default.
    static func rewrite(_ source: URL, to dest: URL, plan: [(sources: [Int], name: String)]) async throws {
        let asset = AVURLAsset(url: source)
        let audioTracks = try await asset.loadTracks(withMediaType: .audio)
        let videoTracks = try await asset.loadTracks(withMediaType: .video)
        guard plan.allSatisfy({ $0.sources.allSatisfy { audioTracks.indices.contains($0) } }) else {
            throw RewindError("That sound isn't in the clip.")
        }

        let tmp = dest.deletingLastPathComponent().appendingPathComponent(".sound-\(UUID().uuidString).\(dest.pathExtension)")
        defer { try? FileManager.default.removeItem(at: tmp) }
        let reader = try AVAssetReader(asset: asset)
        let writer = try AVAssetWriter(outputURL: tmp, fileType: dest.pathExtension.lowercased() == "mov" ? .mov : .mp4)
        writer.shouldOptimizeForNetworkUse = true
        var jobs: [(AVAssetReaderOutput, AVAssetWriterInput)] = []

        if let vt = videoTracks.first {
            let out = AVAssetReaderTrackOutput(track: vt, outputSettings: nil)
            out.alwaysCopiesSampleData = false
            guard reader.canAdd(out) else { throw RewindError("Can't read the video track.") }
            reader.add(out)
            let hint = try await vt.load(.formatDescriptions).first
            let vin = AVAssetWriterInput(mediaType: .video, outputSettings: nil, sourceFormatHint: hint)
            vin.transform = try await vt.load(.preferredTransform)
            vin.expectsMediaDataInRealTime = false
            guard writer.canAdd(vin) else { throw RewindError("Can't write the video track.") }
            writer.add(vin)
            jobs.append((out, vin))
        }

        let pcm: [String: Any] = [
            AVFormatIDKey: kAudioFormatLinearPCM, AVSampleRateKey: 48_000, AVNumberOfChannelsKey: 2,
            AVLinearPCMBitDepthKey: 32, AVLinearPCMIsFloatKey: true,
            AVLinearPCMIsNonInterleaved: false, AVLinearPCMIsBigEndianKey: false,
        ]
        var audioInputs: [AVAssetWriterInput] = []
        for entry in plan {
            let out: AVAssetReaderOutput
            let input: AVAssetWriterInput
            if entry.sources.count == 1 {
                let track = audioTracks[entry.sources[0]]
                let o = AVAssetReaderTrackOutput(track: track, outputSettings: nil)
                o.alwaysCopiesSampleData = false
                out = o
                input = AVAssetWriterInput(mediaType: .audio, outputSettings: nil,
                                           sourceFormatHint: try await track.load(.formatDescriptions).first)
            } else {
                let o = AVAssetReaderAudioMixOutput(audioTracks: entry.sources.map { audioTracks[$0] }, audioSettings: pcm)
                o.alwaysCopiesSampleData = false
                out = o
                input = AVAssetWriterInput(mediaType: .audio, outputSettings: [
                    AVFormatIDKey: kAudioFormatMPEG4AAC, AVSampleRateKey: 48_000,
                    AVNumberOfChannelsKey: 2, AVEncoderBitRateKey: 192_000,
                ])
            }
            guard reader.canAdd(out) else { throw RewindError("Can't read the clip's sound.") }
            reader.add(out)
            input.expectsMediaDataInRealTime = false
            input.metadata = metadata(name: entry.name)
            guard writer.canAdd(input) else { throw RewindError("Can't write the sound track.") }
            writer.add(input)
            audioInputs.append(input)
            jobs.append((out, input))
        }
        group(audioInputs, in: writer)

        guard reader.startReading() else { throw reader.error ?? RewindError("Couldn't read \(source.lastPathComponent).") }
        // Peek at each output's first sample so the session starts where the media does.
        var firsts: [CMSampleBuffer?] = jobs.map { $0.0.copyNextSampleBuffer() }
        let starts = firsts.compactMap { $0?.presentationTimeStamp }.filter(\.isValid)
        guard let start = starts.min() else { throw RewindError("The file has no media.") }
        guard writer.startWriting() else { throw writer.error ?? RewindError("Couldn't start writing.") }
        writer.startSession(atSourceTime: start)

        await withCheckedContinuation { (done: CheckedContinuation<Void, Never>) in
            let group = DispatchGroup()
            for (i, job) in jobs.enumerated() {
                let (output, input) = job
                var pending = firsts[i]
                firsts[i] = nil
                var finished = false
                group.enter()
                input.requestMediaDataWhenReady(on: DispatchQueue(label: "rewind.sound.\(i)")) {
                    guard !finished else { return }
                    while input.isReadyForMoreMediaData {
                        let sample = pending ?? output.copyNextSampleBuffer()
                        pending = nil
                        guard let sample, writer.status == .writing, input.append(sample) else {
                            finished = true
                            input.markAsFinished()
                            group.leave()
                            return
                        }
                    }
                }
            }
            group.notify(queue: .global()) { done.resume() }
        }

        if reader.status == .failed { writer.cancelWriting(); throw reader.error ?? RewindError("Reading failed.") }
        await writer.finishWriting()
        guard writer.status == .completed else { throw writer.error ?? RewindError("Writing the sound tracks failed.") }
        if FileManager.default.fileExists(atPath: dest.path) {
            _ = try FileManager.default.replaceItemAt(dest, withItemAt: tmp)
        } else {
            try FileManager.default.moveItem(at: tmp, to: dest)
        }
    }
}
