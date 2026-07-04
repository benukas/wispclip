# tools/

This folder holds **local** FFmpeg binaries. Wispclip does not bundle or
download FFmpeg automatically — get it yourself from the official page:

https://ffmpeg.org/download.html

Use a Windows build that includes the `ddagrab` filter, which Wispclip needs
for GPU screen capture (the essentials build from
[gyan.dev](https://www.gyan.dev/ffmpeg/builds/), linked from the page above,
works). Extract the archive and copy these two files here:

```text
tools/ffmpeg.exe
tools/ffprobe.exe
```

`configure.cmd build` copies these into `dist\Wispclip\tools\` for release
packaging; `configure.cmd` / `configure.cmd deps` just check that they're
here and tell you where to get them if not.

Wispclip looks for FFmpeg in `tools\` next to the executable, then on PATH,
then under **Settings > Capture engine**.
