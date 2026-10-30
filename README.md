# Osmo Overlay ✨
Add a modern telemetry HUD (speed, map, route, tilt and more) to DJI Osmo, GoPro and DJI drone footage using camera telemetry, DJI flight logs and optional external GPS tracks. Videos are re-encoded with the source's codec and settings to keep the result as close to the original as possible.

If you find this repository useful, I would greatly appreciate it if you could give it a **star** ⭐. Thank you!
Using OsmoOverlay in your videos? A mention (e.g. in your YouTube video description) would make my day 💖
Pull requests are welcome too - bug fixes, new widgets, support for other cameras, anything that makes the app better.

![](.github/OsmoOverlay_I0MRYO1Z.jpg)

## Features
- **Encoded like the original** - export settings are taken from the source to preserve its codec, 10-bit color, bitrate, keyframe interval and timecode. Burning in an overlay always means encoding the picture once more, but with the camera's own settings the difference is practically invisible.
- **Rendered on your computer, not your phone** - unlike the overlay in DJI Mimo: no drop to 8-bit H.264 at a lower bitrate, audio untouched, no long wait with the phone unlocked and the app open, no phone as hot as an oven.
- **Joins split recordings** - the camera splits long recordings into files of about 25 minutes, and OsmoOverlay joins them into one video without breaking the route.
- **Telemetry straight from the MP4** - GPS, speed, accelerometer, ISO, shutter speed and white balance.
- **DJI drones** - the drone's MP4 carries no telemetry, so everything comes from the flight log (`FlightRecord_*.txt`): position, speed, height, climb rate, attitude, satellites, battery, flight mode, signal, remote sticks and flight alerts. The app matches the recording's time to logs in the same folder or saved from your phone, then aligns the video using the recording events in the log.
- **External GPS tracks** - add FIT or GPX from a bike computer or watch, or a DJI flight log. Match an event in the video to its GPS time, with an optional second point to correct clock drift. No camera date is needed; alignment is saved in your project.
- **GPS track on its own** - open a track without a video recording, choose the resolution and frame rate, and play back its telemetry over a satellite or street map. The map can follow your position or show the entire route.
- **Dozens of widgets** - including a speedometer, map, compass with route, roll and pitch, G-meter, elevation and speed charts, trip statistics, altitude, distance, date and time, track temperature, motion sensor temperature, device distance and battery, audio levels, wind noise reduction, image brightness, drone height, distance from home, vertical speed, satellites, battery, signal, flight mode, remote sticks, attitude indicator and a 3D orientation view, and your own text or logo. Metric or imperial units.
- **Route card** - an optional summary at the start of the video: the whole route on a map, distance, speeds, elevation gain and ride time.
- **Satellite or standard map** - several built-in map sources or your own tile server.
- **Drag-and-drop editor** - position, size, fonts, colors, entrance and exit animations. Presets can be exported and shared.
- **Live preview** - play back with sound, adjust playback speed, loop sections, navigate a timeline with thumbnails and save any frame as a full-resolution PNG.
- **Cutting** - remove parts of a recording right in the app, with fade, crossfade, wipe or push transitions and captions at the cuts. Distance, stats and the route only count what's left.
- **Key moments, volume line and settings presets** - mark moments on the timeline, adjust the recording's volume over time and save settings presets to switch between for each project.
- **Music** - add songs on the timeline, with trimming, looping, fades, crossfades and ducking of the recording's sound.
- **Recording settings check** - shows whether the footage was recorded with the settings recommended for your camera.
- **Privacy** - the camera's serial number and GPS track don't end up in the rendered MP4 unless you turn that on.
- **HUD-only export** - the overlay alone on a green screen, or on a transparent background (ProRes 4444 or VP9 with alpha) to key over the footage in an editor.
- **Render queue** - queue several projects and render them one after another, from the app or the command line.
- **Export profiles** - ready-made combinations of resolution, codec and bitrate for the command line (`--profile`: Compatible 8-bit, Web 1080p, Phone 720p, High quality), alongside the default settings that match the source.
- **Telemetry export** - the processed speed, heading, slope, distance, tilt and G as a CSV table or a GPX track.
- **Tools** (no re-encoding, no quality loss):
  - **Remove metadata** - a copy that's safe to share: no GPS track, camera serial number, recording date, timecode or thumbnail.
  - **Camera microphone audio** - with an external microphone connected, the camera saves audio from its built-in microphones to a separate .AAC file that neither Vegas Pro nor Audacity opens by default. Converts to WAV or M4A.
  - **Color tag fix** - Vegas Pro can leave out the color space tag (Rec.709) when exporting to MP4, so some players show wrong colors. Highly recommended after rendering in Vegas Pro.
  - **Compare videos** - resolution, codec, color tags, bitrate and telemetry of up to 6 files side by side.
- **Automatic updates** - the app checks for new versions by itself, and on Windows (when installed with the installer) updates with one click.
- **Windows, Linux and macOS** - x64 and ARM, with a GUI and a command-line version. Only the Windows version is regularly tested and considered stable; Linux and macOS are experimental.

## Download
Every release comes in two variants for each platform:

| Variant               | .NET runtime                                                                          | Size    |
|:----------------------|:--------------------------------------------------------------------------------------|:--------|
| `self-contained`      | Included - nothing else to install                                                    | Larger  |
| `framework-dependent` | [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) required | Smaller |

Pick the archive for your system:

| System        | CPU                  | Installer                                                                                                                | `self-contained`                                                                                                               | `framework-dependent`                                                                                                               |
|:--------------|:---------------------|:-------------------------------------------------------------------------------------------------------------------------|:-------------------------------------------------------------------------------------------------------------------------------|:------------------------------------------------------------------------------------------------------------------------------------|
| Windows 10/11 | Intel / AMD (x64)    | [Download (exe)](https://github.com/sefinek/osmo-overlay/releases/download/v0.4.0/OsmoOverlay-0.4.0-win-x64-setup.exe)   | [zip](https://github.com/sefinek/osmo-overlay/releases/download/v0.4.0/OsmoOverlay-0.4.0-win-x64-self-contained.zip)           | [zip](https://github.com/sefinek/osmo-overlay/releases/download/v0.4.0/OsmoOverlay-0.4.0-win-x64-framework-dependent.zip)           |
| Windows 11    | ARM64 (Snapdragon)   | [Download (exe)](https://github.com/sefinek/osmo-overlay/releases/download/v0.4.0/OsmoOverlay-0.4.0-win-arm64-setup.exe) | [zip](https://github.com/sefinek/osmo-overlay/releases/download/v0.4.0/OsmoOverlay-0.4.0-win-arm64-self-contained.zip)         | [zip](https://github.com/sefinek/osmo-overlay/releases/download/v0.4.0/OsmoOverlay-0.4.0-win-arm64-framework-dependent.zip)         |
| Linux         | Intel / AMD (x64)    | -                                                                                                                        | [tar.gz](https://github.com/sefinek/osmo-overlay/releases/download/v0.4.0/OsmoOverlay-0.4.0-linux-x64-self-contained.tar.gz)   | [tar.gz](https://github.com/sefinek/osmo-overlay/releases/download/v0.4.0/OsmoOverlay-0.4.0-linux-x64-framework-dependent.tar.gz)   |
| Linux         | ARM64 (Raspberry Pi) | -                                                                                                                        | [tar.gz](https://github.com/sefinek/osmo-overlay/releases/download/v0.4.0/OsmoOverlay-0.4.0-linux-arm64-self-contained.tar.gz) | [tar.gz](https://github.com/sefinek/osmo-overlay/releases/download/v0.4.0/OsmoOverlay-0.4.0-linux-arm64-framework-dependent.tar.gz) |
| macOS         | Intel (x64)          | -                                                                                                                        | [tar.gz](https://github.com/sefinek/osmo-overlay/releases/download/v0.4.0/OsmoOverlay-0.4.0-osx-x64-self-contained.tar.gz)     | [tar.gz](https://github.com/sefinek/osmo-overlay/releases/download/v0.4.0/OsmoOverlay-0.4.0-osx-x64-framework-dependent.tar.gz)     |
| macOS         | Apple Silicon (M1+)  | -                                                                                                                        | [tar.gz](https://github.com/sefinek/osmo-overlay/releases/download/v0.4.0/OsmoOverlay-0.4.0-osx-arm64-self-contained.tar.gz)   | [tar.gz](https://github.com/sefinek/osmo-overlay/releases/download/v0.4.0/OsmoOverlay-0.4.0-osx-arm64-framework-dependent.tar.gz)   |

All files are also listed on the [latest release page](https://github.com/sefinek/osmo-overlay/releases/latest). Not sure which CPU you have?
On Windows see *Settings* → *System* → *About* → *System type*; on Linux and macOS run `uname -m` (`x86_64` = x64, `arm64`/`aarch64` = ARM64).

Each package contains both the app (`OsmoOverlay`) and the command-line renderer (`OsmoOverlay.Cli`).
Checksums are in `OsmoOverlay-<version>-SHA256SUMS.txt`.

### Hardware
The first-run guide checks your computer against these hardware requirements for rendering 4K Osmo Action footage:

| Component       | Minimum                                  | Recommended                                                                               |
|:----------------|:-----------------------------------------|:------------------------------------------------------------------------------------------|
| System          | 64-bit                                   | 64-bit                                                                                    |
| RAM             | 8 GB                                     | 16 GB or more                                                                             |
| CPU             | 4 threads                                | 8 threads or more                                                                         |
| Video encoder   | CPU (x264/x265) - works, but much slower | Graphics card: NVIDIA NVENC, AMD AMF or Intel Quick Sync (the latter two on Windows only) |
| Video decoding  | CPU                                      | GPU decoding of 10-bit HEVC (D3D11VA on Windows, VideoToolbox on macOS, VAAPI on Linux)   |
| Free disk space | 20 GB                                    | About 66 GB per hour of 4K footage (rendering temporarily requires twice the output file's size) |

The built-in performance test (*Tools*) measures what your computer can actually do and suggests settings.

### Windows
The easiest way is the installer: `OsmoOverlay-<version>-win-x64-setup.exe` (or `win-arm64`).
It installs into `%LocalAppData%\Programs\OsmoOverlay` for your user account without administrator rights, and includes desktop and Start menu shortcuts and an uninstaller.
It's based on the `self-contained` variant, so nothing else is needed.
An installed copy checks for new versions on startup and can update itself (also from *Settings* → *About*): it downloads the new installer, verifies it, replaces the old version and starts again.

Alternatively, extract the zip and run `OsmoOverlay.exe`.

The builds aren't code-signed yet, so SmartScreen may warn on first launch - choose *More info* → *Run anyway*.

### Linux
```sh
tar -xzf OsmoOverlay-<version>-linux-x64-self-contained.tar.gz
./OsmoOverlay-<version>-linux-x64-self-contained/OsmoOverlay
```
To add OsmoOverlay (with its icon) to your application menu, run `./install-desktop-entry.sh` from the extracted folder. Use `--remove` to remove the menu entry.
Run it again if you move the folder.

### macOS
Extract the archive and move `OsmoOverlay.app` to *Applications*.
The app isn't signed or notarized yet, so macOS blocks the first launch, usually with "OsmoOverlay is damaged and can't be opened".
The app isn't damaged - remove the quarantine flag macOS puts on downloaded files:
```sh
xattr -dr com.apple.quarantine /Applications/OsmoOverlay.app
```
If you only see a warning about an unidentified developer, right-clicking the app and choosing *Open* works too.
The command-line renderer is inside the bundle: `OsmoOverlay.app/Contents/MacOS/OsmoOverlay.Cli`.

## Supported cameras
Use the original files from the memory card. Footage exported from DJI Mimo has no telemetry.

| Camera                                | Status                  | Read from the recording                                                                     | GPS                                                                |
|:--------------------------------------|:------------------------|:--------------------------------------------------------------------------------------------|:-------------------------------------------------------------------|
| DJI Osmo Action 6, Action 5 Pro       | Tested                  | GPS, speed, tilt, G-force, ISO, shutter speed, white balance                                | DJI GPS Bluetooth Remote Controller, or the phone through DJI Mimo |
| Other DJI Osmo Action (e.g. Action 4) | Untested, should work   | The same                                                                                    | The same                                                           |
| GoPro HERO12 Black                    | Tested (`.MP4`, `.LRV`) | Tilt, G-force, ISO, shutter speed, white balance, audio level, wind noise, image brightness | None - the camera has no GPS, use an external track                |
| Other GoPro with GPS                  | Untested                | The same, plus GPS (GPS5/GPS9, covered by synthetic tests only)                             | Built in                                                           |
| DJI drones (Mavic 2, Mavic Air 2)     | Tested (flight logs)    | Nothing - no telemetry in the MP4, see the flight log below                                 | The flight log                                                     |
| Insta360 X4                           | Experimental (`.insv`)  | Tilt, G-force                                                                               | Not read yet, use an external track                                |

- Without GPS data the widgets that need it are disabled. Before recording with a DJI remote, wait for a satellite fix.
- The recording settings check currently supports only the Osmo Action 6, Action 5 Pro, GoPro HERO12 Black and Mavic Air 2 (identified by the aircraft name in the flight log).
- GoPro: pitch was checked against the video image; roll still needs a sideways tilt test. GoPro MAX (360) is not supported.
- Insta360: a 360° recording is shown as a standard frame with adjustable viewing direction and field of view.
- Audio level, wind noise and image brightness widgets show what the GoPro recorded in its own telemetry.
  They don't analyze sound or picture: the audio bar is a digital level (dBFS, -60 to 0), not sound pressure, and the wind meter is not wind speed.

## DJI drones
A DJI drone MP4 (`DJI_####.MP4`) is recognized by its filename and contains no telemetry, so a flight log is the only telemetry source.
When you analyze the recording, the app looks for a log (`FlightRecord_*.txt`) in the same folder and selects one whose flight time includes the start of the clip.
You can also fetch logs from your phone: connect it by USB, enable *File transfer* and use **Find a flight log on your phone** (Windows only).
The app copies the DJI Fly logs it finds to your computer and automatically matches them to subsequent recordings by time.
The log uses the phone's clock, so check that it belongs to the recording.
You can always add a log manually using **GPS track** in the Source card.

Logs from newer firmware (version 13 and up) are encrypted.
To read them, enter a DJI API key (the SDK key for an Open API app in your DJI developer account at [developer.dji.com](https://developer.dji.com)) in *Settings*, or set `OSMOOVERLAY_DJI_API_KEY`.
Only key blocks from the log's header are sent to DJI, without GPS positions, and the downloaded keys are kept on your computer.
Tested with Mavic 2 and Air 2 logs; other aircraft are unverified.

Height is above the take-off point, not sea level, so the Elevation widget is off in the *DJI drones* and *FPV drones* layouts.
Flight mode names are confirmed only for a few modes, and the remote sticks assume mode 2.

## Adding an external GPS track
Analyze the recording, seek to a recognizable event in the preview and open **GPS track** in the Source card.
Choose the original FIT or GPX file and match the video time to the GPS date and time (UTC).
The point picker lists recorded GPS timestamps and, for FIT, speed.
A second matching point later in the recording corrects clock drift.
Save the project to keep the file reference and alignment.
A DJI flight log is added the same way, and **View a GPS track** on the start screen plays any track on its own.

Motion-based alignment is an approximate suggestion: check it against the video before applying it.
The camera's file date can be wrong even when it falls within the track's date range.
Timer pauses and GPS gaps retain their actual duration on the timeline, with no GPS fix or speed data; a paused timer does not mean the camera was stationary.
If you stop and restart recording, align each recording separately.

FIT is preferable when it contains measured speed and timer events. GPX works too; speed is derived from consecutive positions.
Temperature is imported from both formats when present; FIT also supplies the device's original distance counter and battery level.
Each reading has its own optional widget, disabled when its data is absent. The device's distance counter includes travel before the video starts and sections cut from the video.
GoPro's motion sensor temperature is a separate reading from the camera, not the GPS track. Both temperature widgets support Celsius and Fahrenheit.
Original tracks contain location data and should be shared carefully.

The CLI accepts the same alignment, using video time before cuts:

```sh
OsmoOverlay.Cli recording.MP4 --track ride.fit --track-anchor 18.5=2026-01-01T12:00:00Z -o result.mp4
```

Repeat `--track-anchor` once with a later video/GPS time to correct drift.
`--track-offset -416` instead applies a known offset to the camera's creation time.
Without anchors or an offset, the CLI attempts approximate motion alignment and reports a warning to verify it visually.

## Command line
`OsmoOverlay.Cli` renders what the app renders:

```sh
OsmoOverlay.Cli recording.MP4 -o result.mp4 --cut 1:00-1:10@fade --volume 0:off,2:0 --music "song.mp3;start=5;db=-6;loop;len=60"
OsmoOverlay.Cli ride1.ovproj ride2.ovproj --profile web1080
OsmoOverlay.Cli recording.MP4 --alpha prores
OsmoOverlay.Cli recording.MP4 --export-telemetry ride.gpx --telemetry-interval 1
```

Saved projects (`.ovproj`) are rendered one after another using their saved cuts, overlay, sound, music, GPS track and settings preset.
You can also pass recording files alongside projects. These are rendered after the projects, combined into one recording by default, or separately with `--each`.
A failed render doesn't stop the remaining jobs. Projects with an existing output file are skipped unless you use `--existing overwrite` or `--existing number`.
The queue window also accepts projects and recordings through **Add projects...** and **Add recordings...**.
`--list-presets` lists the preset names accepted by `--settings-preset`, `--layout` and `--profile`. `OsmoOverlay.Cli --help` lists all options.

To check an untested camera, use `--report` to inspect a recording's telemetry (GPS fix, accelerometer scale, tilt range). Add `--expect-lean right|left|nose-up|nose-down` to check that roll and pitch have the expected sign in a recording made with the camera tilted in that direction.
To check a graphics card, `--encoder-test` tests each video encoder with the settings used for rendering and reports ffmpeg's error if it fails.

## Important information
Do not add the overlay in the DJI Mimo app. It re-encodes your footage to 8-bit H.264 at a lower bitrate, which reduces its quality.

| Comparison      | Original footage from the camera    | Rendered with OsmoOverlay                        | Footage exported from DJI Mimo            |
|:----------------|:------------------------------------|:-------------------------------------------------|:------------------------------------------|
| Image quality   | Full quality recorded by the camera | Encoded once more with the camera's own settings | Lower - re-encoded to 8-bit H.264 at a lower bitrate |
| Resolution      | 3840 × 2160 (4K UHD)                | 3840 × 2160 (4K UHD)                             | 3840 × 2160 (4K UHD)                      |
| Video codec     | H.265 / HEVC, Main 10               | H.265 / HEVC, Main 10                            | H.264 / AVC, Baseline                     |
| Color depth     | 10-bit                              | 10-bit                                           | 8-bit                                     |
| Video bitrate   | 89.79 Mb/s                          | 89.79 Mb/s (same as the source)                  | 79.68 Mb/s                                |
| Audio           | AAC-LC, stereo, 48 kHz, 317 kb/s    | Copied unchanged*                                | AAC-LC, stereo, 48 kHz, 128 kb/s          |
| Camera metadata | Preserved                           | Removed by default, can be kept                  | Removed                                   |

\* With parts cut out, the audio is re-encoded at the source's own bitrate, since a copy can't span the joins.

Adding anything to the picture means it has to be encoded again - no program can avoid that.
OsmoOverlay keeps the source's codec, 10-bit color, bitrate, keyframe interval and HEVC level to minimize the visible difference from the original. DJI Mimo instead converts the footage to 8-bit H.264 at a lower bitrate.

## Good to know
- GPS data and the camera's serial number are stored directly in the MP4 file. Be careful who you share it with.
- The same resolution doesn't mean the same quality - codec, color depth and bitrate matter too.

## Requirements
**FFmpeg 9** is required - both the `ffmpeg`/`ffprobe` commands and its shared libraries (used by the live preview).
If it's missing, the app offers to install it on first launch:

| System  | Installed with                                                                        |
|:--------|:--------------------------------------------------------------------------------------|
| Windows | `winget install Gyan.FFmpeg.Shared` (the static `Gyan.FFmpeg` build has no libraries) |
| macOS   | `brew install ffmpeg`                                                                 |
| Linux   | Your package manager (`ffmpeg` on apt/dnf/pacman)                                     |

Many Linux distributions still ship an older FFmpeg - the preview needs version 9.x.

[ExifTool](https://exiftool.org) is optional.
It's only used as a fallback for cameras whose telemetry format the app can't read on its own, and can be installed from *Settings* → *About*.

## Building from source
Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```sh
dotnet run --project OsmoOverlay.Gui                     # run the app
dotnet test --project OsmoOverlay.Tests                  # run the unit tests
dotnet run --project OsmoOverlay.Build                   # build every release package into artifacts/
dotnet run --project OsmoOverlay.Build -- --help         # options: platforms, variant, version, output directory
```
Packages for all platforms can be built on any system (Windows, Linux or macOS).

## Reviews and mentions
- [Creative Content - Australian Videocamera Magazine](https://cre-8.com.au/2026/10/23/review-osmo-overlay)
- [Quadricottero News](https://www.quadricottero.com/2026/10/osmooverlay-arriva-il-software-gratuito.html)
- [Mototour.pl](https://mototour.pl/infomoto/186-art/5919-osmooverlay-darmowy-program-na-kamery-dji-do-telemetrii)
- [VIDEOAKTIV](https://www.videoaktiv.tv/news/editing/osmooverlay-telemetrie-einblendungen-fuer-dji-videos)

## License
OsmoOverlay is free for non-commercial use under the [PolyForm Noncommercial License 1.0.0](LICENSE), with source code on [GitHub](https://github.com/sefinek/osmo-overlay).
You may use, modify and share it under its terms, which also permit uses covered by the **Noncommercial Organizations** section.

Commercial use beyond those permissions, including monetized videos, sponsored content, paid client work and paid rendering or editing services, requires separate permission.
Contact [contact@sefinek.net](mailto:contact@sefinek.net) to agree on terms before starting such use.

Third-party libraries, tools and maps retain their own licenses and terms.
