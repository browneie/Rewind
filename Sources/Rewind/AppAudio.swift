import Foundation
import AppKit
import AVFoundation
import CoreAudio
import CoreMedia
import Darwin

/// Watches which apps are making sound and records each one on a track of its own, through Core Audio
/// process taps, up to eight. Helper processes count as their app ("Discord Helper" is Discord), and apps
/// without a bundle (Minecraft's java) are named after their window. The captured app is skipped: its sound
/// is already the clip's own.
final class AppAudioWatcher: @unchecked Sendable {
    static let most = 8

    private let onSample: @Sendable (CMSampleBuffer, AudioTrack) -> Void
    private let onProblem: @Sendable (String) -> Void
    private let skip: Set<String>
    private let selfKey = AppIdentity.key(for: getpid())
    private let queue = DispatchQueue(label: "rewind.app-audio")
    private var timer: DispatchSourceTimer?
    private let lock = NSLock()
    private var live: [String: AppTap] = [:]
    private var failed: Set<String> = []
    private var stopped = false
    private var reported = false

    /// `skipping`: the captured app's processes. `onSample` is called with AAC sound, on a background queue.
    init(skipping pids: [pid_t], onSample: @escaping @Sendable (CMSampleBuffer, AudioTrack) -> Void,
         onProblem: @escaping @Sendable (String) -> Void) {
        skip = Set(pids.compactMap { AppIdentity.key(for: $0) })
        self.onSample = onSample
        self.onProblem = onProblem
        let t = DispatchSource.makeTimerSource(queue: queue)
        t.schedule(deadline: .now() + 0.3, repeating: 1.5)
        t.setEventHandler { [weak self] in self?.poll() }
        t.resume()
        timer = t
    }

    /// The apps being recorded right now.
    var tracks: [AudioTrack] {
        lock.lock(); defer { lock.unlock() }
        return live.values.map(\.track).sorted()
    }

    func stop() {
        queue.sync {
            stopped = true
            timer?.cancel()
            timer = nil
            lock.lock()
            let taps = Array(live.values)
            live.removeAll()
            lock.unlock()
            taps.forEach { $0.stop() }
        }
    }

    private func poll() {
        guard !stopped else { return }
        var apps: [String: [AudioProcesses.Process]] = [:]
        for p in AudioProcesses.all() {
            guard let key = AppIdentity.key(for: p.pid) else { continue }
            apps[key, default: []].append(p)
        }
        lock.lock()
        let gone = live.filter { apps[$0.key] == nil }
        for key in gone.keys { live[key] = nil }
        let current = live
        lock.unlock()
        gone.values.forEach { $0.stop() }

        for (key, procs) in apps where procs.contains(where: \.audible) {
            if key == selfKey || skip.contains(key) || failed.contains(key) { continue }
            let objects = Set(procs.map(\.object))
            if let tap = current[key] {
                // A new helper process started making sound: tap it too.
                guard !objects.isSubset(of: tap.objects) else { continue }
                tap.stop()
                start(key, objects: objects, track: tap.track)
                continue
            }
            guard current.count < Self.most else { break }
            start(key, objects: objects, track: .app(uniqueName(AppIdentity.name(key: key, pid: procs[0].pid))))
        }
    }

    private func start(_ key: String, objects: Set<AudioObjectID>, track: AudioTrack) {
        do {
            let tap = try AppTap(objects: objects, track: track, onSample: onSample)
            lock.lock(); live[key] = tap; lock.unlock()
            NSLog("Rewind: recording \(track.name(main: "")) on its own track")
        } catch {
            failed.insert(key)
            lock.lock(); live[key] = nil; lock.unlock()
            NSLog("Rewind: can't record \(track.name(main: "")) on its own: \(error.localizedDescription)")
            if !reported {
                reported = true
                onProblem(error.localizedDescription)
            }
        }
    }

    /// Names are unique in a clip, and never clash with the clip's own sound or the microphone's.
    private func uniqueName(_ name: String) -> String {
        lock.lock(); defer { lock.unlock() }
        let taken = Set(live.values.map { $0.track.name(main: "") } + [SoundNames.all, SoundNames.mic])
        var candidate = name.isEmpty ? "App" : name
        var n = 2
        while taken.contains(candidate) { candidate = "\(name) \(n)"; n += 1 }
        return candidate
    }
}

/// The processes Core Audio knows, and whether each is playing sound right now (macOS 14.2+).
enum AudioProcesses {
    struct Process { let object: AudioObjectID; let pid: pid_t; let audible: Bool }

    static func all() -> [Process] {
        let system = AudioObjectID(kAudioObjectSystemObject)
        var addr = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyProcessObjectList,
                                              mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(system, &addr, 0, nil, &size) == noErr, size > 0 else { return [] }
        var ids = [AudioObjectID](repeating: 0, count: Int(size) / MemoryLayout<AudioObjectID>.size)
        guard AudioObjectGetPropertyData(system, &addr, 0, nil, &size, &ids) == noErr else { return [] }
        return ids.compactMap { id in
            let pid: pid_t = property(id, kAudioProcessPropertyPID, -1)
            guard pid > 0 else { return nil }
            let running: UInt32 = property(id, kAudioProcessPropertyIsRunningOutput, 0)
            return Process(object: id, pid: pid, audible: running != 0)
        }
    }

    static func property<T>(_ object: AudioObjectID, _ selector: AudioObjectPropertySelector, _ fallback: T) -> T {
        var addr = AudioObjectPropertyAddress(mSelector: selector, mScope: kAudioObjectPropertyScopeGlobal,
                                              mElement: kAudioObjectPropertyElementMain)
        var value = fallback
        var size = UInt32(MemoryLayout<T>.size)
        return AudioObjectGetPropertyData(object, &addr, 0, nil, &size, &value) == noErr ? value : fallback
    }
}

/// Which app a process belongs to: the outermost .app it runs from (so helpers inside Discord.app are Discord),
/// or its own executable for programs outside a bundle.
enum AppIdentity {
    static func key(for pid: pid_t) -> String? {
        var buf = [CChar](repeating: 0, count: 4096)
        guard proc_pidpath(pid, &buf, UInt32(buf.count)) > 0 else { return nil }
        let path = String(cString: buf)
        if let r = path.range(of: ".app/") { return String(path[..<r.lowerBound]) + ".app" }
        return path
    }

    /// "Discord", or for a program without a bundle its window's title ("Minecraft 1.21.4"), else its file name.
    static func name(key: String, pid: pid_t) -> String {
        if key.hasSuffix(".app") {
            if let app = NSWorkspace.shared.runningApplications.first(where: { $0.bundleURL?.path == key }),
               let name = app.localizedName, !name.isEmpty { return name }
            return URL(fileURLWithPath: key).deletingPathExtension().lastPathComponent
        }
        if let title = windowTitle(pid) { return title }
        return URL(fileURLWithPath: key).lastPathComponent
    }

    private static func windowTitle(_ pid: pid_t) -> String? {
        guard let windows = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID)
                as? [[String: Any]] else { return nil }
        for w in windows where (w[kCGWindowOwnerPID as String] as? pid_t) == pid && (w[kCGWindowLayer as String] as? Int) == 0 {
            if let title = w[kCGWindowName as String] as? String, !title.isEmpty { return String(title.prefix(40)) }
        }
        return nil
    }
}

/// One app's sound: a private process tap on the app's processes, read through a private aggregate device
/// and encoded to AAC as it arrives.
final class AppTap: @unchecked Sendable {
    let track: AudioTrack
    let objects: Set<AudioObjectID>
    private var tapID = AudioObjectID(kAudioObjectUnknown)
    private var deviceID = AudioObjectID(kAudioObjectUnknown)
    private var procID: AudioDeviceIOProcID?
    private let queue: DispatchQueue

    init(objects: Set<AudioObjectID>, track: AudioTrack, onSample: @escaping @Sendable (CMSampleBuffer, AudioTrack) -> Void) throws {
        self.track = track
        self.objects = objects
        let name = track.name(main: "")
        queue = DispatchQueue(label: "rewind.app-audio.\(name)", qos: .userInteractive)

        let desc = CATapDescription(stereoMixdownOfProcesses: objects.sorted())
        desc.name = "Rewind: \(name)"
        desc.isPrivate = true
        desc.muteBehavior = .unmuted
        var tap = AudioObjectID(kAudioObjectUnknown)
        try Self.check(AudioHardwareCreateProcessTap(desc, &tap), "Couldn't tap \(name)'s sound")
        tapID = tap

        var asbd: AudioStreamBasicDescription = AudioProcesses.property(tap, kAudioTapPropertyFormat, AudioStreamBasicDescription())
        guard asbd.mSampleRate > 0, let format = AVAudioFormat(streamDescription: &asbd) else {
            stop()
            throw RewindError("Couldn't read \(name)'s sound format.")
        }
        let encoder: AACTrackEncoder
        do { encoder = try AACTrackEncoder(input: format) } catch { stop(); throw error }

        var description: [String: Any] = [
            kAudioAggregateDeviceNameKey: "Rewind: \(name)",
            kAudioAggregateDeviceUIDKey: UUID().uuidString,
            kAudioAggregateDeviceIsPrivateKey: true,
            kAudioAggregateDeviceIsStackedKey: false,
            kAudioAggregateDeviceTapAutoStartKey: true,
            kAudioAggregateDeviceTapListKey: [[kAudioSubTapDriftCompensationKey: true, kAudioSubTapUIDKey: desc.uuid.uuidString]],
        ]
        // The speakers keep the device's clock.
        if let output = Self.defaultOutputUID() {
            description[kAudioAggregateDeviceMainSubDeviceKey] = output
            description[kAudioAggregateDeviceSubDeviceListKey] = [[kAudioSubDeviceUIDKey: output]]
        }
        var device = AudioObjectID(kAudioObjectUnknown)
        do { try Self.check(AudioHardwareCreateAggregateDevice(description as CFDictionary, &device), "Couldn't read \(name)'s sound") }
        catch { stop(); throw error }
        deviceID = device

        let track = self.track
        var proc: AudioDeviceIOProcID?
        let status = AudioDeviceCreateIOProcIDWithBlock(&proc, device, queue) { _, input, inputTime, _, _ in
            guard let buffer = Self.copy(input, format: format) else { return }
            let t = inputTime.pointee
            let pts = t.mFlags.contains(.hostTimeValid)
                ? CMClockMakeHostTimeFromSystemUnits(t.mHostTime) : CMClockGetTime(CMClockGetHostTimeClock())
            for sample in encoder.encode(buffer, pts: pts) { onSample(sample, track) }
        }
        do {
            try Self.check(status, "Couldn't read \(name)'s sound")
            procID = proc
            try Self.check(AudioDeviceStart(device, proc), "Couldn't start reading \(name)'s sound")
        } catch { stop(); throw error }
    }

    func stop() {
        if deviceID != kAudioObjectUnknown {
            if let procID {
                AudioDeviceStop(deviceID, procID)
                AudioDeviceDestroyIOProcID(deviceID, procID)
            }
            AudioHardwareDestroyAggregateDevice(deviceID)
            deviceID = AudioObjectID(kAudioObjectUnknown)
        }
        procID = nil
        if tapID != kAudioObjectUnknown {
            AudioHardwareDestroyProcessTap(tapID)
            tapID = AudioObjectID(kAudioObjectUnknown)
        }
    }

    /// The input copied out of Core Audio's buffer, which is only valid during the callback.
    private static func copy(_ input: UnsafePointer<AudioBufferList>, format: AVAudioFormat) -> AVAudioPCMBuffer? {
        let src = UnsafeMutableAudioBufferListPointer(UnsafeMutablePointer(mutating: input))
        let bytesPerFrame = format.streamDescription.pointee.mBytesPerFrame
        guard src.count > 0, bytesPerFrame > 0 else { return nil }
        let frames = src[0].mDataByteSize / bytesPerFrame
        guard frames > 0, let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: frames) else { return nil }
        buffer.frameLength = frames
        let dst = UnsafeMutableAudioBufferListPointer(buffer.mutableAudioBufferList)
        for i in 0..<min(src.count, dst.count) {
            guard let from = src[i].mData, let to = dst[i].mData else { continue }
            memcpy(to, from, Int(min(src[i].mDataByteSize, dst[i].mDataByteSize)))
        }
        return buffer
    }

    private static func defaultOutputUID() -> String? {
        let device: AudioObjectID = AudioProcesses.property(AudioObjectID(kAudioObjectSystemObject),
                                                            kAudioHardwarePropertyDefaultSystemOutputDevice, AudioObjectID(0))
        guard device != 0 else { return nil }
        var addr = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyDeviceUID, mScope: kAudioObjectPropertyScopeGlobal,
                                              mElement: kAudioObjectPropertyElementMain)
        var uid: Unmanaged<CFString>?
        var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        guard AudioObjectGetPropertyData(device, &addr, 0, nil, &size, &uid) == noErr, let uid else { return nil }
        return uid.takeRetainedValue() as String
    }

    private static func check(_ status: OSStatus, _ what: String) throws {
        guard status == noErr else {
            throw RewindError(status == kAudioHardwareIllegalOperationError || status == -1
                ? "\(what): Rewind isn't allowed to record other apps' sound (System Settings → Privacy & Security)."
                : "\(what) (error \(status)).")
        }
    }
}

/// Encodes one track's sound to AAC as it arrives, so app tracks take a tenth of the memory raw sound would
/// in the replay buffer. Its sample buffers carry the encoder's magic cookie, so they can be written to an
/// MP4 as they are.
final class AACTrackEncoder: @unchecked Sendable {
    private let converter: AVAudioConverter
    private let output: AVAudioFormat
    private let formatDescription: CMAudioFormatDescription
    private let inputRate: Double
    private var pending: [AVAudioPCMBuffer] = []
    private var start: CMTime?
    private var inputFrames: Int64 = 0   // since `start`
    private var outputFrames: Int64 = 0  // packets written × 1024, since `start`

    init(input: AVAudioFormat) throws {
        inputRate = input.sampleRate
        let rate = min(input.sampleRate, 48_000)
        guard let out = AVAudioFormat(settings: [AVFormatIDKey: kAudioFormatMPEG4AAC, AVSampleRateKey: rate, AVNumberOfChannelsKey: 2]),
              let conv = AVAudioConverter(from: input, to: out) else {
            throw RewindError("Couldn't set up an AAC encoder for \(Int(input.sampleRate)) Hz sound.")
        }
        conv.bitRate = 128_000
        converter = conv
        output = conv.outputFormat
        // The encoder's own description, with 1024 frames per packet (without it the packets have no timing).
        var asbd = conv.outputFormat.streamDescription.pointee
        if asbd.mFramesPerPacket == 0 { asbd.mFramesPerPacket = 1024 }
        var desc: CMAudioFormatDescription?
        let cookie = conv.magicCookie ?? Data()
        let status = cookie.withUnsafeBytes { raw in
            CMAudioFormatDescriptionCreate(allocator: kCFAllocatorDefault, asbd: &asbd, layoutSize: 0, layout: nil,
                                           magicCookieSize: raw.count, magicCookie: raw.baseAddress, extensions: nil,
                                           formatDescriptionOut: &desc)
        }
        guard status == noErr, let desc else { throw RewindError("Couldn't describe the AAC sound (\(status)).") }
        formatDescription = desc
    }

    func encode(_ buffer: AVAudioPCMBuffer, pts: CMTime) -> [CMSampleBuffer] {
        // A gap (the app went quiet and Core Audio stopped calling) or a jump: start the timeline over.
        if let start {
            let expected = start.seconds + Double(inputFrames) / inputRate
            if abs(pts.seconds - expected) > 0.25 { reset() }
        }
        if start == nil {
            start = pts
            inputFrames = 0
            outputFrames = -Int64(converter.primeInfo.leadingFrames) // the encoder's priming isn't real sound
        }
        inputFrames += Int64(buffer.frameLength)
        pending.append(buffer)

        var samples: [CMSampleBuffer] = []
        while true {
            let packets = AVAudioCompressedBuffer(format: output, packetCapacity: 8, maximumPacketSize: max(converter.maximumOutputPacketSize, 1536))
            var error: NSError?
            let status = converter.convert(to: packets, error: &error) { [weak self] _, inputStatus in
                guard let self, !self.pending.isEmpty else { inputStatus.pointee = .noDataNow; return nil }
                inputStatus.pointee = .haveData
                return self.pending.removeFirst()
            }
            if packets.packetCount > 0, let s = sample(packets) { samples.append(s) }
            if status != .haveData || packets.packetCount == 0 { break }
        }
        return samples
    }

    private func reset() {
        converter.reset()
        pending.removeAll()
        start = nil
    }

    private func sample(_ packets: AVAudioCompressedBuffer) -> CMSampleBuffer? {
        guard let start, let descriptions = packets.packetDescriptions else { return nil }
        let count = Int(packets.packetCount), bytes = Int(packets.byteLength)
        var block: CMBlockBuffer?
        guard CMBlockBufferCreateWithMemoryBlock(allocator: kCFAllocatorDefault, memoryBlock: nil, blockLength: bytes,
                                                 blockAllocator: kCFAllocatorDefault, customBlockSource: nil, offsetToData: 0,
                                                 dataLength: bytes, flags: 0, blockBufferOut: &block) == noErr, let block,
              CMBlockBufferReplaceDataBytes(with: packets.data, blockBuffer: block, offsetIntoDestination: 0, dataLength: bytes) == noErr
        else { return nil }
        let rate = Int32(output.sampleRate)
        let pts = start + CMTime(value: outputFrames, timescale: rate)
        outputFrames += Int64(count) * 1024
        var sample: CMSampleBuffer?
        guard CMAudioSampleBufferCreateReadyWithPacketDescriptions(allocator: kCFAllocatorDefault, dataBuffer: block,
                                                                   formatDescription: formatDescription, sampleCount: count,
                                                                   presentationTimeStamp: pts, packetDescriptions: descriptions,
                                                                   sampleBufferOut: &sample) == noErr else { return nil }
        return sample
    }
}

extension CMSampleBuffer {
    /// Already AAC (an app's track): written to files as it is.
    var isAAC: Bool {
        formatDescription.map { CMFormatDescriptionGetMediaSubType($0) == kAudioFormatMPEG4AAC } ?? false
    }
}
