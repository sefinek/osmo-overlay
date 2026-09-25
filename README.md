# Osmo Overlay ✨
Not finished yet.

![](.github/OsmoOverlay_ru8MsYzI.jpg)

## Important information
Do not add the overlay in the DJI Mimo app. Doing so will slightly reduce the quality of your footage.

| Comparison      | Original footage from the camera    | Footage exported from DJI Mimo    |
|:----------------|:------------------------------------|:----------------------------------|
| Image quality   | Full quality recorded by the camera | Slightly lower due to re-encoding |
| Resolution      | 3840 × 2160 (4K UHD)                | 3840 × 2160 (4K UHD)              |
| Video codec     | H.265 / HEVC, Main 10               | H.264 / AVC, Baseline             |
| Color depth     | 10-bit                              | 8-bit                             |
| Video bitrate   | 89.79 Mb/s                          | 79.68 Mb/s                        |
| Audio           | AAC-LC, stereo, 48 kHz, 317 kb/s    | AAC-LC, stereo, 48 kHz, 128 kb/s  |
| Camera metadata | Preserved                           | Removed                           |

This application fully preserves the source codec and other original video properties, so it has absolutely no impact on the final quality after export.

Having the same resolution does not mean that the footage retains the same quality.

## Good to know
Telemetry data (GPS data, as well as your camera's serial number) is stored directly in the MP4 file. Be careful who you share it with.

## Download
Every release comes in two variants for each platform:

| Variant               | .NET runtime                                                                       | Size   |
|:----------------------|:-----------------------------------------------------------------------------------|:-------|
| `self-contained`      | Included - nothing else to install                                                 | Larger |
| `framework-dependent` | [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) required | Smaller |

Pick the archive for your system:

| System                | Package                                        |
|:----------------------|:-----------------------------------------------|
| Windows (Intel/AMD)   | `OsmoOverlay-<version>-win-x64-<variant>.zip`     |
| Windows (ARM)         | `OsmoOverlay-<version>-win-arm64-<variant>.zip`   |
| Linux (Intel/AMD)     | `OsmoOverlay-<version>-linux-x64-<variant>.tar.gz`   |
| Linux (ARM)           | `OsmoOverlay-<version>-linux-arm64-<variant>.tar.gz` |
| macOS (Intel)         | `OsmoOverlay-<version>-osx-x64-<variant>.tar.gz`     |
| macOS (Apple Silicon) | `OsmoOverlay-<version>-osx-arm64-<variant>.tar.gz`   |

Each package contains both the app (`OsmoOverlay`) and the command-line renderer (`OsmoOverlay.Cli`). Checksums are in `OsmoOverlay-<version>-SHA256SUMS.txt`.

### Windows
Extract the zip and run `OsmoOverlay.exe`. The builds aren't code-signed yet, so SmartScreen may warn on first launch - choose *More info* → *Run anyway*.

### Linux
```sh
tar -xzf OsmoOverlay-<version>-linux-x64-self-contained.tar.gz
./OsmoOverlay-<version>-linux-x64-self-contained/OsmoOverlay
```

### macOS
Extract the archive and move `OsmoOverlay.app` to *Applications*. The app isn't signed or notarized yet, so macOS blocks the first launch - right-click it and choose *Open*, or remove the quarantine flag:
```sh
xattr -dr com.apple.quarantine /Applications/OsmoOverlay.app
```
The command-line renderer is inside the bundle: `OsmoOverlay.app/Contents/MacOS/OsmoOverlay.Cli`.

## Requirements
**FFmpeg 9** is required - both the `ffmpeg`/`ffprobe` commands and its shared libraries (used for the live preview). If it's missing, the app offers to install it on first launch:

| System  | Installed with                                                                    |
|:--------|:----------------------------------------------------------------------------------|
| Windows | `winget install Gyan.FFmpeg.Shared` (the static `Gyan.FFmpeg` build has no libraries) |
| macOS   | `brew install ffmpeg`                                                             |
| Linux   | Your package manager (`ffmpeg` on apt/dnf/pacman)                                  |

Many Linux distributions still ship an older FFmpeg - the preview needs version 9 exactly.

[ExifTool](https://exiftool.org) is optional. It's only used as a fallback for cameras whose telemetry format the app can't read on its own.

## Building from source
Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```sh
dotnet run --project OsmoOverlay.Gui                     # run the app
dotnet test --project OsmoOverlay.Tests                  # run the unit tests
dotnet run --project OsmoOverlay.Build                   # build every release package into artifacts/
dotnet run --project OsmoOverlay.Build -- --help         # options: platforms, variant, version, output directory
```
Release packages for every platform can be built from any of them.
