<img src="docs/icon.png" width="112" alt="Rewind icon">

# Rewind

A ShadowPlay-style screen recorder for macOS. Rewind keeps the last few minutes of your screen in memory, so you can save a clip *after* something happens. It also does regular recordings and screenshots, all from an in-game overlay.

**On Windows?** There's a native Windows port in [`windows/`](windows/README.md).

![Rewind overlay over a game](docs/overlay.png)

## Features

- **Instant Replay**: keep the last 15 seconds to 20 minutes and save it with one key. Saving doesn't re-encode, so it's near-instant.
- **Recording and screenshots**: these run alongside Instant Replay. Screenshots are full-resolution PNGs.
- **Record a display or one app**: pick an app (a game, say) and only its windows and sound are recorded.
- **Hardware encoding**: H.264 or HEVC on the Apple media engine, at up to 240 fps. Light on the CPU and GPU while you play.
- **Audio**: system sound and/or your mic, mixed into one track or kept as separate tracks.
- **Overlay** (⌥Z): works over fullscreen games. You can play, trim, convert and delete clips right in the overlay, and change every setting there too. It has four layouts.
- **Trim and convert**: cut a clip and save it as H.264 or HEVC, at any quality from "much smaller file" to "original".
- **Copy to share**: one click puts a clip on the clipboard, ready to paste into Discord. Clips over your upload limit (Settings → Saving) are shrunk to fit first.
- **Automatic updates and cleanup**: updates install with one click, and Rewind can move old clips to the Trash for you (off by default).

## Overlay layouts

Pick one in Settings → Overlay.

| Layout | What it looks like |
|---|---|
| **Classic** | ShadowPlay-style tiles across the top, with the Gallery or Settings underneath |
| **Split** | Actions on the left, a player with your newest clip on the right |
| **Console** | One solid panel: actions and capture info on the left, the clip and a filmstrip on the right |
| **Sidebar** | A full-height panel that slides in from the right, in light, dark or matching your system |

In the player, **Space** plays or pauses and **← →** step a frame. **Esc** closes the overlay, or goes back if you're in Settings.

## Hotkeys

| Default | Action |
|---|---|
| ⌥Z | Show / hide the overlay |
| ⌥F10 | Save instant replay |
| ⌥⇧F10 | Turn instant replay on / off |
| ⌥F9 | Start / stop recording |
| ⌥F1 | Take a screenshot |

These work while a game has focus. Change them in Settings → Hotkeys: click one and press the new keys, or press Esc to remove it. On a Mac keyboard you may need to hold **fn** for F-keys.

## Install

1. Download `Rewind-<version>-macOS.dmg` from [Releases](../../releases) and drag **Rewind** into **Applications**.
2. The app isn't notarized, so the first time, **right-click Rewind → Open → Open**. If macOS still blocks it, click **Open Anyway** in System Settings → Privacy & Security.
3. Allow **Screen & System Audio Recording** when asked, then quit and reopen Rewind.

Requires macOS 15 Sequoia or later, on Apple Silicon or Intel. Clips are saved to `~/Movies/Rewind`.

<details>
<summary><b>Permissions</b></summary>

| Permission | When it's needed |
|---|---|
| Screen & System Audio Recording | Always. macOS applies it only after you reopen Rewind. |
| Microphone | If you turn on "Record microphone" |
| Login Items | If you turn on "Open Rewind at login" |
| Files and Folders | If you save clips to Desktop, Documents or Downloads |

Rewind doesn't need Accessibility or Input Monitoring. If clips come out black or silent after an update, remove Rewind from the Screen Recording list, add it again, and reopen it.

</details>

<details>
<summary><b>Updates</b></summary>

Rewind checks GitHub for a new version when it starts. Click the notice to update. Rewind downloads the release, makes sure it's signed with the same certificate as your copy (anything else is refused), then swaps itself out and reopens. You can turn the check off, or check now, in Settings → General.

</details>

<details>
<summary><b>Uninstall</b></summary>

Settings → General → **Uninstall Rewind…** permanently deletes the app, other copies of it, downloaded Rewind installers, its settings and caches. It also resets its permissions and removes its login item. **Your clips are kept.** You'll see the list before anything is deleted.

</details>

## Build from source

Requires Xcode 16 or later.

```bash
./build.sh            # build/Rewind.app for this Mac
./release.sh 2.2.0    # universal, signed .dmg in dist/
.build/release/Rewind --selftest /tmp/rewind-test   # test capture → encode → MP4 without screen permission
```

Builds are signed with a "Rewind Release" certificate from your keychain (not in this repo), falling back to an Apple Development certificate. Set `SIGN_IDENTITY=-` to sign ad-hoc. Keeping the same certificate means macOS keeps Rewind's Screen Recording permission across updates, and the updater only accepts apps signed with it. Sizes, roundness and text for the overlay are in [`Theme.swift`](Sources/Rewind/Theme.swift).

## Known limitations

- Saved replays can be up to a second longer than the set length, because clips have to start on a keyframe.
- With the mic on a separate track, VLC, browsers, Discord and most phones play only the first track (system audio).
- When nothing on screen changes, macOS sends no new frames, so the video holds the last frame.
- Opening the overlay takes focus from the game, so games that pause when they lose focus (like Minecraft) will pause.

## License

[MIT](LICENSE)
