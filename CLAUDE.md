# CLAUDE.md

Guidance for Claude Code in this repository. Only what the code doesn't say by itself: invariants, things that must not change, and where a change has to be made in two places. How a feature works is in the code and its XML docs; history and measurements are in git.

## What this is

OsmoOverlay burns a telemetry HUD (speed, heading, tilt, sun, stats) onto footage from DJI Osmo Action and Insta360 cameras. The picture is re-encoded once with the source's own codec and settings, so user-facing text never claims "no quality loss" for a render - only the stream-copy Tools may.

Projects in `OsmoOverlay.slnx` (net10.0):
- `Core` - everything that isn't one camera's: telemetry processing, SkiaSharp overlay renderer, ffmpeg pipeline, preview, 360 reframing, `ICameraFormat`. Depends on no GUI/CLI and no camera.
- `Cameras.Dji`, `Cameras.Insta360` - one camera each, implementing `ICameraFormat`. They see Core's internals (`InternalsVisibleTo`).
- `Cli` (thin wrapper around `RenderJob`), `Gui` (Avalonia), `Tests` (MSTest), `Build` (release packaging, references nothing).

## Build and test

- `dotnet build OsmoOverlay.Core|Cli|Gui` - build Core/Cli alone when verifying logic (faster, doesn't need the GUI closed).
- `dotnet test --project OsmoOverlay.Tests` - MSTest.Sdk on Microsoft.Testing.Platform (`global.json` opts in; required on .NET 10). Pure logic with synthetic data, never a real recording. The CodeCoverage extension is versioned by `MicrosoftTestingExtensionsCodeCoverageVersion` in the csproj - a `PackageReference Update` has no effect on it.
- Anything touching ffmpeg/ffprobe is verified by a real build plus the CLI on a real file; visual changes by rendering `OverlayRenderer` to a PNG via SkiaSharp.
- Release packages: `dotnet run --project OsmoOverlay.Build` (`-- --help`). FFmpeg isn't bundled.
- Shared settings (framework, company, `Version` - the one place to bump it) live in `Directory.Build.props`. Release builds treat warnings as errors (NuGet audit excepted): keep the build at zero warnings.
- Package versions live only in `Directory.Packages.props` (central package management), each written out - never a `$(property)` (Visual Studio's NuGet manager replaces it with a number). A family's packages (Avalonia, SkiaSharp) carry one version (`PackageVersionsTests`). Dependabot groups each family into one PR.
- CI builds the `.slnx` in Release and tests on Windows/Linux/macOS. Release is run by hand from `main` and creates a **draft** `v<Version>`; the tag exists only once the draft is published. Auto Assign runs on `pull_request_target` and must never check out the PR's code. Workflows carry no comments - reasoning lives here.

## Installer and updates

- Installer script: `OsmoOverlay.Build/Installer/OsmoOverlay.iss`. `AppId` never changes. Per-user install (`PrivilegesRequired=lowest`, no override), `WizardStyle=classic dark` (keep). The Inno download in `release.yml` is pinned by SHA-256 - update URL and hash together.
- These three must match: `AppGuid` in the .iss, `AppUpdates.InstallerGuid`, and the mutex (`AppUpdates.MutexName` = the .iss `AppMutexName`).
- The `.ovproj` association is written twice with the same keys: the installer (`AssociateProjects` task, kept across `/UPDATE`) and Settings (`ProjectFileAssociation`). `ProjectProgId` in the .iss and `ProjectFileAssociation.ProgId` must match, as must the command line (`"exe" "%1"`).
- In-app update (`Core/Updates/AppUpdates.cs`) only for a copy installed by the setup (`CanUpdateInPlace`); the installer asset is checked against its SHA-256. `UpdateChecks` is the one check, run once at startup. `OverlaySettings.SkippedAppUpdate` is set only by the dialog's "Skip this update"; Cancel asks again at the next start.

## Dependencies (external)

- FFmpeg is required as `ffmpeg`/`ffprobe` and as shared libraries matching `FFmpeg.AutoGen`'s major (the preview decodes in-process). The executables are started from the install the libraries come from (`DependencyChecker.FromFfmpegLibraryFolder`), so one install is used everywhere.
- `exiftool` is optional (`ExternalTool.IsOptional`); the startup prompt appears only when a required tool is missing.
- Install/update never go past `ExternalTool.SupportedMajorVersion` (winget gets an explicit `--version`).
- Windows can't upgrade FFmpeg while its DLLs are loaded: Update closes the app, upgrades in a PowerShell window and restarts it.

## Telemetry

- **Camera formats.** Core knows no camera. An `ICameraFormat` detects its files, extracts telemetry, says which way gravity pulls in its accelerometer's axes (`Gravity`, null = unknown) and copies its own metadata into a render. Formats are registered in both `Program`s (`CameraFormats.Register`, tried in order). `FileSummary.CameraFormatId` is part of the cache - never change a released one. A new camera is a new project; nothing changes in Core. The one exception is `SyntheticCamera` (id `synthetic`, the performance test's recording): found by `Detect`/`Find` after the registered ones, never listed in `All`.
- Telemetry frames keep the camera's own accelerometer axes. Core does the physics once for every camera (`CameraTilt`, G-meter, `HorizonLeveling`); nothing in Core reads `AccelX/Y/Z` as a direction. `OverlayAvailability` turns widgets off when `Gravity` is null.
- Extract telemetry only through `TelemetryExtraction.ExtractCombined`; don't add other call sites.
- **Cache**: `FileSummaryCache.FormatVersion` **must be bumped by 1 whenever extraction/processing logic changes**, or a stale entry hides the change. Entries are Brotli-compressed JSON, streamed, never read into a string; unused ones are deleted at startup (`CacheCleanupDays`).
- `TelemetryProcessor.Process`: windows and EMA time constants are in **seconds**, not samples. Speed prefers the receiver's `GpsSpeedMs` over differentiating position. Distance skips a step the receiver measured as standing still at both ends. Gradient is over `GradientWindowMeters` of distance, never per second (GPS altitude noise).
- **GPS outliers** (`GpsOutliers`, first in `Process`): repaired fixes are filled in by time between accepted samples; what was repaired is shown, not hidden (log, timelines, GPS alert). Don't weaken it to hide the repair. `CameraTilt` takes the vehicle's acceleration out only with a fix at both ends of its derivative window.
- **Speed correction** (`SpeedCalibration`): applied in `Process` to `DerivedFrame.SpeedKmh` only (tilt, heading, distance keep measured values), so every consumer agrees. Cached summary frames are derived without it - use `SpeedCalibration.Apply` where they're consumed. The calculator uses the cruising speed (`CruisingSpeedKmh`), never a top speed: the receiver's per-sample speed jitters and a maximum is where the GPS read highest.
- **Tilt**: `CameraTilt` over `Gravity` - roll = asin(g_x), pitch = -asin(g_z), vehicle acceleration (from GPS) taken out. DJI's gravity is (-AccelY, -AccelZ, -AccelX), Insta360 X4's is (AccelZ, -AccelX, AccelY) - verified empirically on real recordings. Don't change axes, signs or compensation without similar verification.
- **No GPS fix / no GPS time**: `OverlayDataRequirements` forces off widgets that need missing data wherever a layout reaches the renderer. Time widgets fall back to the container's `creation_time`.
- **No telemetry at all**: the preview still plays with stand-in frames (`PlainRecordingFrames`) and an empty layout; render stays disabled.
- **Multi-file recordings** are joined by *frames* (`VideoSegments.Join`: frames / fps), never by container duration, which runs on past the last frame (+26 ms on a real Osmo Action 6 split). Audio is joined by container duration (`PlaybackSegment.DurationSeconds`, `VideoSegments.LocateAudio`) - the render does the same. A file starting over 2 s after the previous one ended by the GPS clock gets `StartsAfterGap`; smoothing, windows and routes don't cross it.
- **GPS clock**: `ExtractCombined` turns the receiver's whole seconds into one clock running with the video (`ApplyGpsClock`), continued across splits and restarted only after a stop. Time widgets, `RecordedAtUtc` and the sun all read this one clock.

### DJI Osmo Action (`Cameras.Dji`)
- Telemetry is a `djmd` protobuf stream decoded by hand (`DjiMetaTelemetryParser`; field mapping in its XML doc, verified against exiftool). Samples are read from the MP4 sample table; ffmpeg extraction is the fallback and byte-identical. `SampleTimeSeconds` is deliberately `index / fps`, as exiftool computes it.
- `ExifToolTelemetry` is the fallback only when the native decoder throws (`DjiOsmoFormat.ExtractTelemetry`). Both paths share `GpsForwardFill` - keep it one file; it takes altitude only with a fix.
- The GPS time string is a whole hour off UTC on the Osmo Action 6. `GpsClockZone` moves it onto the camera's clock only when the two differ by whole quarter hours (+-2 min drift). Never shift it unconditionally - in winter the offset may be 0.
- A fix is judged by `DjiGpsFix`: `gps_status` (not 1) and a place on Earth, with the unit (0 radians, 1 degrees) converted - the first coordinate field is the unit, not a fix type. `TelemetryFrame.Orientation` (quaternion, djmd `f2.f9`, 25 Hz in pairs) and `DeviceTimeMicroseconds` (header `f1.f2`, runs ~47 ppm slow against `index / fps`) are only read: nothing in Core uses them. The quaternion's convention (verified on Osmo Action 6 rides): accelerometer ~ `-(R20, R21, R22)` of the standard rotation matrix; it is not yet shown to beat `CameraTilt`, and `dbgi`'s second quaternion series is the `djmd` one ~1.06 s late. Don't swap the HUD's tilt or video timing to either without a controlled recording.

### Insta360 (`Cameras.Insta360`)
- `.insv`/`.lrv` telemetry is a trailer after the last MP4 box (ffprobe doesn't see it); `Insta360TrailerParser` documents the layout. No GPS read yet; CameraInfo is unavailable.
- A 360 recording becomes a flat 16:9 view (`Core.Reframe`, `FisheyeProjector`): the same C# projector for preview and render (ffmpeg's v360 is 5x slower). Lens FOV 190 was found on a real X4. The 360 render is composed in `RenderJob.ProduceComposedAsync`; ffmpeg only encodes it.

## Overlay

- `OverlayRenderer` is data-driven: it draws the active preset's `OverlayElement`s. A new `OverlayElementType` needs its default position/visibility in `OverlayPreset.CreateDefault`; with every widget on, none may cover another (`CreateDefault_NoWidgetCoversAnother_AllOfThemOn`) - one that's off by default goes into a free spot. The default preset is read-only in the GUI.
- Presets have no migrations or backfills for older formats (pre-1.0) - don't add any.
- A widget's `X`/`Y` are in the 3840x2160 reference space, not video pixels; each axis scales on its own (`OverlayElementBounds.ToPixels`/`ToReference`), sizes use `GetScale`. Hit-testing boxes come from `OverlayRenderer.MeasureElement`, not estimates.
- `BeginElement` is the one place each widget's draw is set up (anchor, scale, slide, fade); widgets draw around (0, 0) and never do their own Save/Translate/Scale. Timing and animation are generic for every type (`ElementTimingEditor`, `ElementStyleEditor`). Widgets that time themselves (progress bar with speed gate/arrival, GPS alert, map credit, watermark) get their times through `AsShown`; the GUI locks the matching timing controls (`LockAppear`, `LockDisappear`, `TimingFixed`).
- Layers (`OverlayLayers`): the renderer never reads a layer - the layout's order is the draw order. Widget times are on the *output* timeline (after cuts); the timeline shows the recording, so convert with `OutputTimeline.ToRecordingSeconds`/`NearestOutputSeconds`.
- **Drawing a frame must be idempotent**: a widget's value may depend only on the frame (and the set of frames), never on which frames were drawn before. That's why the G-meter's smoothing is precomputed (`GMeterDeltas`) and GPS alert notices come from the whole frame set once (`FindGpsAlerts`).
- Text style (`DrawOutlined`: a thin outline, nothing else) is deliberate - don't change it without an explicit request. The widget's shadow is the text's only shadow.
- Widget shadow (`OverlayRenderer.Shadow.cs`): centered (offset 0, CSS's `0 0 10px`, not a 3D drop), on by default. `OverlaySettings.DisableShadows` turns every shadow off (preview, snapshot, render) and locks the controls. Shadows are ~85% of the overlay's drawing time at 4K, hence the parallel rasterization in `DrawWidgets` (output equal to straight drawing within +-2 a channel, `ParallelWidgets_DrawLikeWidgetsDrawnStraightOn`) and the preview-only switch `PreviewShadows`. Don't make hover/selection include the shadow.
- Panel widgets implement `IPanelElement`; the Gui's `WithPanel` lists them (a record can't be copied through an interface) - add a new panel widget there too. "Apply to all" is in `MainWindow.WidgetAppearance.cs`.
- GPS alert (`GpsAlertElement`): shown only while the GPS has something to say; a loss under `GpsLossMinSeconds` says nothing on screen, but the timelines still mark every loss. Fixed width per font so it doesn't jump. A user's own message text is kept as typed; null means the built-in.
- The overlay is drawn premultiplied (Skia's unpremultiplied path is ~10x slower) and converted to straight alpha in `BgraAlpha`.
- Statistics (`TripStats`): nothing counted across a cut or a gap, moving time from 3 km/h, elevation with a 3 m hysteresis. The summary's max G is the smoothed one the widget shows.
- **Maps**: one provider list (`MapProviders`), picked through one `MapSourcePicker`. Each provider carries its terms (`MapTerms`, checked against the providers' own); respect them - Esri World Imagery is never stored on disk (`CacheDays` 0), tile.openstreetmap.org and Stadia are left out on purpose, the default `satellite` is Esri with its key else Esri's public service (never another provider's key). The credit is its own widget (`MapAttributionElement`) and where a provider's terms say `WhileMapVisible` it is drawn the whole time - don't let a preset or the GUI hide it.
- **Route overview card** (`RouteIntro`): the opening widgets' times shift by the card's length (`IntroShiftSeconds`, applied in `AsShown`); the layer clip shows it shifted and stores an edit unshifted. A render starting later than the recording has no card unless `RouteIntroWhenTrimmed`. The render decides in `RenderJob`, the preview in `PreviewPlayer.RouteIntroFor`.
- Settings: `OverlaySettingsStore` (`settings.json`) holds cross-preset state. Secrets (`MapApiKeys`, `CustomMapUrlTemplate`) go through `SecretProtector` / `MapSecrets` and never into a preset or `.ovproj`. `SettingsWindow` is one `Load`/`Apply` pair - a new setting goes there.
- Interface scale: Linux uses `AVALONIA_GLOBAL_SCALE_FACTOR`; on Windows/macOS every window's content sits in a `LayoutTransformControl`. Code counting device pixels uses `UiScale.DeviceScaling`, not `RenderScaling`.

## Projects

- `OverlayProject` (`.ovproj`, JSON): `Format` bumps only when old files can't be read. The overlay is an embedded copy of the preset plus its id; a changed or deleted preset asks, and the project's copy is added as its own preset. Applied after Get Summary (`MainWindow.Project.cs`). No unsaved-changes tracking.

## Live preview

- `PreviewPlayer` decodes in-process through FFmpeg's shared libraries (`LibavLoader`), GPU first. **No ffmpeg-process fallback on purpose** (0.3-1.7 s per seek); missing libraries are a dependency problem.
- The overlay is composited on the CPU on purpose. All use of `OverlayRenderer` (not thread-safe) goes through the compositor's lock; decoding runs outside it.
- Frame buffers come from one `FrameBufferPool` per recording. A still's `Bgra` is valid only during `FrameReady`; a played frame belongs to the caller until `PlaybackFrameSource.Release`.
- Presentation is driven by the display on the compositor's render thread (`VideoView` -> `TakeDueFrame`), not by a timer, `Task.Delay` or `RequestAnimationFrame`. Each stopped playback logs a summary to app.log - the first place to look when playback isn't smooth.
- `PlaybackClock` follows the audio device; two sessions never share the decoder or the device. Seeks: latest wins, keyframe while dragging, exact on release; cancellation is a cooperative `null`, not an exception.
- `PreviewFrames.IndexAt` is the one rule for "which frame is on screen", shared by the decoder and the GUI.
- Full screen and the second screen (F9) *move* the viewport (`PreviewViewport`) between windows, never mirror it - a played frame goes to exactly one `VideoView`. Playback pauses for the move; `_movingViewport` keeps one move from starting another. They are never active together.
- The GUI's overlay element list is **copy-on-write** (`ReplaceActiveElements`) because the compose thread may be enumerating it. Dragging an element pauses playback.

## Render

- **Range and cuts**: `RenderPlan.Resolve` gives whole kept frames from `RenderOptions.RangeStart/End` and `CutOuts`. The GUI has only cuts (no From/To); the CLI has `--from/--to/--cut/--frames`. Telemetry follows the cut video (`OutputTimeline.MapFrames`).
- **ffmpeg** (`FfmpegPipeline`) - these were each found by breaking frame-exactness or sync on real files; don't "simplify" them:
  - Don't switch video to concat's `inpoint`/`-ss` (stuck frames); don't drop the audio list's `-itsoffset` (audio leads by ~0.3-0.8 s).
  - Each piece's video gets a one-frame time base and timestamps from the frame number (`settb=den/num,setpts=N`), also a single piece over several files (`FrameNumbered`): otherwise `xfade` rejects the time bases or overlay pairs frames wrong after a join.
  - The overlay's `scale` runs with `threads=0`, and the main picture's format is set before the overlay (hardware decoding gives p010, which overlay can't take) - both are large speedups with bit-identical output.
  - A piece starting in a later file opens its sound where the container-duration timeline puts it (`SoundFor`).
  - A partial render doesn't copy the camera's data tracks.
- **Cut transitions** (`CutTransition`): fades happen inside the kept frames, so frame count, telemetry and overlay timing never change. Crossfade/wipe/push (`Overlaps`) shorten the output (`RenderPiece.OverlapIn`). Flat recordings blend in the ffmpeg graph, 360 and the preview in C# (`CutTransitionFade`/`CutTransitionBlend`, same curves as ffmpeg's). The preview doesn't blend; its time stays the render's.
- **Cut captions** (`CutCaption`) never change a frame count; the renderer gets them on the output's timeline so preview and render draw the same. The CLI's `--cut` has no caption syntax.
- **Output parity**: the default export matches the source 1:1 (constant bitrate at the source's rate, no B-frames, GOP/level/tier from the source's first file, `hvc1`, exact frame count via `overlay=...:shortest=1`, `creation_time` + timecode). Export options may deviate only when the user picks a non-default value - never change the defaults. Resolution, codec and 8-bit go through one place, `OutputVideo.For` (unset = the source's own `VideoInfo`).
- **Encoders** (`SelectVideoEncoder`): the graphics card's first (NVENC, then on Windows AMF and Quick Sync), else x264/x265. Each is probed with the render's own `AddVideoEncoderArgs` at the output's size; only a success is cached. AMF and Quick Sync get their own D3D11 device on the right vendor's card (`EncoderDeviceArgs`, `GpuAdapters.Pick`); hardware decoding runs on that device. Settings' preset stays NVENC's p1-p7 scale (`PresetName` maps it). Quick Sync is untested. The CLI's `--encoder` forces one.
- **Disk space**: the GUI checks the output drive before a render (`RenderDiskSpace`) and asks before going on with less.
- **Camera metadata**: ffmpeg can't mux `djmd`/`dbgi`, so `ICameraFormat.CopyMetadata` does it after ffmpeg at the box level. The camera serial number lives in the first djmd sample and in udta `©uid`; both are removed unless explicitly kept (default: no).

## Tools and performance test

- `ToolsWindow`: standalone utilities unrelated to the render flow (`ColorTagFixer`, `MetadataStripper`, `CameraAudioConverter` - no MP3 on purpose, `CompareVideosWindow`, data and cache management).
- **Performance test** (`BenchmarkWindow`, Core `RenderBenchmark`): always runs on the generated test recording (`SyntheticRecording`, three 4K 10-bit HEVC files with `SyntheticTelemetry` beside them, read through `SyntheticCamera`, so the real path is exercised); with "Also test my recording" it repeats decoding and a short render on a sample of the loaded one. **Bump `SyntheticRecording.Version` whenever the picture or the telemetry changes.** ffmpeg's input options go before any `-map`. Frames a second are steady state from `-progress` reports only. `RenderBenchmark.Advise` suggests only decoding and the preview's shadows - the encoder preset changes quality, so it's shown, never suggested. Only an applied suggestion is saved; a run under load would skew `RenderSpeedHistory`, which only real renders feed. The load is sampled first (`BenchmarkLoad`) and decoding isn't suggested when it was busy. The preview measurements leave the route overview card out (`SuppressRouteIntro`) and keep auto quality, the second screen and the shortcuts off the player (`_previewTestRunning`).
- The first-run window's "Your computer" step (`HardwareCheck`, thresholds in `HardwareReport`) rates the system; hardware decoding is a real try on a tiny clip, since `-hwaccel` alone falls back to the CPU silently.

## Localization

- **Polish is the source language, English is the default.** Wording is written in Polish first (natural word order, no calques, lowercase "ty/twój" in UI); English is translated from it. When changing a text, change the Polish one first.
- GUI texts in `Gui/Resources/Strings*.resx`, Core/camera texts that reach the user in `Core/Localization/CoreStrings*.resx` (internal). The classes are generated by MSBuild, there's no Designer.cs; the `Strings` class must stay public (XAML uses `{x:Static}`).
- A new user-facing text goes into both files at once (`ResourceParityTests`). Plurals are one resource with forms split by `|` (English `one|other`, Polish `one|few|many`), read through `Plural.Format`.
- The language is `OverlaySettings.UiLanguage` (null = system's, else English), applied once in `Program.Main` as the UI culture only - `CurrentCulture` (number formats, ffmpeg args) is untouched. The CLI forces English.
- The overlay's built-in texts are `CoreStrings` `Overlay_*`; a text the user set is kept as typed in every language. A default is never written into a preset or settings (null = built-in). Resources are in natural case; what's drawn in capitals is uppercased where it's drawn.
- Not translated: file-log-only `AppLogger.Info` lines, ffmpeg/parser exception details, `DeveloperWindow`, the PowerShell update script, the `.ovproj` ProgId description, the performance test's window title.

## Developer tools

Ctrl+Shift+D in the main window opens `DeveloperWindow`: every `ConfirmDialog` kind, notifications, sounds, log lines, taskbar states and the first-run windows with sample text. A new dialog or notification gets a button there. Debug mode (`OverlaySettings.DebugMode`; Shift at startup turns it on for that run) makes `AppLogger.Info` also raise `Notified` as `Debug`.

## Conventions

- Real recordings embed GPS tracks and the camera serial number. Never commit one (`.gitignore` excludes them) and never paste their telemetry into anything shared externally.
- `Core` folders keep the namespace `OsmoOverlay.Core`; `Gui` folders keep the flat namespace `OsmoOverlay.Gui` (so `x:Class` and `xmlns:local` don't change when a file moves). Tests mirror Core's folders.
- `ProcessHelper.CreateHidden` for every external tool - never a hand-built `ProcessStartInfo`.
- GUI colors only in the palette (`Resources/Themes/*.axaml`, brushes in `App.axaml`, C# through `Palette`), never a hex literal; every theme defines every key. The overlay's own render colors are content, not UI. Chart lines (`LiveChart`) take `ChartSeries1-4` in order, never status colors, and a chart carries at most two lines.
- Fluent's own colors are replaced by aliasing its resource keys to palette brushes in `App.axaml`. Before restyling a Fluent control, read its template in the Avalonia source for the exact version.
- A `BrushTransition` from `Transparent` to an opaque color passes through light grey: such a control hovers to a translucent white (`SubtleFillBrush`), fades a layer's opacity, or has its `Transitions` off.
- XAML carries no comments - reasoning lives here.
- GUI icons only in `Icons.axaml`, shown with `IconView` (not `PathIcon`, not Unicode glyphs): a filled shape on a 16x16 grid, never stroked; a hole is a subpath running the other way under `F1`.
- `AngleMath` is the only place for angle conversions.
