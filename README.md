# Osmo Overlay ✨
Add a modern telemetry HUD (speed, map, route, tilt and more) to your DJI Osmo footage, straight from the data your camera already records - with no loss of image quality.

If you find this repository useful, I would greatly appreciate it if you could give it a **star** ⭐. Thank you!
Using OsmoOverlay in your videos? A mention (e.g. in your YouTube video description) would make my day 💖
Pull requests are welcome too - bug fixes, new widgets, support for other cameras, anything that makes the app better.

![](.github/OsmoOverlay_ru8MsYzI.jpg)

## Features
- **No quality loss** - export settings are taken from the source, so the video matches the camera original 1:1 (codec, 10-bit color, bitrate, timecode).
- **Rendered on your computer, not your phone** - unlike the overlay in DJI Mimo: no drop in image and audio quality, no long wait with the phone unlocked and the app open, no phone as hot as an oven.
- **Joins split recordings** - the camera splits long recordings into files of about 25 minutes, and OsmoOverlay joins them into one video without breaking the route.
- **Telemetry straight from the MP4** - GPS, speed, accelerometer, ISO, shutter speed and white balance.
- **15 widgets** - including a speedometer, map, compass with route, tilt, G-meter, altitude, distance, and date and time. Metric or imperial units.
- **Route card** - an optional summary at the start of the video: the whole route on a map, distance, speeds, elevation gain and ride time.
- **Satellite or standard map** - several built-in map sources or your own tile server.
- **Drag-and-drop editor** - position, size, fonts, colors, appear and disappear animations. Presets can be exported and shared.
- **Live preview** - with sound, playback speed control, looping, a timeline with thumbnails and saving a frame as a full-resolution PNG.
- **Cutting** - remove parts of a recording right in the app. Distance, stats and the route only count what's left.
- **Recording settings check** - shows whether the footage was recorded with the settings recommended for your camera.
- **Privacy** - the camera's serial number and GPS track don't end up in the rendered MP4 unless you turn that on.
- **HUD-only export on a green screen** - for editing in another program.
- **Tools** (no re-encoding, no quality loss):
  - **Remove metadata** - a copy that's safe to share: no GPS track, camera serial number, recording date, timecode or thumbnail.
  - **Camera microphone audio** - with an external microphone connected, the camera saves audio from its built-in microphones to a separate .AAC file that neither Vegas Pro nor Audacity opens by default. Converts to WAV or M4A.
  - **Color tag fix** - Vegas Pro can leave out the color space tag (Rec.709) when exporting to MP4, so some players show wrong colors. Highly recommended after rendering in Vegas Pro.
  - **Compare videos** - resolution, codec, color tags, bitrate and telemetry of up to 6 files side by side.
- **Automatic updates** - the app checks for new versions by itself, and on Windows (when installed with the installer) updates with one click.
- **Windows, Linux and macOS** - x64 and ARM, with a GUI and a command-line version. Only the Windows version is regularly tested and considered stable, Linux and macOS are experimental.

## Supported cameras
Only the DJI Osmo Action 6 is currently supported. I don't have other DJI cameras, so I can't test them. Recordings from other models (e.g. Osmo Action 4) should work. The recording settings check only knows the recommended settings for the Action 6.

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

## Good to know
- The camera has no GPS module of its own, only an accelerometer. To get the route, map, speed and distance, record with the DJI GPS Bluetooth Remote Controller paired with the camera, or use your phone's GPS through DJI Mimo (though that one can behave oddly). Before you start recording, wait until the remote gets a satellite fix. Without GPS data, the widgets that need it are disabled.
- GPS data and the camera's serial number are stored directly in the MP4 file. Be careful who you share it with.
- The same resolution doesn't mean the same quality - codec, color depth and bitrate matter too.

## Download
Every release comes in two variants for each platform:

| Variant               | .NET runtime                                                                          | Size    |
|:----------------------|:--------------------------------------------------------------------------------------|:--------|
| `self-contained`      | Included - nothing else to install                                                    | Larger  |
| `framework-dependent` | [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) required | Smaller |

Pick the archive for your system:

| System                | Package                                              |
|:----------------------|:-----------------------------------------------------|
| Windows (Intel/AMD)   | `OsmoOverlay-<version>-win-x64-<variant>.zip`        |
| Windows (ARM)         | `OsmoOverlay-<version>-win-arm64-<variant>.zip`      |
| Linux (Intel/AMD)     | `OsmoOverlay-<version>-linux-x64-<variant>.tar.gz`   |
| Linux (ARM)           | `OsmoOverlay-<version>-linux-arm64-<variant>.tar.gz` |
| macOS (Intel)         | `OsmoOverlay-<version>-osx-x64-<variant>.tar.gz`     |
| macOS (Apple Silicon) | `OsmoOverlay-<version>-osx-arm64-<variant>.tar.gz`   |

Each package contains both the app (`OsmoOverlay`) and the command-line renderer (`OsmoOverlay.Cli`). Checksums are in `OsmoOverlay-<version>-SHA256SUMS.txt`.

### Windows
The easiest way is the installer: `OsmoOverlay-<version>-win-x64-setup.exe` (or `win-arm64`). It installs for your account into `%LocalAppData%\Programs\OsmoOverlay`, without administrator rights, and adds desktop and Start menu shortcuts and an uninstaller. It's based on the `self-contained` variant, so nothing else is needed. An installed copy checks for new versions on startup and can update itself (also from *Settings* → *About*): it downloads the new installer, verifies it, replaces the old version and starts again.

Alternatively, extract the zip and run `OsmoOverlay.exe`.

The builds aren't code-signed yet, so SmartScreen may warn on first launch - choose *More info* → *Run anyway*.

### Linux
```sh
tar -xzf OsmoOverlay-<version>-linux-x64-self-contained.tar.gz
./OsmoOverlay-<version>-linux-x64-self-contained/OsmoOverlay
```
To add OsmoOverlay (with its icon) to your application menu, run `./install-desktop-entry.sh` from the extracted folder - `--remove` takes it out again. Run it again if you move the folder.

### macOS
Extract the archive and move `OsmoOverlay.app` to *Applications*. The app isn't signed or notarized yet, so macOS blocks the first launch - right-click it and choose *Open*, or remove the quarantine flag:
```sh
xattr -dr com.apple.quarantine /Applications/OsmoOverlay.app
```
The command-line renderer is inside the bundle: `OsmoOverlay.app/Contents/MacOS/OsmoOverlay.Cli`.

## Requirements
**FFmpeg 9** is required - both the `ffmpeg`/`ffprobe` commands and its shared libraries (used by the live preview). If it's missing, the app offers to install it on first launch:

| System  | Installed with                                                                        |
|:--------|:--------------------------------------------------------------------------------------|
| Windows | `winget install Gyan.FFmpeg.Shared` (the static `Gyan.FFmpeg` build has no libraries) |
| macOS   | `brew install ffmpeg`                                                                 |
| Linux   | Your package manager (`ffmpeg` on apt/dnf/pacman)                                     |

Many Linux distributions still ship an older FFmpeg - the preview needs version 9.x.

[ExifTool](https://exiftool.org) is optional. It's only used as a fallback for cameras whose telemetry format the app can't read on its own, and can be installed from *Settings* → *About*.

## Building from source
Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```sh
dotnet run --project OsmoOverlay.Gui                     # run the app
dotnet test --project OsmoOverlay.Tests                  # run the unit tests
dotnet run --project OsmoOverlay.Build                   # build every release package into artifacts/
dotnet run --project OsmoOverlay.Build -- --help         # options: platforms, variant, version, output directory
```
Packages for all platforms can be built on any system (Windows, Linux or macOS).

## License
OsmoOverlay is free for non-commercial use under the [PolyForm Noncommercial License 1.0.0](LICENSE).

- You can use, modify and share it (including modified versions) for any non-commercial purpose.
- The videos you make with it are yours: you can monetize them and publish them as sponsored content, and you can use the app itself for paid editing work for clients.
- You can't sell the app or a modified version of it, or include it in a paid product or service.

The second point is an additional permission from the licensor on top of the license: using OsmoOverlay to create videos and other output, and using that output for any purpose, including commercial ones, is permitted. See [LICENSE](LICENSE) for the full terms.
