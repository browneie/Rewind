import Foundation
import AVFoundation
import CoreMedia

/// Folds every audio track of an MP4 into one stereo AAC track, so players that only play the
/// first track (VLC, browsers, Discord, phones) still hear the microphone. Video is copied as-is.
enum AudioMixdown {
    /// Rewrites `url` in place if it has more than one audio track. Leaves the file untouched on failure.
    static func mixIfNeeded(_ url: URL) async throws {
        let asset = AVURLAsset(url: url)
        let audioTracks = try await asset.loadTracks(withMediaType: .audio)
        guard audioTracks.count > 1 else { return }
        let videoTracks = try await asset.loadTracks(withMediaType: .video)

        let tmp = url.deletingLastPathComponent().appendingPathComponent(".mixing-\(UUID().uuidString).mp4")
        defer { try? FileManager.default.removeItem(at: tmp) }

        let reader = try AVAssetReader(asset: asset)
        let writer = try AVAssetWriter(outputURL: tmp, fileType: .mp4)
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
        let mixOut = AVAssetReaderAudioMixOutput(audioTracks: audioTracks, audioSettings: pcm)
        mixOut.alwaysCopiesSampleData = false
        guard reader.canAdd(mixOut) else { throw RewindError("Can't mix these audio tracks.") }
        reader.add(mixOut)
        let ain = AVAssetWriterInput(mediaType: .audio, outputSettings: [
            AVFormatIDKey: kAudioFormatMPEG4AAC, AVSampleRateKey: 48_000,
            AVNumberOfChannelsKey: 2, AVEncoderBitRateKey: 192_000,
        ])
        ain.expectsMediaDataInRealTime = false
        guard writer.canAdd(ain) else { throw RewindError("Can't write the mixed audio track.") }
        writer.add(ain)
        jobs.append((mixOut, ain))

        guard reader.startReading() else { throw reader.error ?? RewindError("Couldn't read \(url.lastPathComponent).") }

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
                input.requestMediaDataWhenReady(on: DispatchQueue(label: "rewind.mixdown.\(i)")) {
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
        guard writer.status == .completed else { throw writer.error ?? RewindError("Writing the mixed file failed.") }
        _ = try FileManager.default.replaceItemAt(url, withItemAt: tmp)
    }
}
