# CLAUDE.md

Guidance for Claude Code in this repository. Only what the code doesn't say by itself.

## What this is

OsmoOverlay burns a telemetry HUD (speed, heading, tilt, sun, stats) onto footage from DJI Osmo Action and Insta360 cameras. The picture is re-encoded once with the source's own codec and settings, so user-facing text never claims "no quality loss" for a render - only the stream-copy Tools may.

Projects in `OsmoOverlay.slnx` (net10.0):
- `Core` - everything that isn't one camera's: telemetry processing, SkiaSharp overlay renderer, ffmpeg pipeline, preview, 360 reframing, `ICameraFormat`. Depends on no GUI/CLI and no camera.
- `Cameras.Dji`, `Cameras.Insta360` - one camera each, implementing `ICameraFormat`. They see Core's internals (`InternalsVisibleTo`).
- `Cli` (thin wrapper around `RenderJob`), `Gui` (Avalonia), `Tests` (MSTest), `Build` (release packaging, references nothing).

Shared settings (framework, company, `Version` - the one place to bump it, output paths) live in `Directory.Build.props`; a csproj holds only what's its own. Release builds treat warnings as errors (NuGet audit warnings excepted), so keep the build at zero warnings. Package versions live only in `Directory.Packages.props` (central package management); Dependabot proposes updates weekly.

## Build and test

- `dotnet build OsmoOverlay.Core|Cli|Gui` - build Core/Cli alone when verifying logic (faster, doesn't need the GUI closed).
- `dotnet test --project OsmoOverlay.Tests` - MSTest.Sdk on Microsoft.Testing.Platform (`global.json` opts in; required on .NET 10). Pure logic with synthetic data, never a real recording. The implicit CodeCoverage extension is versioned by `MicrosoftTestingExtensionsCodeCoverageVersion` in the csproj - a `PackageReference Update` has no effect on it.
- Anything touching ffmpeg/ffprobe is verified by a real build plus the CLI on a real file; visual changes by rendering `OverlayRenderer` to a PNG via SkiaSharp.
- Release packages: `dotnet run --project OsmoOverlay.Build` (`-- --help`). Per RID, self-contained and framework-dependent; zip on Windows, tar.gz elsewhere (written by the tool so executables keep +x); Inno Setup 7 installer for win-x64/arm64. FFmpeg isn't bundled.
- CI (`.github/workflows`): every push to `main` and every PR builds the `.slnx` in Release and tests on Windows/Linux/macOS with the SDK from `global.json`. Release is run by hand and creates a **draft** `v<Version>`; the tag exists only once the draft is published. Workflows carry no comments - reasoning lives here.

## Installer and updates

- Installer script: `OsmoOverlay.Build/Installer/OsmoOverlay.iss`. `AppId` never changes. Per-user install (`PrivilegesRequired=lowest`, no override), `WizardStyle=classic dark` (keep). Dependencies are the app's job, not the installer's. The Inno download in `release.yml` is pinned by SHA-256 - update URL and hash together.
- In-app update (`Core/Updates/AppUpdates.cs`): latest GitHub release, installer asset checked against its SHA-256, run with `/UPDATE /SILENT`. Only for a copy installed by the setup (`CanUpdateInPlace`); other copies get the release page.
- These three must match: `AppGuid` in the .iss, `AppUpdates.InstallerGuid`, and the mutex (`AppUpdates.MutexName` = the .iss `AppMutexName`).
- The `.ovproj` association is written twice with the same keys: the installer (`AssociateProjects` task, checked by default, kept across `/UPDATE` because the old uninstaller deletes the keys) and Settings' Interface tab (`ProjectFileAssociation`). `ProjectProgId` in the .iss and `ProjectFileAssociation.ProgId` must match, as must the command line (`"exe" "%1"`).
- GUI: `UpdateChecks` is the one check (app + dependencies), run once at startup; only Settings' "Check for updates" reruns it. A declined app version is stored in `OverlaySettings.SkippedAppUpdate`.

## Dependencies (external)

- FFmpeg is required, as `ffmpeg`/`ffprobe` and as shared libraries matching `FFmpeg.AutoGen`'s major (the preview decodes in-process). `ffmpeg`/`ffprobe` are started from the install the libraries come from (`DependencyChecker.FromFfmpegLibraryFolder`, via `ProcessHelper`), so one FFmpeg install is used everywhere. On Windows the installer uses `Gyan.FFmpeg.Shared`.
- `exiftool` is optional (`ExternalTool.IsOptional`): the startup prompt appears only when a required tool is missing; optional ones are installed from Settings' About tab or `OsmoOverlay.Cli --install-dependencies`.
- Install/update never go past `ExternalTool.SupportedMajorVersion`: winget gets an explicit `--version`, a newer major is only reported.
- Windows can't upgrade FFmpeg while its DLLs are loaded - Update closes the app, upgrades in a PowerShell window and restarts it (`DependencyInstaller.UpgradeNeedsRestart`).

## Telemetry

- **Camera formats.** Core knows no camera. An `ICameraFormat` detects its files, extracts telemetry, says which way gravity pulls in its accelerometer's axes (`Gravity`, null = unknown) and copies its own metadata into a render. Formats are registered in both `Program`s (`CameraFormats.Register`, tried in order). `FileSummary.CameraFormatId` is part of the cache - never change a released one. A new camera = a new project, nothing changes in Core.
- Telemetry frames keep the camera's own accelerometer axes. Core does the physics once for every camera (`CameraTilt`, G-meter, `HorizonLeveling`); nothing in Core reads `AccelX/Y/Z` as a direction. `OverlayAvailability` turns widgets off when `Gravity` is null.
- Extract telemetry only through `TelemetryExtraction.Extract`/`ExtractCombined`; don't add other call sites.
- **Cache**: `FileSummaryCache.FormatVersion` **must be bumped by 1 whenever extraction/processing logic changes**, or a stale entry hides the change.
- `TelemetryProcessor.Process`: windows and EMA time constants are in **seconds**, not samples (independent of sampling rate). Speed prefers the receiver's `GpsSpeedMs` over differentiating position.
- **Tilt**: `CameraTilt` over `Gravity` - roll = asin(g_x), pitch = -asin(g_z), with the vehicle's own acceleration (from GPS) taken out. DJI's gravity is (-AccelY, -AccelZ, -AccelX), Insta360 X4's is (AccelZ, -AccelX, AccelY) - all verified empirically on real recordings. Don't change axes, signs or compensation without similar verification.
- **No GPS fix / no GPS time**: `OverlayDataRequirements` forces off widgets that need missing data at every point a layout reaches the renderer. `DateTimeText`/`UtcTimeText` fall back to the container's `creation_time` (the camera's own clock, flagged in the GUI).
- **No telemetry at all** (a file no camera format knows): the preview still plays it with stand-in frames (`PlainRecordingFrames`) and an empty layout; render stays disabled.
- **Multi-file recordings**: files are joined back to back. A file starting over 2 s after the previous one ended by the GPS clock gets `TelemetryFrame.StartsAfterGap`, and smoothing/windows/routes don't run across it. Without GPS time the files stay joined.

### DJI Osmo Action (`Cameras.Dji`)
- Telemetry is a `djmd` protobuf stream, decoded by hand (`DjiMetaTelemetryParser`, field mapping in its XML comment; verified byte-for-byte against exiftool). `SampleTimeSeconds` is deliberately `index / fps`, as exiftool computes it.
- `ExifToolTelemetry` is the fallback only when the native decoder throws; the choice is in `DjiOsmoFormat.ExtractTelemetry`. Both paths share `GpsForwardFill` - keep it one file.

### Insta360 (`Cameras.Insta360`)
- `.insv`/`.lrv` telemetry is a trailer after the last MP4 box (ffprobe doesn't see it); `Insta360TrailerParser` documents the layout. No GPS read yet. CameraInfo is unavailable (no ISO/color temperature).
- A 360 recording becomes a flat 16:9 view (`Core.Reframe`, `FisheyeProjector`): the same C# projector for preview and render (ffmpeg's v360 rebuilds its map on every change - 5x slower). Lens FOV 190 was found on a real X4. `HorizonLeveling` keeps the horizon level from the accelerometer; another model may need its own `Gravity`.
- The render of a 360 recording is composed in `RenderJob.ProduceComposedAsync`; ffmpeg only encodes it and takes the sound from the source files.

## Overlay

- `OverlayRenderer` is data-driven: it draws the active preset's `OverlayElement`s. Add a new `OverlayElementType`'s default position/visibility to `OverlayPreset.CreateDefault`. The default preset is read-only in the GUI.
- Presets have no migrations or backfills for older formats (pre-1.0) - don't add any.
- A widget's `X`/`Y` are in the 3840x2160 reference space, not the video's pixels, so a preset fits every resolution and aspect ratio; each axis scales on its own (`OverlayElementBounds.ToPixels`/`ToReference`), sizes use the one `GetScale`. The renderer and the editor convert at the edge - the editor itself works in video pixels. `CreateDefault` is resolution-independent.
- Widget boxes for hit-testing come from `OverlayRenderer.MeasureElement`, not estimates; `OverlayElementBounds.GetBounds` is only a fallback.
- `BeginElement` (`OverlayRenderer.Animation.cs`) is the one place each widget's draw is set up (anchor, scale, slide, fade); widgets draw around (0, 0) and never do their own Save/Translate/Scale. Timing (`AppearAtSeconds`, in/out animation) is generic for every type: the GUI uses one `ElementTimingEditor` and one `ElementStyleEditor`.
- Layers (`OverlayLayers`): the renderer never reads a layer - the layout's order is the draw order. Mute/solo go through `OverlayLayers.Drawn`. Widget times are on the output timeline (after cuts); the timeline shows the recording, so convert with `OutputTimeline.ToRecordingSeconds`/`NearestOutputSeconds`.
- Drawing a frame must be idempotent: a widget's value may depend only on the frame (and the set of frames), never on which frames were drawn before it - a paused or re-composed still draws the same frame again. That's why the G-meter's smoothing is precomputed per frame (`GMeterDeltas`) instead of an EMA updated while drawing.
- Overlay text style (`DrawOutlined`: thin outline + soft shadow) is deliberate - don't change it without an explicit request.
- The overlay is drawn premultiplied (Skia's unpremultiplied path is ~10x slower) and converted to straight alpha in `BgraAlpha`.
- Statistics (`TripStats`): nothing counted across a cut or a gap, moving time from 3 km/h, elevation with a 3 m hysteresis (the plain sum gave 3-10x too much).
- Map tile servers: one list (`MapProviders`), picked per consumer (`MapWidgetElement.MapProviderId`, `OverlaySettings.RouteIntroMapProvider`) through the one `MapSourcePicker` control. What's shared lives once in `OverlaySettings`: API keys per `KeyGroup` (all CARTO styles share one key) and the custom server. `MapSources` turns an id into a URL/credit; the renderer fetches one mosaic for the first visible Map widget, so other Map widgets draw from it.
- Settings: `OverlaySettingsStore` (`settings.json`) holds cross-preset state; presets are one file each in `%LOCALAPPDATA%\OsmoOverlay\presets`.
- Interface scale: Linux uses `AVALONIA_GLOBAL_SCALE_FACTOR`; on Windows/macOS every window's content sits in a `LayoutTransformControl` and popups use the overlay layer. Code counting device pixels uses `UiScale.DeviceScaling`, not `RenderScaling`.

## Projects

- `OverlayProject` (`.ovproj`, JSON, `Format` bumps only when old files can't be read): recordings, output path, cuts (frames), 360 view, preview frame and the overlay. The overlay is an embedded copy of the preset plus its id: an identical preset is simply activated (edits keep going to it); a changed or deleted one asks, and the project's copy is added as its own preset. Applied after Get Summary (`MainWindow.Project.cs`), since cuts and the view need a loaded recording. No unsaved-changes tracking. Opened from a command-line argument too (`MainWindow.StartupProject`).

## Live preview

- `PreviewPlayer` decodes in-process through FFmpeg's shared libraries (`LibavLoader`; major must match `FFmpeg.AutoGen`), GPU first. **No ffmpeg-process fallback on purpose** (0.3-1.7 s per seek); missing libraries are a dependency problem.
- The overlay is composited on the CPU on purpose (~1 ms). All use of `OverlayRenderer` (not thread-safe) goes through the compositor's lock; decoding runs outside it.
- Frame buffers come from one `FrameBufferPool` per recording. A still's `Bgra` is valid only during the `FrameReady` callback; a played frame belongs to the caller until `PlaybackFrameSource.Release`.
- Presentation is driven by the display on the compositor's render thread (`VideoView` -> `TakeDueFrame`), not by a timer, `Task.Delay` or `RequestAnimationFrame` (uneven, late frames measured). Each stopped playback logs a summary to app.log - the first place to look when playback isn't smooth.
- `PlaybackClock` follows the audio device (smoothed against its stepping); a stopwatch without audio. Two sessions never share the decoder or the device (`_sessionsStopped`).
- Seeks: latest wins; keyframe while dragging, exact on release. Cancellation is a cooperative `null`, not an exception.
- `PreviewFrames.IndexAt` is the one rule for "which frame is on screen", shared by the decoder and the GUI's `CurrentFrame()`.
- Full screen preview (`MainWindow.PreviewFullscreen.cs`): the viewport (`PreviewViewport`) is moved into a window and back, never mirrored - a played frame goes to exactly one `VideoView`, and a `VideoView` starts empty when attached, so playback pauses for the move and the frame is shown again after it. The monitor is `OverlaySettings.PreviewMonitor` (`MonitorChoice`).
- In the GUI the overlay element list is **copy-on-write** (`ReplaceActiveElements`) because the compose thread may be enumerating it. Dragging an element pauses playback.

## Render

- **Range and cuts**: `RenderPlan.Resolve` gives whole kept frames from `RenderOptions.RangeStart/End` and `CutOuts`. The GUI has only cuts (I/O marks, X/Delete, `CutList`), no From/To; the CLI has `--from/--to/--cut/--frames`. Telemetry follows the cut video (`OutputTimeline.MapFrames` processes the whole recording once, then keeps kept pieces and re-sums distance). Routes cross a cut as `RouteJoin` says.
- **ffmpeg** (`FfmpegPipeline`): one piece = `AddSourceInputs` (plain `-ss`, plus a concat input only if it runs into later files); several pieces = `AddCutInputs` (each trimmed to its exact frame count, joined with the concat filter, audio re-encoded to AAC). Verified frame-exact on real files. Don't switch video to concat's `inpoint`/`-ss` (stuck frames) or drop the audio list's `-itsoffset` (audio leads by ~0.3-0.8 s). A partial render doesn't copy the camera's data tracks.
- **Cut transitions** (`CutTransition`, per cut): a fade through black or white, half out of the part before the cut and half into the one after, both inside the kept frames - so the frame count, telemetry and overlay timing never change, and the overlay itself isn't faded. `RenderPlan.Resolve` puts it on `RenderPiece.TransitionIn/Out` (none for a cut at the very start/end). Flat recordings fade in the ffmpeg graph (`fade`/`afade` in `AddCutInputs`), pictures made in C# (360 render, preview) through `CutTransitionFade`, the same curve. Crossfade/wipe/push (`CutTransition.Overlaps`) instead show the end of the earlier part and the start of the later one together and so shorten the output: `RenderPiece.OverlapIn` (at most half of the shorter part) is the frames a piece shares with the one before it, `RenderPlan.TotalFrames` and `OutputTimeline` count them once, telemetry through the overlap is the earlier part's (`MapFrames` skips the later part's head). ffmpeg joins such pieces one after another with `xfade`/`acrossfade` (the rest with `concat`), the 360 render with `CutTransitionBlend` (same directions and progress i/n as xfade, checked against ffmpeg's output). The preview doesn't blend: it plays the earlier part through the overlap and then goes on after the later part's head (`NextKeptStretch`), so its time stays the render's. Verified frame-exact on a real file (the 360 path only by tests).
- **Output parity**: the default export matches the source 1:1 (constant bitrate at the source's rate, no B-frames, GOP/level/tier read from the source, `hvc1`, exact frame count via `overlay=...:shortest=1`, `creation_time` + timecode). Export options may deviate only when the user picks a non-default value - never change the defaults.
- **Camera metadata**: ffmpeg can't mux `djmd`/`dbgi`, so `ICameraFormat.CopyMetadata` does it after ffmpeg at the box level (`Mp4CameraMetadata`, `Mp4FastStart`). The serial number lives only in the first djmd sample (protobuf f1.1.5) and in udta `©uid`; unless explicitly kept (default: no) both are removed.

## Tools (`ToolsWindow`)

Standalone utilities, unrelated to the render flow: `ColorTagFixer` (remux to bt709 tags, no re-encode), `MetadataStripper` (share-safe copy, verified against the source before it replaces `.partial`), `CameraAudioConverter` (Osmo's `.AAC` to WAV/M4A, verified by sample MD5; no MP3 on purpose), `CompareVideosWindow` (hand-built table, no DataGrid dependency), data folder and cache management. Dependency versions are shown in Settings' About tab (`DependencyStatusRows`), not here.

## Conventions

- Real recordings embed GPS tracks and the camera serial number. Never commit one (`.gitignore` excludes them) and never paste their telemetry into anything shared externally.
- `Core` folders (namespace stays `OsmoOverlay.Core`): `Editing` (cuts, history, project file, markers), `Rendering` (render job, plan, output timeline), `Recordings` (file summary, its cache, telemetry extraction), `Support` (process/file helpers, time text), plus the feature folders. Tests mirror them.
- `Gui` folders: `Windows` (`Main` is the main window's partials - the shell, `Preview` for playback/timeline, `Editing` for the overlay, layers and cuts), `Controls`, `Resources` (icons, palette), `Helpers`, `Updates`, `Native`. The namespace stays flat (`OsmoOverlay.Gui`) whatever the folder, so `x:Class` and `xmlns:local` don't change when a file moves.
- `ProcessHelper.CreateHidden` for every external tool (ffmpeg/ffprobe/exiftool) - never a hand-built `ProcessStartInfo`.
- GUI colors only in the `App.axaml` palette (`{StaticResource ...Brush}`, C# through `Palette`), never a hex literal; shared classes (`card`, `hint`, ...) are global there; `ScrollViewer` has `AllowAutoHide=False` globally. The overlay's own render colors are content, not UI.
- GUI icons only in `Icons.axaml`, shown with `IconView` (not `PathIcon`, not Unicode glyphs).
- `AngleMath` is the only place for angle conversions.
