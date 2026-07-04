<p align="center">
  <img src="wispclip.png" alt="Wispclip logo" width="180"/>
</p>

# Wispclip

**Lightweight Windows game recorder with an always-ready replay buffer, hardware encoding, and a focused clip editor.**

![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D6)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)
![License](https://img.shields.io/badge/license-GPLv3-blue)

Everything runs locally. Wispclip never uploads recordings, analytics, or account data.

---

## Contents

- [Features](#features)
- [Install](#install)
- [FFmpeg](#ffmpeg)
- [First launch](#first-launch)
- [Usage](#usage)
  - [Hotkeys](#hotkeys)
  - [Recording modes](#recording-modes)
  - [Editor](#editor)
  - [Library](#library)
- [Hardware and codecs](#hardware-and-codecs)
- [Where data is stored](#where-data-is-stored)
- [Troubleshooting](#troubleshooting)
- [Build from source](#build-from-source)
- [Development](#development)
- [License](#license)

## Features

- **Always-on replay buffer** — keep the last 15 seconds to 5 minutes and save it with a hotkey.
- **Hardware encoding** — NVENC, AMD AMF, and Intel Quick Sync with automatic pipeline detection.
- **Low-impact capture** — probes the best GPU capture path instead of trusting driver capability lists.
- **Built-in editor** — trim, zoom, and reframe clips, then export without touching the original.
- **Tray-first** — arms on launch, keeps recording when the window closes, optional start at sign-in.
- **Fully local** — no accounts, no uploads, no telemetry.

## Install

**Requirements:** Windows 10 or 11. No separate .NET install is needed for release builds. FFmpeg is required and is **not bundled** — see [FFmpeg](#ffmpeg) below.

1. Download the latest release and extract it anywhere.
2. Download FFmpeg (see [FFmpeg](#ffmpeg)) and place `ffmpeg.exe` and `ffprobe.exe` in a `tools\` folder next to `Wispclip.exe`.
3. Run `Wispclip.exe`.

A portable release is laid out like this:

```text
Wispclip/
├─ Wispclip.exe
├─ tools/
│  ├─ ffmpeg.exe   (you provide these — see FFmpeg below)
│  └─ ffprobe.exe
├─ README.md
├─ LICENSE
└─ THIRD_PARTY_NOTICES.md
```

> Wispclip is unsigned. Windows SmartScreen may warn on first launch — choose **More info → Run anyway** if you trust the build.

## FFmpeg

Wispclip uses FFmpeg for capture, encoding, and export, but does not bundle or auto-download it — grab it yourself from the official page:

**[ffmpeg.org/download.html](https://ffmpeg.org/download.html)**

Pick a Windows build that includes the `ddagrab` filter, which Wispclip needs for GPU screen capture — the essentials build from [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) (linked from the official page) works well. After extracting the archive, give Wispclip `ffmpeg.exe` and `ffprobe.exe` one of these ways:

- Place them in a `tools\` folder next to `Wispclip.exe`.
- Add their folder to your system `PATH`.
- Point Wispclip at their folder under **Settings → Capture engine → Custom FFmpeg folder**.

## First launch

On first run, Wispclip automatically:

- detects the lowest-impact capture path supported by the GPU;
- tests hardware encoding before selecting it;
- arms the replay buffer;
- registers itself under **Windows Settings → Apps → Startup**;
- stays available in the system tray when the window closes.

Upgrading from **Clipps**? Existing settings and recording locations are imported automatically. Existing recordings are not moved.

## Usage

### Hotkeys

| Action | Default hotkey |
| --- | --- |
| Save the replay buffer | `Ctrl+Alt+S` |
| Start or stop a full recording | `Ctrl+Alt+R` |
| Turn the replay buffer on or off | `Ctrl+Alt+B` |

Rebind any of these under **Settings → Hotkeys**.

### Recording modes

**Replay buffer** (default) continuously protects the most recent 15 seconds to 5 minutes. Press the save hotkey only when something worth keeping happens. Saving joins already-encoded segments without re-encoding, avoiding a GPU or CPU spike mid-game.

**Full recording** — press **Record** or `Ctrl+Alt+R` for a normal recording. The status area shows the active state and elapsed time.

### Editor

Double-click a clip to open the editor.

- Drag the orange range handles to choose the exported section.
- Use **Start at** and **End at** to place trim points at the playhead.
- Add zoom segments, then click the preview to place their focus.
- Choose a background, frame size, and corner radius.
- Review the selected duration and resolution in the Export panel.
- Select **Export video** to render a new file without changing the original.

Edit settings are saved beside the source recording as a small `.wispclip.json` sidecar. Legacy `.clipps.json` projects from the old Clipps name still load automatically.

### Library

One click selects a clip and exposes quick actions; double-click opens the editor.

- **Edit** opens trimming and effects.
- **Rename** changes the file name.
- **Delete** asks for confirmation before removing the file.
- The context menu can copy the file path or reveal the recording in Explorer.

## Hardware and codecs

Wispclip tests available pipelines instead of trusting capability lists from the driver.

| Hardware path | H.264 | H.265 / HEVC | AV1 |
| --- | --- | --- | --- |
| NVIDIA NVENC | Supported | Supported | Supported on compatible GPUs |
| AMD AMF | Supported | Supported | Supported on compatible GPUs |
| Intel Quick Sync | Supported | Supported | Supported on compatible GPUs |
| Windows Media Foundation | Fallback | Hardware dependent | Hardware dependent |
| Software encoding | Last-resort fallback | Last-resort fallback | Hardware recommended |

H.264 has the widest playback and editing compatibility. HEVC and AV1 usually produce smaller files at similar quality, but Windows may require the matching Microsoft video extension for playback.

## Where data is stored

| Data | Location |
| --- | --- |
| Recordings | `Videos\Wispclip` |
| Settings | `%AppData%\Wispclip\settings.json` |
| Logs | `%AppData%\Wispclip\logs\wispclip.log` |
| Thumbnails | `%LocalAppData%\Wispclip\thumbs` |
| Edit projects | `<clip>.wispclip.json` beside each recording |
| Startup entry | `HKCU\...\Run\Wispclip` |

Closing the window keeps capture running in the tray by default. Use **Exit** from the tray menu to stop Wispclip completely. The startup entry appears in **Windows Settings → Apps → Startup** and can be toggled from Windows or Wispclip Settings.

## Troubleshooting

**FFmpeg not found.** Wispclip doesn't bundle FFmpeg — see [FFmpeg](#ffmpeg) above for where to get it and where to put it. When building from source, run `configure.cmd deps` to check whether `tools\` has it.

**A clip will not play in the editor.** The file may use HEVC or AV1 without the matching Windows decoder. Install the appropriate Microsoft video extension or switch future recordings to H.264. The recording file itself is normally unaffected.

**Capture does not start.** Open **Settings → Capture engine** and select **Detect best pipeline**. The log is available from the same section.

**Audio is early or late.** Adjust **Settings → Audio → Audio delay**. Positive values delay audio.

**The game loses too many frames.** Use a hardware codec, reduce the frame rate, and keep the automatically detected GPU capture pipeline. Avoid software encoding unless no hardware encoder works.

**A hotkey does nothing.** Another application may already own that key combination. Change it under **Settings → Hotkeys**.

## Build from source

**Requirements:**

- Windows 10 or 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- FFmpeg in `tools\` — see [FFmpeg](#ffmpeg) above; `configure.cmd` checks for it but does not download it

From the repository root:

```cmd
configure.cmd
```

This publishes a self-contained single-file build and stages a complete, zip-ready release folder at `dist\Wispclip\`, copying `tools\ffmpeg.exe` and `tools\ffprobe.exe` into it.

| Command | What it does |
| --- | --- |
| `configure.cmd` | Check deps, build, and stage the release |
| `configure.cmd deps` | Check dependencies only (fails with instructions if FFmpeg is missing from `tools\`) |
| `configure.cmd build` | Build and stage `dist\Wispclip` (requires deps) |
| `configure.cmd clean` | Remove `dist\Wispclip`, `bin`, and `obj` |
| `configure.cmd help` | Show usage |

To publish a release, zip the `dist\Wispclip` folder and attach it to a GitHub Release.

## Development

Quick iteration without publishing:

```powershell
configure.cmd deps
dotnet run --project src\Wispclip\Wispclip.csproj -c Release
```

Manual release publish (equivalent to `configure.cmd build` after deps):

```powershell
dotnet publish src\Wispclip\Wispclip.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o dist\Wispclip
```

Then copy `tools\ffmpeg.exe` and `tools\ffprobe.exe` into the output folder yourself, or run `configure.cmd build` to do both.

## License

Wispclip is free software licensed under the [GNU General Public License v3.0 or later](LICENSE).

FFmpeg and other third-party components have their own licenses. See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
