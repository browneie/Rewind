import Foundation
import CoreMedia

struct ReplaySnapshot {
    var video: [EncodedFrame]
    var audio: [AudioTrack: [CMSampleBuffer]]
}

/// Rolling in-memory buffer of already-encoded video plus raw audio, like ShadowPlay's Instant Replay.
/// Always starts on a keyframe so a clip can be written without re-encoding.
final class ReplayBuffer: @unchecked Sendable {
    private let lock = NSLock()
    private var video: [EncodedFrame] = []
    private var audio: [AudioTrack: [CMSampleBuffer]] = [:]
    private var videoBytes = 0
    private var _duration: Double = 120

    var duration: Double {
        get { lock.lock(); defer { lock.unlock() }; return _duration }
        set { lock.lock(); _duration = newValue; trimLocked(); lock.unlock() }
    }

    func append(_ frame: EncodedFrame) {
        lock.lock(); defer { lock.unlock() }
        if video.isEmpty && !frame.isKey { return }
        video.append(frame)
        videoBytes += frame.size
        trimLocked()
    }

    func appendAudio(_ sample: CMSampleBuffer, track: AudioTrack) {
        lock.lock(); defer { lock.unlock() }
        audio[track, default: []].append(sample)
        // Bound audio even while the screen is static and no video frames arrive.
        let newest = sample.presentationTimeStamp.seconds
        trimAudioLocked(track, before: newest - _duration - 2)
    }

    private func trimLocked() {
        guard let newest = video.last?.pts.seconds else { return }
        let cutoff = newest - _duration
        // Keep from the last keyframe at or before the cutoff, so the clip is at least `duration` long.
        var cut = 0
        var i = 0
        while i < video.count, video[i].pts.seconds <= cutoff {
            if video[i].isKey { cut = i }
            i += 1
        }
        if cut > 0 {
            for f in video[..<cut] { videoBytes -= f.size }
            video.removeFirst(cut)
        }
        if let first = video.first?.pts.seconds {
            for t in audio.keys { trimAudioLocked(t, before: first - 0.5) }
        }
    }

    private func trimAudioLocked(_ track: AudioTrack, before time: Double) {
        guard var arr = audio[track], let idx = arr.firstIndex(where: { $0.presentationTimeStamp.seconds >= time }) else { return }
        if idx > 0 { arr.removeFirst(idx); audio[track] = arr }
    }

    func snapshot() -> ReplaySnapshot {
        lock.lock(); defer { lock.unlock() }
        return ReplaySnapshot(video: video, audio: audio)
    }

    func clear() {
        lock.lock(); defer { lock.unlock() }
        video.removeAll(); audio.removeAll(); videoBytes = 0
    }

    var bufferedSeconds: Double {
        lock.lock(); defer { lock.unlock() }
        guard let a = video.first?.pts.seconds, let b = video.last?.pts.seconds else { return 0 }
        return min(b - a, _duration)
    }

    var bytes: Int {
        lock.lock(); defer { lock.unlock() }
        return videoBytes
    }
}

/// Fans encoded video and audio out to the replay buffer and the active recording, from capture threads.
final class MediaRouter: @unchecked Sendable {
    let replay = ReplayBuffer()
    private let lock = NSLock()
    private var _bufferReplay = false
    private var _recorder: LiveRecorder?
    private var _audioFormats: [AudioTrack: CMFormatDescription] = [:]

    /// Last-seen audio format per track, so a new recording can set up its audio inputs immediately.
    var audioFormats: [AudioTrack: CMFormatDescription] {
        lock.lock(); defer { lock.unlock() }; return _audioFormats
    }

    var bufferReplay: Bool {
        get { lock.lock(); defer { lock.unlock() }; return _bufferReplay }
        set { lock.lock(); _bufferReplay = newValue; lock.unlock() }
    }

    var recorder: LiveRecorder? {
        get { lock.lock(); defer { lock.unlock() }; return _recorder }
        set { lock.lock(); _recorder = newValue; lock.unlock() }
    }

    func resetAudioFormats() {
        lock.lock(); _audioFormats.removeAll(); lock.unlock()
    }

    func video(_ sample: CMSampleBuffer) {
        let frame = EncodedFrame(sample)
        lock.lock(); let rep = _bufferReplay; let rec = _recorder; lock.unlock()
        if rep { replay.append(frame) }
        rec?.appendVideo(frame)
    }

    func audio(_ sample: CMSampleBuffer, _ track: AudioTrack) {
        lock.lock()
        if _audioFormats[track] == nil, let f = sample.formatDescription { _audioFormats[track] = f }
        let rep = _bufferReplay; let rec = _recorder
        lock.unlock()
        if rep { replay.appendAudio(sample, track: track) }
        rec?.appendAudio(sample, track: track)
    }
}
