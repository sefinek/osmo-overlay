# CLAUDE.md

Guidance for Claude Code in this repository. Only what the code doesn't say by itself: invariants, things that must not change, and where a change has to be made in two places. How a feature works is in the code and its XML docs; history and measurements are in git.

## What this is

OsmoOverlay burns a telemetry HUD (speed, heading, tilt, sun, stats) onto footage from DJI Osmo Action, GoPro and Insta360 cameras and DJI drones. The picture is re-encoded once with the source's own codec and settings, so user-facing text never claims "no quality loss" for a render - only the stream-copy Tools may.

Projects in `OsmoOverlay.slnx` (net10.0):
- `Core` - everything that isn't one camera's: telemetry processing, SkiaSharp overlay renderer, ffmpeg pipeline, preview, 360 reframing, `ICameraFormat`. Depends on no GUI/CLI and no camera.
- `Cameras.Dji`, `Cameras.GoPro`, `Cameras.Insta360`, `Dji.Drones` - one maker each (`Dji.Drones`: `DjiDroneFormat`, an `ICameraFormat` with empty frames, and `DjiFlightLogFormat`, an `ITrackFormat`). They see Core's internals (`InternalsVisibleTo`).
- `Cli` (thin wrapper around `RenderJob`), `Gui` (Avalonia), `Tests` (MSTest), `Build` (release packaging, references nothing).

## Build and test

- `dotnet build OsmoOverlay.Core|Cli|Gui` - build Core/Cli alone when verifying logic (doesn't need the GUI closed).
- `dotnet test --project OsmoOverlay.Tests` - MSTest.Sdk on Microsoft.Testing.Platform (`global.json` opts in). Pure logic with synthetic data, never a real recording. The CodeCoverage extension is versioned by `MicrosoftTestingExtensionsCodeCoverageVersion` in the csproj - a `PackageReference Update` has no effect on it.
- Anything touching ffmpeg/ffprobe is verified by a real build plus the CLI on a real file; visual changes by rendering `OverlayRenderer` to a PNG via SkiaSharp.
- `smoke-test.yml` starts the Gui with `--smoke-test` on Windows/Linux/macOS. FFmpeg comes from the `install-ffmpeg` action: Gyan's `full_build-shared` on Windows (`gyan-version` and `gyan-sha256` change together; the hash is the asset's `digest`), BtbN's `n9.0` on Linux (`btbn-build`), Homebrew on macOS. Bump them with `FFmpeg.AutoGen`'s major. Hardware decoding/encoders are not covered.
- `release.yml`: `build` (tests, packages), `verify` (runs the real win-x64 / linux-x64 / osx-arm64 package with `--smoke-test`), `publish` (the **draft** `v<Version>`, only after `verify`, the only job with `contents: write`). An unsigned osx-arm64 binary macOS refuses fails `verify` - that is the check working. Release is run by hand from `main`; the tag exists only once the draft is published.
- Auto Assign runs on `pull_request_target` and must never check out the PR's code. Workflows and XAML carry no comments - reasoning lives here.
- Release packages: `dotnet run --project OsmoOverlay.Build` (`-- --help`). FFmpeg isn't bundled.
- `Directory.Build.props` holds shared settings and `Version` (the one place to bump it). Release builds treat warnings as errors (NuGet audit excepted): keep zero warnings.
- Package versions live only in `Directory.Packages.props`, each written out, never a `$(property)` (Visual Studio's NuGet manager replaces it). A family's packages (Avalonia, SkiaSharp) carry one version (`PackageVersionsTests`).
- The build's date and commit come from `BUILD_DATE` and `GITHUB_SHA`, written into Core's assembly metadata only when `BUILD_DATE` is set (a stamp changing on every local build would recompile everything). `BuildInfo` falls back to Core's file time.

## Installer and updates

- `OsmoOverlay.Build/Installer/OsmoOverlay.iss`: `AppId` never changes; per-user install (`PrivilegesRequired=lowest`), `WizardStyle=classic dark` (keep). The Inno download in `release.yml` is pinned by SHA-256 - change URL and hash together.
- Must match: `AppGuid` in the .iss = `AppUpdates.InstallerGuid`; `AppUpdates.MutexName` = the .iss `AppMutexName`; `ProjectProgId` in the .iss = `ProjectFileAssociation.ProgId`, and the command line (`"exe" "%1"`). The `.ovproj` association is written by both the installer (kept across `/UPDATE`) and Settings.
- In-app update (`AppUpdates`) only for a copy installed by the setup (`CanUpdateInPlace`); the installer asset is checked against its SHA-256. `UpdateChecks` is the one check, once at startup. `SkippedAppUpdate` is set only by "Skip this update"; Cancel asks again.

## Dependencies (external)

- FFmpeg is required as `ffmpeg`/`ffprobe` and as shared libraries matching `FFmpeg.AutoGen`'s major (the preview decodes in-process). The executables start from the install the libraries come from (`DependencyChecker.FromFfmpegLibraryFolder`).
- `exiftool` is optional (`ExternalTool.IsOptional`). Install/update never go past `ExternalTool.SupportedMajorVersion`.
- Windows can't upgrade FFmpeg while its DLLs are loaded: Update closes the app, upgrades in a PowerShell window and restarts it.

## Telemetry

- **Camera formats.** Core knows no camera. An `ICameraFormat` detects its files, extracts telemetry, gives `Gravity` (which way gravity pulls in its accelerometer's axes, null = unknown) and copies its metadata into a render. Registered in both `Program`s (`CameraFormats.Register`, tried in order). `FileSummary.CameraFormatId` is part of the cache - never change a released one. A new camera is a new project; nothing changes in Core. Exception: `SyntheticCamera` (id `synthetic`), found after the registered ones, never listed in `All`.
- Telemetry frames keep the camera's own axes. Core does the physics once (`CameraTilt`, G-meter, `HorizonLeveling`); nothing in Core reads `AccelX/Y/Z` as a direction. `OverlayAvailability` turns widgets off when `Gravity` is null.
- Extract telemetry only through `TelemetryExtraction.ExtractCombined`.
- **Cache**: `FileSummaryCache.FormatVersion` **must be bumped by 1 whenever extraction/processing logic changes**. Entries are Brotli JSON, streamed, never read into a string.
- `TelemetryProcessor.Process`: windows and EMA constants are in **seconds**, not samples. Speed prefers the receiver's `GpsSpeedMs`. Distance skips a step the receiver measured as standing still at both ends. Gradient is over `GradientWindowMeters` of distance, never per second.
- **GPS outliers** (`GpsOutliers`, first in `Process`): what was repaired is shown (log, timelines, GPS alert), never hidden. `CameraTilt` takes the vehicle's acceleration out only with a fix at both ends of its derivative window.
- **Speed correction** (`SpeedCalibration`): applied in `Process` to `DerivedFrame.SpeedKmh` only. Cached summary frames are derived without it - use `SpeedCalibration.Apply` where they're consumed. It uses the cruising speed (`CruisingSpeedKmh`), never a top speed.
- **Tilt**: roll = asin(g_x), pitch = -asin(g_z), vehicle acceleration (from GPS) taken out. DJI's gravity is (-AccelY, -AccelZ, -AccelX), Insta360 X4's (AccelZ, -AccelX, AccelY), GoPro's (AccelX, AccelZ, AccelY) - DJI and Insta360 verified on real recordings, GoPro's pitch on a HERO12 (roll's sign still needs a controlled sideways lean). Don't change axes, signs or compensation without similar verification.
- `OverlayDataRequirements` forces off widgets that need missing data (no GPS fix, no GPS time) wherever a layout reaches the renderer; time widgets fall back to the container's `creation_time`. With no telemetry at all the preview plays with stand-in frames (`PlainRecordingFrames`) and render stays disabled.
- **Multi-file recordings** are joined by *frames* (`VideoSegments.Join`), never by container duration. Audio is joined by container duration (`VideoSegments.LocateAudio`), the render does the same. A file starting over 2 s after the previous one ended gets `StartsAfterGap`; smoothing, windows and routes don't cross it.
- **GPS clock** (`ApplyGpsClock` in `ExtractCombined`): one clock running with the video, continued across splits. Time widgets, `RecordedAtUtc` and the sun all read it.

### DJI Osmo Action (`Cameras.Dji`)
- `djmd` protobuf decoded by hand (`DjiMetaTelemetryParser`, verified against exiftool), read from the MP4 sample table; ffmpeg extraction is the byte-identical fallback. `SampleTimeSeconds` is deliberately `index / fps`. `ExifToolTelemetry` runs only when the native decoder throws. All formats share Core's `GpsForwardFill` (altitude only with a fix) and `Mp4Fields.SampleOffsets`.
- The GPS time string is a whole hour off UTC on the Osmo Action 6. `GpsClockZone` shifts it only when it differs from the camera's clock by whole quarter hours (+-2 min drift). Never shift unconditionally.
- A fix is `DjiGpsFix`: `gps_status` (not 1) and a place on Earth, unit converted (0 radians, 1 degrees).
- `TelemetryFrame.Orientation` (quaternion) is the camera body's, not the levelled picture's. It drives roll, pitch and G-meter only with `OrientationSource.Quaternion` (default: accelerometer, as it isn't yet shown to beat the GPS-compensated tilt), through `OrientationTilt.Apply`; the tilt then takes no GPS compensation. The device clock (header `f1.f2`) is deliberately unread, and `dbgi`'s second quaternion series is `djmd`'s ~1.06 s late. Don't change video timing to either without a controlled recording.

### GoPro (`Cameras.GoPro`)
- Native GPMF from the `gpmd` sample table, one frame per video frame; the sensor clock is aligned on `SHUT`, then `GRAV`, packet timing is the fallback. GRAV supplies gravity (`GoProTilt`, `OrientationGravity`), never a heading.
- GPS5/GPS9 are covered only by synthetic tests (the real HERO12 sample has no receiver). MAX / multiple video streams are unsupported; other models unverified (`SupportNotice`).
- `TMPC` on ACCL is the motion sensor's temperature (`ImuTemperatureCelsius`), never a GPS track's. `GoProDiagnostics` holds the latest sample in its block aligned by STMP; missing stays null. Wind processing is not wind speed; dBFS is not sound pressure.

### External FIT / GPX tracks
- `ExternalTrackReader` reads originals, never exported analysis JSON. `ExternalTrack` interpolates only normal intervals: a gap or timer pause has no fix/speed, proves no standstill and doesn't shorten elapsed UTC time. Battery is held, never interpolated, and only from the camera's own device (device_index 0). Temperature and distance aren't interpolated across gaps. The track's distance never replaces `DerivedFrame.CumulativeDistanceMeters`.
- `TrackSynchronization` maps the recording's time before cuts to GPS UTC with one anchor or two (drift), without needing a camera date. Anchors describe one continuous recording. `--track-offset` is relative to `creation_time` and can't combine with `--track-anchor`.
- `ExternalTrackSync` proposes a motion correlation only; clock overlap alone is never proof. The GUI requires Apply after a proposal, the CLI logs that it needs visual verification.
- `ExternalTrackWindow` (three steps: file, alignment, summary): `ExternalTrackSource.Propose` uses the same order as `Resolve` (recording span, container clock, motion), nothing is applied before the summary's Apply, only a recording span is exact (`NeedsVerification`). `_cameraSummary` stays untouched and the derived summary is never written to the camera cache. `.ovproj` stores the track's local path and anchors and rejects network paths; an unreadable one keeps rendering disabled.
- A GUI load reads its external track once and passes that snapshot to `ExternalTrackSource.Apply` and the source details. `RefreshExternalTrack` only updates controls; it never reads a track. A new load reads the file again.
- The preview toolbar offers full telemetry only when the loaded external track extends beyond the synchronized recording. It uses the same `PreviewPlayer`, `VideoView`, transport, timelines and compositor as the recording preview. `ExternalTrackPreview` prepares bounded 10 Hz telemetry without camera sensors; `ExternalTrackPlayback` carries those frames and the synchronization. `LibavVideoSource` maps track time back to recording time (including clock drift) and supplies an opaque blank frame outside the recording. Do not add a separate player, timer, renderer or transport for this mode. The recording summary, project and saved playhead stay untouched.
- Full telemetry follows the normal quality picker, zoom/pan, layout edits, preset comparison, fullscreen and source-resolution snapshots through the shared preview. Quality changes reuse the prepared track frames and keep its position, rate, loop and playback state. Cuts and key-moment editing return to the matching recording position.

### DJI drones (`Dji.Drones`)
- The flight log (`FlightRecord_*.txt`) is an `ITrackFormat` registered in both `Program`s (`ExternalTrackReader.Register`), tried before FIT/GPX. Layout from the open `dji-log-parser`; byte offsets and what was checked are in `FlightLog`'s docs, verified on real Mavic 2 / Air 2 logs (v14) and a Flight Reader CSV.
- Version 13+ records are AES-encrypted; keys come from DJI's flight record API (`DjiKeychains`), cached per log in `dji-keychains/`. The SDK key is `OverlaySettings.DjiApiKey` (protected on disk like `MapApiKeys`, in `SharedSettings`, never in a preset, `.ovproj` or the keychain cache; env `OSMOOVERLAY_DJI_API_KEY` is the fallback). The request sends key blocks only, not positions. A record whose feature point has no key is skipped, never read as plain xor.
- The drone's MP4 has no telemetry or DJI tag: `DjiDroneFormat` recognises it by name (`DJI_####.MP4`, one video stream; registered after the Osmo format) and gives empty frames, so the log is the only source. `ICameraFormat.Brand` is what the "no telemetry" message lists.
- The log is found by time (`FindFor`, drone formats only): a log in the recording's folder (or the phone-log cache) whose flight covers the clip's start +-60 s, latest start wins; GUI at Get Summary when no track is set, `RenderJob` when options carry none. The log's clock is the app's (a phone's), so make sure a log belongs to the recording. A recording is placed on the log by the camera record (`ExternalTrack.Recordings`) before motion is tried; `--track-offset`/`--track-anchor` still win.
- Height is above take-off, not sea level (altitude = height + the home record's altitude, never GPS altitude), so Elevation is off in the drone presets. Climb rate is computed in `FlightLog.WithVerticalSpeed` from height over a second (the log's own z-speed was never checked). `KeyMoments.IsFlight` swaps climb/descent/lowest point for fastest ascent/descent and uses the Height widget's number as the highest point.
- The log's battery goes to `TelemetryFrame.DroneBattery`, never to `DeviceBatteryPercent` (the FIT/GPX widget). Events (`FlightEventTracker`) land on the first frame at or after them; `FindFlightAlerts` reads the whole frame set so a cut drops them and drawing stays idempotent.
- Voltage alerts match downward crossings observed in the Flight Reader CSV comparison: lowest cell at or below 3.5 V (low) and 3.3 V (critical). Each threshold rearms immediately above itself, without hysteresis or a timer. A jump through both produces only the critical alert. Only fresh valid cell records count; invalid/missing readings cannot prove recovery. These are analysis thresholds, not DJI firmware limits or a fault diagnosis. Widget voltage colors include equality too and have their own configurable limits.
- **Unconfirmed, don't present as fact**: which radio link is the uplink (`FlightEventTracker.OnSignal` assumes "high bit set"); flight mode names are verified only for 6, 10, 31, 33, 41 (`FlightModes`); event rules imitate Flight Reader and are heuristics; the aircraft model is a product-type number with no table (only the owner's name is shown); gimbal (type 3), flight action (OSD byte 37) and 15-byte type 49 records aren't decoded. Remote sticks assume mode 2 (the log has no mode).
- `PhoneFlightLogs` reads the phone through the Windows shell (STA thread; MTP has no drive letter), only from DJI Fly's known folders, changes nothing on the phone and copies to `%LOCALAPPDATA%/OsmoOverlay/flight-logs`. The MTP path was checked by hand only, not by a test.
- `DroneGpsWidth`/`DroneSignalWidth` in the drone preset are measured widths: re-measure them if those widgets change. Flight alert panels are `FlightAlertWidth` wide (never under `FlightAlertMinWidth`) so a stack lines up. Signal and GPS bars ignore `AccentColor`.

### Insta360 (`Cameras.Insta360`)
- `.insv`/`.lrv` telemetry is a trailer after the last MP4 box (`Insta360TrailerParser`). No GPS read yet.
- A 360 recording becomes a flat view (`Core.Reframe`, `FisheyeProjector`), the same C# projector for preview and render, composed in `RenderJob.ProduceComposedAsync`; ffmpeg only encodes it.

## Overlay

- `OverlayRenderer` is data-driven. A new `OverlayElementType` needs its default position/visibility in `OverlayPreset.CreateDefault`; with every widget on none may cover another (`CreateDefault_NoWidgetCoversAnother_AllOfThemOn`) - one off by default goes into a free spot.
- Three built-in read-only presets (`OverlayPresetStore.IsBuiltIn`): `default`, `dji-drones` (`CreateDrone`), `fpv-drones` (`CreateFpv`, built from the default, not the drone layout). Ids never change; names come from `CoreStrings` and are rewritten at every load. `ICameraFormat.PreferredPresetId` names a format's layout; `AdoptCameraLayout` switches on Get Summary only while a built-in is on (and not between the two drone ones), never over a user's own.
- Presets have no migrations or backfills (pre-1.0) - don't add any.
- A widget's `X`/`Y` are in the 3840x2160 reference space; each axis scales on its own (`ToPixels`/`ToReference`), sizes use `GetScale`. Hit-test boxes come from `OverlayRenderer.MeasureElement`, not estimates.
- `BeginElement` is the one place a widget's draw is set up (anchor, scale, slide, fade, `Opacity`); widgets draw around (0, 0) and never do their own Save/Translate/Scale. Widgets that time themselves (progress bar, GPS alert, map credit, watermark) get times through `AsShown`; the GUI locks the matching controls.
- Layers (`OverlayLayers`): the renderer never reads a layer - the layout's order is the draw order. Widget times are on the *output* timeline (after cuts); convert with `OutputTimeline.ToRecordingSeconds`/`NearestOutputSeconds`.
- **Drawing a frame must be idempotent**: a widget's value may depend only on the frame (and the set of frames), never on which frames were drawn before (hence `GMeterDeltas`, `FindGpsAlerts`).
- A new stat widget drawn through `DrawStat` is added to `SupportsTextAlign`, `SupportsStatParts`/`SupportsDecimals` and `ElementStyleEditor`. Widgets whose layout is more than lines (Satellites, Signal, drone battery) are left out of `TextAlign` on purpose. Threshold colors are `ThresholdStatElement`s; the flight alerts' cell limits stay fixed in `FlightEventTracker`.
- Panel widgets implement `IPanelElement`; the Gui's `WithPanel` lists them (a record can't be copied through an interface) - add new ones there.
- Text style (`DrawOutlined`: a thin outline) is deliberate - don't change it without an explicit request; the widget's shadow is the text's only shadow. Shadow (`OverlayRenderer.Shadow.cs`) is centered (offset 0), on by default; `DisableShadows` turns every one off. Shadows dominate drawing time at 4K, hence the parallel rasterization in `DrawWidgets` (`ParallelWidgets_DrawLikeWidgetsDrawnStraightOn`) and the preview-only `PreviewShadows`. Hover/selection never include the shadow.
- GPS alert (`GpsAlertElement`): a loss under `GpsLossMinSeconds` says nothing on screen, but the timelines still mark every loss. A user's own message text is kept as typed; null is the built-in.
- The overlay is drawn premultiplied (Skia's unpremultiplied path is far slower) and converted in `BgraAlpha`.
- `TripStats`: nothing counted across a cut or a gap, moving time from 3 km/h, elevation with a 3 m hysteresis. The summary's max G is the smoothed one the widget shows.
- Sensor widgets (`AmbientTemperature`, `ImuTemperature`, `DeviceDistance`, `DeviceBattery`, `AudioLevel`, `WindProcessing`, `ImageLuma`) use their own nullable readings, not GPS availability; off by default, disabled in the palette and filtered out of rendering without readings. The audio peak is the current recorded value, never accumulated state.
- **Maps**: one provider list (`MapProviders`) and one `MapSourcePicker`. Each provider's terms (`MapTerms`) must be respected: Esri World Imagery is never stored on disk (`CacheDays` 0), tile.openstreetmap.org and Stadia are left out on purpose, the default `satellite` is Esri with its key else Esri's public service (never another provider's key). The credit is its own widget (`MapAttributionElement`); where terms say `WhileMapVisible` it is always drawn - don't let a preset or the GUI hide it.
- Tile downloads share a per-server cooldown after HTTP 429 (or 503 with `Retry-After`): 30 s, doubling to 5 min, never shorter than `Retry-After`; one request tests recovery before the others resume. Shared downloads cancel when their last consumer leaves, so obsolete preview requests don't wake after a cooldown. Tile X wraps around the world; Y outside it is skipped without HTTP.
- **GPS loss on a route**: `GpsLossJoin` (and `RouteIntroGpsLossJoin`) says how a route crosses a stretch without a fix; a recording's first fix is never a resume; `GpsLossTracker` serves both.
- **Route overview card** (`RouteIntro`): the opening widgets' times shift by the card's length (`IntroShiftSeconds`, applied in `AsShown`); the layer clip shows it shifted and stores an edit unshifted. A render starting later than the recording has no card unless `RouteIntroWhenTrimmed`. Decided in `RenderJob` (render) and `PreviewPlayer.RouteIntroFor` (preview). The map is `RouteMapMosaic.View`, a window centered on the route - tiles are fetched for the window, never padded by whole tiles. Automatic zoom never upscales past `RouteIntroMaxUpscale`. The zoom-in draws only the map per frame, the rest of the card is cached. Title/subtitle are the user's text (blank = nothing).
- Settings: `OverlaySettingsStore` (`settings.json`) holds cross-preset state. Secrets (`MapApiKeys`, `CustomMapUrlTemplate`, `DjiApiKey`) go through `SecretProtector` and never into a preset or `.ovproj`. `SettingsWindow` is one `Load`/`Apply` pair - a new setting goes there.
- **Settings presets** (`SettingsPresetStore`, `settings-presets/`): settings.json stays the one place every reader reads; `Activate` copies a preset's values in, every `Save` writes them back (`Sync`). A new `OverlaySettings` field goes into `SettingsPresetValues` (+ `From`/`ApplyTo`, default equal to the settings' own, and `Sanitize` for values read from files) or `SharedSettings` in `SettingsPresetTests` - the test fails otherwise. Machine-bound or secret values are never in a preset. `Default` always exists and can't be renamed, deleted or given a layout. A preset's layout is set only in Settings. The `.ovproj` stores `SettingsPresetId`.
- Interface scale: Linux uses `AVALONIA_GLOBAL_SCALE_FACTOR`; on Windows/macOS every window's content sits in a `LayoutTransformControl`. Code counting device pixels uses `UiScale.DeviceScaling`, not `RenderScaling`.
- Panel widths asked for are kept apart from the ones shown (`FitColumns`), so they come back when the window grows.
- While the summary loads, `WorkingView` replaces `TopGrid` (unless `WorkingAnimation` is off); a render keeps its panels.

## Projects

- `OverlayProject` (`.ovproj`, JSON): `Format` bumps only when old files can't be read. The overlay is an embedded copy of the preset plus its id; a changed or deleted preset asks, and the project's copy is added as its own preset. The external track is applied to the summary first, then the project's overlay, cuts and 360 view before opening the preview at its saved position. Unsaved changes are the difference from `OverlayProject.Fingerprint()` (checked every two seconds): the fingerprint leaves the playhead out, and a field that changes without an edit would show the project as unsaved for good.

## Live preview

- `PreviewPlayer` decodes in-process through FFmpeg's shared libraries, GPU first. **No ffmpeg-process fallback on purpose** (a seek takes seconds); missing libraries are a dependency problem.
- `TelemetryProcessor.MeasuredFrames` reuses summary-derived frames only without GPS smoothing; otherwise it processes the raw frames with smoothing. The GUI shares the measured result between speed statistics and preview preparation, then applies speed correction and orientation once. Preview opening logs media-source, compositor and first-frame times; `Preview UI` and `Recording load` log the surrounding stages to the file log.
- The overlay is composited on the CPU on purpose. All use of `OverlayRenderer` (not thread-safe) goes through the compositor's lock; decoding runs outside it.
- Frame buffers come from one `FrameBufferPool` per recording. A still's `Bgra` is valid only during `FrameReady`; a played frame belongs to the caller until `PlaybackFrameSource.Release`.
- Presentation is driven by the display on the compositor's render thread (`VideoView` -> `TakeDueFrame`), not by a timer, `Task.Delay` or `RequestAnimationFrame`. A stopped playback logs a summary to app.log - the first place to look when it isn't smooth.
- `PlaybackClock` follows the audio device; two sessions never share the decoder or the device. Seeks: latest wins, keyframe while dragging, exact on release; cancellation is a cooperative `null`, not an exception. `PreviewFrames.IndexAt` is the one rule for "which frame is on screen".
- Full screen and the second screen (F9) *move* the viewport (`PreviewViewport`), never mirror it; playback pauses for the move, `_movingViewport` prevents a second one, they are never active together.
- Whatever ends a drag puts back `RestingCursor` (or runs `UpdateHoverState`), never a plain `null`.
- The GUI's overlay element list is **copy-on-write** (`ReplaceActiveElements`): the compose thread may be enumerating it. Dragging an element pauses playback.
- The preview keeps the source's shape even when the export has another (`OutputAspect`): widgets are edited on it.

## Render

- **Range and cuts**: `RenderPlan.Resolve` gives whole kept frames from range and `CutOuts`. The GUI has only cuts; the CLI has `--from/--to/--cut/--frames`. Telemetry follows the cut video (`OutputTimeline.MapFrames`).
- **ffmpeg** (`FfmpegPipeline`) - each of these was found by breaking frame-exactness or sync on real files; don't "simplify" them:
  - No concat `inpoint`/`-ss` (stuck frames); keep the audio list's `-itsoffset` (audio leads by 0.3-0.8 s).
  - Each piece's video gets a one-frame time base and timestamps from the frame number (`settb=den/num,setpts=N`, also a single piece over several files, `FrameNumbered`), or `xfade` rejects the time bases and overlay pairs frames wrong after a join.
  - The overlay's `scale` runs with `threads=0`, and the main picture's format is set before the overlay (hardware decoding gives p010, which overlay can't take) - large speedups, bit-identical output.
  - A piece starting in a later file opens its sound where the container-duration timeline puts it (`SoundFor`). A partial render doesn't copy the camera's data tracks.
- **Cut transitions** (`CutTransition`): fades happen inside the kept frames, so frame count, telemetry and overlay timing never change; crossfade/wipe/push (`Overlaps`) shorten the output. Flat recordings blend in the ffmpeg graph, 360 in C# with the same curves; the preview doesn't blend.
- **Cut captions** never change a frame count; the renderer gets them on the output's timeline so preview and render match.
- **Route across a cut** (`RouteJoin`): each cut carries its own (`FrameRange.Join` -> `TimeRange.Join` -> `RenderPiece.JoinIn` -> `DerivedFrame.CutJoin`), null is `RouteJoins.Default`; there is no global setting. The same default covers a gap between files.
- **Output parity**: the default export matches the source 1:1 (constant bitrate at the source's rate, no B-frames, GOP/level/tier from the source's first file, `hvc1`, exact frame count via `overlay=...:shortest=1`, `creation_time` + timecode). Options may deviate only when the user picks a non-default value - never change the defaults. Resolution, shape (`OutputAspect`), codec and 8-bit go through one place, `OutputVideo.For`. The shape is applied before the resolution; `ResizeFilter` tells a new shape from a scale by `OutputVideo.Reshapes` (1% tolerance, so even rounding isn't one).
- **Encoders** (`SelectVideoEncoder`): the graphics card's first (NVENC, then AMF and Quick Sync on Windows), else x264/x265. Each is probed with the render's own `AddVideoEncoderArgs` at the output's size; only a success is cached, failures keep FFmpeg's error in the file log. `HevcEncodingLimits` keeps a known source level/tier only when the requested bitrate fits; it promotes tier, then level without lowering bitrate (Mavic Air 2 declares Main Tier 5.1 at 122 Mbps, which NVENC rejects). Settings' preset stays NVENC's p1-p7 scale (`PresetName` maps it). Quick Sync is untested.
- **A render's settings** are `RenderOptions.Settings` when set, else the settings file's - a project's settings preset, an export profile or `--settings-preset` never write to `settings.json`. `ProjectRender` turns an `OverlayProject` into `RenderOptions`; the CLI's projects and `RenderQueue` both go through it, so a field added to `OverlayProject` that affects the render is added there too.
- **Render queue**: every job is a project copied into the queue's folder, so a later edit doesn't change what is rendered; only the queue's own copies are ever deleted. A job shows under the project's name, never its copy's GUID. It and a main-window render never run together; items change under the queue's lock because `Progress<T>` can report after the render ended.
- **Transparent overlay** (`AlphaOverlay`): the green screen path's twin - no picture, sound, music or camera metadata - with its own CPU encoder (ProRes 4444, VP9), so no GPU encoder is probed. Colors always BT.709.
- **Volume line** (`VolumeLine`): the preview applies it in `PlaybackSession`, the render as one `volume` filter after the join, which re-encodes the sound; stream copy applies only without a line.
- **Music**: clips are on the *output* timeline (after cuts), stored as edited and cut down only where played (`MusicTrack.Normalize`). A clip's `DuckDb` lowers the recording's sound in the render (`DuckExpression`) and the preview (`MusicMixer.DuckDbAt`) - keep the two in step. Green screen renders carry no music.
- **Camera metadata**: ffmpeg can't mux `djmd`/`dbgi`, so `ICameraFormat.CopyMetadata` does it after ffmpeg at the box level. The serial number (first djmd sample, udta `©uid`) is removed unless explicitly kept.
- **Export marker**: a render's `comment` (`©cmt`) says program, version, `FileSummaryCache.FormatVersion` and tilt source (`ExportMarker`); comparing renders between releases reads it, so keep its shape stable. The camera's udta replaces ffmpeg's after the render, so `Mp4Comment.Carry` moves the comment into it - any other tag ffmpeg writes would be lost the same way.

## Tools and performance test

- `ComparePlayer`: exported videos side by side on one clock; no sound on purpose; views must be attached again at every `PlaybackStarted`. `CameraAudioConverter` has no MP3 on purpose.
- **Performance test** (`RenderBenchmark`) always runs on the generated recording (`SyntheticRecording`, read through `SyntheticCamera`). **Bump `SyntheticRecording.Version` whenever the picture or telemetry changes.** ffmpeg's input options go before any `-map`; frames a second come from `-progress` only. `Advise` suggests only decoding and the preview's shadows - the encoder preset changes quality, so it's never suggested. Only real renders feed `RenderSpeedHistory`; the load is sampled first (`BenchmarkLoad`). Preview measurements leave the route card out (`SuppressRouteIntro`).
- Hardware decoding is checked by a real try on a tiny clip (`HardwareCheck`), since `-hwaccel` alone falls back to the CPU silently.

## Localization

- **Polish is the source language, English is the default.** Write the Polish first (natural word order, no calques, lowercase "ty/twój" in UI), then translate. When changing a text, change the Polish one first.
- GUI texts in `Gui/Resources/Strings*.resx`, Core/camera texts in `Core/Localization/CoreStrings*.resx` (internal). Classes are generated by MSBuild (no Designer.cs); `Strings` must stay public (XAML `{x:Static}`).
- A new user-facing text goes into both files at once (`ResourceParityTests`). Plurals are one resource with forms split by `|` (English `one|other`, Polish `one|few|many`), read through `Plural.Format`.
- `UiLanguage` (null = system's, else English) is applied once in `Program.Main` as the UI culture only - `CurrentCulture` (number formats, ffmpeg args) is untouched. The CLI forces English.
- Overlay built-in texts are `CoreStrings` `Overlay_*`; a user's text is kept as typed in every language; a default is never written into a preset or settings (null = built-in). Resources are in natural case; capitals are applied where drawn.
- Not translated: the CLI's own output, the export comment, file-log-only `AppLogger.Info` lines, ffmpeg/parser exception details, `DeveloperWindow`, the PowerShell update script, the `.ovproj` ProgId description, the performance test's window title.

## Developer tools

Ctrl+Shift+D opens `DeveloperWindow`: every `ConfirmDialog` kind, notifications, sounds, log lines, taskbar states and the first-run windows. A new dialog or notification gets a button there. `DebugMode` (Shift at startup for one run) makes `AppLogger.Info` also raise `Notified` as `Debug`.

## Conventions

- Real recordings embed GPS tracks and the camera serial number. Never commit one (`.gitignore` excludes them) and never paste their telemetry into anything shared externally.
- Namespaces follow the first-level folder only, never a subfolder. `Core`: `OsmoOverlay.Core.<Folder>`, except `Editing`, `Recordings`, `Rendering` and `Support` (plain `OsmoOverlay.Core`). `Gui` is flat `OsmoOverlay.Gui` except `Native`. Tests mirror Core's folders in `OsmoOverlay.Tests.<Folder>`.
- Custom controls' `Render` runs on every playhead move: no new `FormattedText`, brush or geometry per call for the same input (cache it, as `LayerTimeline` and `PreviewTimeline` do). `LiveChart` is the exception: it reads the palette at every draw so a theme change recolors it.
- `ProcessHelper.CreateHidden` for every external tool - never a hand-built `ProcessStartInfo`.
- GUI colors only in the palette (`Resources/Themes/*.axaml`, brushes in `App.axaml`, C# through `Palette`), never a hex literal; every theme defines every key. The overlay's render colors are content, not UI. `LiveChart` lines take `ChartSeries1-4` in order, never status colors, at most two lines. Fluent's colors are aliased to palette brushes in `App.axaml`; read a Fluent control's template in the Avalonia source for the exact version before restyling it.
- A `BrushTransition` from `Transparent` to an opaque color passes through light grey: hover to a translucent white (`SubtleFillBrush`), fade opacity, or turn `Transitions` off.
- GUI icons only in `Icons.axaml`, shown with `IconView` (not `PathIcon`, not Unicode glyphs): a filled shape on a 16x16 grid, never stroked; a hole is a subpath running the other way under `F1`.
- `AngleMath` is the only place for angle conversions.
- Viewing another overlay preset (F8) changes only what the preview draws (`ShownLayout`); any edit or a switch of the active preset ends it.
- `--report --expect-lean` and `--encoder-test` verify a camera or a graphics card the app wasn't verified on (sign of roll and pitch on a recording leaned one known way; each encoder tried as a render would). Quick Sync stays unverified until it passes on an Intel card.
