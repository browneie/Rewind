import Foundation
import Carbon.HIToolbox

/// System-wide hotkeys via Carbon's RegisterEventHotKey (works in fullscreen games, needs no
/// Accessibility permission).
@MainActor
final class HotkeyManager {
    static let shared = HotkeyManager()

    private var refs: [EventHotKeyRef] = []
    private var handlers: [UInt32: () -> Void] = [:]
    private var bindings: [(Hotkey, () -> Void)] = []
    private var suspended = false
    private var installed = false

    /// Registers the bindings; returns the hotkeys that couldn't be registered (already taken).
    @discardableResult
    func register(_ bindings: [(Hotkey, () -> Void)]) -> [Hotkey] {
        self.bindings = bindings
        return suspended ? [] : apply()
    }

    func suspend() { suspended = true; unregisterAll() }
    func resume() { suspended = false; apply() }

    @discardableResult
    private func apply() -> [Hotkey] {
        installHandlerIfNeeded()
        unregisterAll()
        var failed: [Hotkey] = []
        for (i, (hk, action)) in bindings.enumerated() {
            let id = EventHotKeyID(signature: OSType(0x5257_4E44) /* 'RWND' */, id: UInt32(i + 1))
            var ref: EventHotKeyRef?
            let status = RegisterEventHotKey(hk.keyCode, hk.modifiers, id, GetApplicationEventTarget(), 0, &ref)
            if status == noErr, let ref {
                refs.append(ref)
                handlers[id.id] = action
            } else {
                failed.append(hk)
            }
        }
        return failed
    }

    private func unregisterAll() {
        refs.forEach { UnregisterEventHotKey($0) }
        refs.removeAll()
        handlers.removeAll()
    }

    fileprivate func fire(_ id: UInt32) { handlers[id]?() }

    private func installHandlerIfNeeded() {
        guard !installed else { return }
        installed = true
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), { _, event, _ -> OSStatus in
            var hk = EventHotKeyID()
            GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID),
                              nil, MemoryLayout<EventHotKeyID>.size, nil, &hk)
            let id = hk.id
            MainActor.assumeIsolated { HotkeyManager.shared.fire(id) }
            return noErr
        }, 1, &spec, nil, nil)
    }
}
