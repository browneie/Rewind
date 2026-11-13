import Foundation
import AVFoundation
import CoreMedia

/// Writes a replay-buffer snapshot to an MP4. Video is passed through untouched (no re-encode);
/// audio is encoded to AAC.
enum ClipWriter {
    static func write(_ snap: ReplaySnapshot, to url: URL,
                      completion: @escaping @Sendable (Result<Double, Error>) -> Void) {
        DispatchQueue.global(qos: .userInitiated).async {
            do {
                guard let k = snap.video.firstIndex(where: \.isKey),
                      let videoFormat = snap.video[k].sample.formatDescription else {
                    throw RewindError("Nothing buffered yet.")
                }
                let frames = Array(snap.video[k...])
                let start = frames[0].pts, end = frames[frames.count - 1].pts

                let writer = try AVAssetWriter(outputURL: url, fileType: .mp4)
                writer.shouldOptimizeForNetworkUse = true
                let vin = AVAssetWriterInput(mediaType: .video, outputSettings: nil, sourceFormatHint: videoFormat)
                vin.expectsMediaDataInRealTime = false
                guard writer.canAdd(vin) else { throw RewindError("Can't write this video format to MP4.") }
                writer.add(vin)

                var jobs: [(AVAssetWriterInput, [CMSampleBuffer])] = [(vin, frames.map(\.sample))]
                for track in AudioTrack.allCases {
                    let samples = (snap.audio[track] ?? []).filter {
                        $0.presentationTimeStamp >= start && $0.presentationTimeStamp <= end
                    }
                    guard let fmt = samples.first?.formatDescription else { continue }
                    let ain = AVAssetWriterInput(mediaType: .audio, outputSettings: aacSettings(for: fmt))
                    ain.expectsMediaDataInRealTime = false
                    if writer.canAdd(ain) { writer.add(ain); jobs.append((ain, samples)) }
                }

                guard writer.startWriting() else { throw writer.error ?? RewindError("Couldn't start writing.") }
                writer.startSession(atSourceTime: start)

                let group = DispatchGroup()
                for (i, job) in jobs.enumerated() {
                    let (input, samples) = job
                    group.enter()
                    var idx = 0
                    var done = false
                    input.requestMediaDataWhenReady(on: DispatchQueue(label: "rewind.clip.\(i)")) {
                        guard !done else { return }
                        while input.isReadyForMoreMediaData {
                            let ok = idx < samples.count && writer.status == .writing && input.append(samples[idx])
                            if !ok {
                                done = true
                                input.markAsFinished()
                                group.leave()
                                return
                            }
                            idx += 1
                        }
                    }
                }
                group.notify(queue: .global()) {
                    writer.finishWriting {
                        if writer.status == .completed {
                            completion(.success((end - start).seconds))
                        } else {
                            try? FileManager.default.removeItem(at: url)
                            completion(.failure(writer.error ?? RewindError("Writing the clip failed.")))
                        }
                    }
                }
            } catch {
                completion(.failure(error))
            }
        }
    }
}

/// A manual recording that's written to disk as it happens (video passed through, audio → AAC).
final class LiveRecorder: @unchecked Sendable {
    let url: URL
    private let lock = NSLock()
    private let wantedTracks: Set<AudioTrack>
    private var formats: [AudioTrack: CMFormatDescription]
    private var firstFrameTime: Double?
    /// Asks the encoder for a keyframe so the file can start right away.
    var requestKeyframe: (() -> Void)?
    private var writer: AVAssetWriter?
    private var videoInput: AVAssetWriterInput?
    private var audioInputs: [AudioTrack: AVAssetWriterInput] = [:]
    private var startPTS: CMTime = .invalid
    private var lastPTS: CMTime = .invalid
    private var finished = false
    private var failure: Error?

    init(url: URL, tracks: Set<AudioTrack>, knownFormats: [AudioTrack: CMFormatDescription] = [:]) {
        self.url = url
        self.wantedTracks = tracks
        self.formats = knownFormats.filter { tracks.contains($0.key) }
    }

    func appendVideo(_ frame: EncodedFrame) {
        lock.lock(); defer { lock.unlock() }
        guard !finished else { return }
        if writer == nil {
            let now = CACurrentMediaTime()
            if firstFrameTime == nil { firstFrameTime = now }
            guard frame.isKey, let fmt = frame.sample.formatDescription else { return }
            // Wait (briefly) until we've seen each audio track's format so it can get its own input.
            let haveAll = wantedTracks.allSatisfy { formats[$0] != nil }
            guard haveAll || now - firstFrameTime! > 1.0 else { return }
            do { try makeWriter(videoFormat: fmt, start: frame.pts) } catch {
                failure = error; finished = true; return
            }
        }
        guard let vin = videoInput, vin.isReadyForMoreMediaData else { return }
        if vin.append(frame.sample) { lastPTS = frame.pts } else { failure = writer?.error }
    }

    func appendAudio(_ sample: CMSampleBuffer, track: AudioTrack) {
        lock.lock(); defer { lock.unlock() }
        guard !finished else { return }
        guard writer != nil else {
            if formats[track] == nil, let f = sample.formatDescription {
                formats[track] = f
                if wantedTracks.allSatisfy({ formats[$0] != nil }) { requestKeyframe?() }
            }
            return
        }
        guard let ain = audioInputs[track], sample.presentationTimeStamp >= startPTS,
              ain.isReadyForMoreMediaData else { return }
        ain.append(sample)
    }

    private func makeWriter(videoFormat: CMFormatDescription, start: CMTime) throws {
        let w = try AVAssetWriter(outputURL: url, fileType: .mp4)
        let vin = AVAssetWriterInput(mediaType: .video, outputSettings: nil, sourceFormatHint: videoFormat)
        vin.expectsMediaDataInRealTime = true
        guard w.canAdd(vin) else { throw RewindError("Can't write this video format to MP4.") }
        w.add(vin)
        for t in wantedTracks.sorted(by: { $0.rawValue < $1.rawValue }) {
            guard let fmt = formats[t] else { continue }
            let ain = AVAssetWriterInput(mediaType: .audio, outputSettings: aacSettings(for: fmt))
            ain.expectsMediaDataInRealTime = true
            if w.canAdd(ain) { w.add(ain); audioInputs[t] = ain }
        }
        guard w.startWriting() else { throw w.error ?? RewindError("Couldn't start writing.") }
        w.startSession(atSourceTime: start)
        writer = w
        videoInput = vin
        startPTS = start
    }

    /// Finishes the file. Calls back with the recording's length.
    func finish(_ completion: @escaping @Sendable (Result<Double, Error>) -> Void) {
        lock.lock()
        finished = true
        guard let w = writer, w.status == .writing else {
            let err = failure ?? writer?.error ?? RewindError("No video was captured.")
            lock.unlock()
            if let w = writer { w.cancelWriting() }
            try? FileManager.default.removeItem(at: url)
            completion(.failure(err))
            return
        }
        videoInput?.markAsFinished()
        audioInputs.values.forEach { $0.markAsFinished() }
        let length = lastPTS.isValid ? (lastPTS - startPTS).seconds : 0
        lock.unlock()
        w.finishWriting {
            if w.status == .completed { completion(.success(length)) }
            else { completion(.failure(w.error ?? RewindError("Writing the recording failed."))) }
        }
    }
}
