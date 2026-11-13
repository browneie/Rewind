import AppKit

if let i = CommandLine.arguments.firstIndex(of: "--selftest") {
    let dir = CommandLine.arguments.count > i + 1 ? URL(fileURLWithPath: CommandLine.arguments[i + 1])
                                                  : FileManager.default.temporaryDirectory
    exit(SelfTest.run(dir: dir))
}

MainActor.assumeIsolated {
    let app = NSApplication.shared
    let delegate = AppDelegate()
    app.delegate = delegate
    app.setActivationPolicy(.accessory)
    app.run()
}
