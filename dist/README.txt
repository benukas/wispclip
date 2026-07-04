This folder is populated by the release build.

FFmpeg is not bundled or auto-downloaded. Before building, download it
yourself from https://ffmpeg.org/download.html and place `ffmpeg.exe` and
`ffprobe.exe` in `tools\` (see `tools\README.md`).

Then run from the repository root:

```cmd
configure.cmd
```

That produces a complete portable release at `dist\Wispclip\`. Zip that folder for GitHub Releases.

Expected layout after build:

```text
dist/Wispclip/
  Wispclip.exe
  tools/
    ffmpeg.exe
    ffprobe.exe
  README.md
  LICENSE
  THIRD_PARTY_NOTICES.md
```
