# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

OsmoOverlay burns a telemetry HUD (speed, heading, tilt, sun, stats) onto footage from DJI Osmo Action cameras without any loss of source quality/codec (see README.md - re-encoding in DJI Mimo degrades quality; this application does not).

Three projects in one `.slnx` (net10.0):
- `OsmoOverlay.Core` - all the logic: telemetry extraction, telemetry processing, overlay renderer (SkiaSharp), ffmpeg pipeline, video preview. No dependency on GUI/CLI.
- `OsmoOverlay.Cli` - thin console wrapper around `RenderJob`.
- `OsmoOverlay.Gui` - Avalonia, overlay editor + live preview + running the render.

## Build

```
dotnet build OsmoOverlay.Core
dotnet build OsmoOverlay.Cli
dotnet build OsmoOverlay.Gui
```

Building the whole `.slnx` also works, but build Core/Cli separately when you're only verifying logic changes - it's faster and doesn't require closing the GUI.

No unit tests in this repo - verification happens through an actual build plus (when it makes sense) manually running the CLI against a real file, or rendering `OverlayRenderer` straight to a PNG via SkiaSharp (bypassing the GUI) to check visual changes.

Real DJI Osmo Action MP4s embed GPS data and the camera's serial number directly in the file. Treat any such sample/test file as sensitive - don't paste its telemetry contents into anything shared externally.

External runtime dependencies: `ffmpeg`/`ffprobe` on PATH (required), `exiftool` (optional fallback, see below). `OsmoOverlay.Core/Dependencies/` checks/installs these in the GUI.

## Telemetry architecture (the most important part to understand)

DJI Osmo Action cameras embed an extra MP4 stream tagged `codec_tag` `djmd` containing protobuf-encoded data: GPS position/velocity, accelerometer XYZ, ISO, shutter speed, color temperature, device name. `SourceProbe.Probe` detects this stream (`SourceInfo.DjmdStreamIndex`/`HasDjmdTrack`).

**Primary path (native)**: `DjiMetaTelemetryParser` manually decodes this protobuf (varints, field/wire-type tags) with no protobuf library at all - the field mapping is documented in the XML comment at the top of the file and was verified byte-for-byte against exiftool across an entire real recording (zero mismatches, DJI Osmo Action 6). `SampleTimeSeconds` is deliberately `index / fps` (from ffprobe), not some raw timestamp in the stream - confirmed that exiftool itself computes it exactly this way too.

**Fallback**: `ExifToolRunner` (calls the external `exiftool`) - used only when the native decoder throws (a different model/firmware may lay out the stream differently) or when the file has no `djmd` stream at all. All the frame-extraction selection logic (native parser vs. exiftool fallback) lives in one place: `TelemetryExtraction.Extract`/`ExtractCombined`. Don't add other call sites that extract telemetry *frames* via `DjiMetaTelemetryParser` or `ExifToolRunner` directly - always go through `TelemetryExtraction`. The one standing exception is `ExifToolRunner.GetCameraModel`, called directly from `FileSummary.cs` when a file has no `djmd` track at all - it's a standalone metadata lookup, not part of the frame-extraction selection.

Both paths share `GpsForwardFill` (a dropped GPS fix in a tunnel/building should "hold" the last known position rather than snap to (0,0)) - if you change the forward-fill logic on one path, change it on both (through this shared file, don't duplicate it).

`TelemetryProcessor.Process` takes raw `TelemetryFrame`s and computes `DerivedFrame`s (speed, heading, gradient, cumulative distance, smoothed tilt/G-force). Key decisions:
- Time windows (speed/heading/gradient) and the EMA smoothing time constants are expressed in **seconds, not sample count** - behavior is meant to stay independent of the given file/camera's telemetry sampling rate.
- Speed prefers `GpsSpeedMs` from the GPS receiver itself (when available) over differentiating position - it isn't affected by GPS position quantization/lag.
- The tilt gauge uses `AccelY` (not `AccelX`) and is negated - confirmed empirically on a controlled test recording (isolated camera tilting, verified by extracting video frames and comparing them against the raw accelerometer values), see the comment next to `rawPitch` in `TelemetryProcessor.cs`. Don't change this without similar verification - it is not an obvious axis choice.

**Cache**: `FileSummaryCache` persists `FileSummary` as JSON to `%LOCALAPPDATA%\OsmoOverlay\cache\<sha256>.json`, keyed by full path + file size + modification time. `FileSummaryCache.FormatVersion` **must be bumped by 1 whenever telemetry extraction/processing logic changes** - otherwise a stale cache entry will silently return data computed with the old logic, making the change look like it didn't work.

**Multi-file recordings**: DJI Osmo Action auto-splits long recordings into consecutive files. `VideoSegments.ProbeAll` probes each input path into a `VideoSegment` (its own `SourceInfo` + `StartOffsetSeconds` on the combined virtual timeline). `ConcatListWriter` writes the temporary ffmpeg concat-demuxer list file shared by both the full render (`FfmpegPipeline`) and the live preview (`VideoFrameSource`), so the two stitch multi-segment recordings identically instead of drifting apart.

**No GPS fix / no GPS timestamp**: many recordings (filmed indoors, GPS never acquired) have telemetry (accelerometer, ISO/shutter/color temp) but no position data at all. `TelemetryProcessor.HasAnyGpsFix`/`HasAnyGpsTimestamp` detect this; `OverlayDataRequirements.IsSupported`/`ApplyAvailability` force off (and the GUI greys out) any widget that needs data this file doesn't have, applied at every point a layout reaches `OverlayRenderer` (`RenderJob`, `PreviewPlayer`) so a widget checked against an earlier, GPS-capable file can't get burned into a render as a placeholder. `DateTimeText`/`UtcTimeText` are the one exception: with no GPS timestamp they still work off a fallback - `SourceInfo.ContainerCreationTimeUtc` (the MP4 container's own `creation_time` tag, written by the camera regardless of GPS) plus per-frame `SampleTimeSeconds`, threaded through as `FileSummary.ContainerRecordingStartUtc`. It's the camera's own clock, not GPS-synced, so the GUI flags it with a ⚠ next to the widget's checkbox instead of showing it as real GPS-recorded time.

## Overlay - data model and renderer

`OverlayRenderer` (SkiaSharp) is fully data-driven: it draws a list of `OverlayElement`s (type + X/Y + visibility) from the active `OverlayPreset`, instead of having positions hardcoded. Per-type sizes/radii live in `OverlayElementBounds` - the single source of truth shared by the renderer (drawing) and the GUI (hit-testing while dragging), so the two can't drift apart.

`OverlayPresetStore` holds multiple named presets in `%LOCALAPPDATA%\OsmoOverlay\overlay-presets.json`, one marked active. Both the GUI (editor) and the CLI/`RenderJob` (which has no editor) use the active preset whenever `RenderOptions.Layout` isn't explicitly supplied. `OverlayPreset.WithMissingDefaultsFilled` backfills any widget type added after a preset was saved (as `Visible: false`, so it can't suddenly appear on an already-arranged layout) - always add a new `OverlayElementType`'s default position/visibility to `OverlayPreset.CreateDefault` so this backfill has something to pull from.

`OverlaySettingsStore` persists cross-preset state (active preset ID, watermark on/off, GPS motion smoothing, live-preview quality) to `%LOCALAPPDATA%\OsmoOverlay\settings.json`. Most of these can't be swapped on a running preview - `PreviewPlayer.SetShowWatermark` flips a draw-time flag and recomposes the last frame in place, but GPS smoothing and preview resolution are baked into the renderer/decoder at `OpenAsync` time, so the GUI (`MainWindow.OnSettingsClick`) has to close and reopen the whole preview for those to take effect.

The speed gauge's scale is dynamic (`ComputeGaugeMaxSpeed` in `OverlayRenderer`) - it rounds up to the nearest ten based on the actual max speed observed in the given recording, not a fixed 60 km/h.

## Live preview (`PreviewPlayer` + Avalonia GUI)

`PreviewPlayer` decodes frames through `VideoFrameSource` (spawns `ffmpeg` per frame request) and composites them with the overlay (`Compose`, `OverlayRenderer.Render`). Two distinct access modes:
- **Scrubbing/seek** (`RequestSeekAsync`): debounce + cooperative cancellation via checking `ct.IsCancellationRequested` (returns `null`), **not** by throwing `OperationCanceledException` - one seek superseding a previous one is a frequent, expected event, not an exceptional one, so exceptions would be needlessly costly here.
- **Dragging an overlay element** (`SetLayout`): does NOT call ffmpeg again - it just swaps `_renderer.Layout` and recomposes the last already-decoded video frame from memory, so dragging stays smooth (dozens of updates/sec without spawning a process).

`VideoFrameSource.GetFrame` retries with backoff, but **only near the end of the file** (`Duration - position <= 1s`) - ffmpeg sometimes needs more than one frame of margin before EOF, depending on keyframe layout/encoder. The retry is deliberately scoped to this case: for a genuinely broken file every position will fail, so retrying would just multiply ffmpeg spawns (and extend how long `PreviewPlayer._lock` is held, blocking playback) before the same error surfaces anyway.

In the GUI (`MainWindow.axaml.cs`), the overlay element list is edited via **copy-on-write**: every change (dragging, a visibility checkbox) builds a new list (`ActiveElements.ToList()`) and swaps the reference on the preset (`ReplaceActiveElements`), instead of mutating the existing list in place - because the playback thread may be concurrently enumerating that same list inside `OverlayRenderer.Render()`. Dragging an element **pauses playback** if it's currently running (`OnOverlayCanvasPointerPressed`).

## Repo-specific conventions

- `ProcessHelper.CreateHidden(command, args)` - always use this instead of manually building a `ProcessStartInfo` for external tools (ffmpeg/ffprobe/exiftool) - it hides the console window and redirects stdout/stderr consistently across the repo.
- `AngleMath` (`DegToRad`/`RadToDeg`/`NormalizeDegrees`) - the only place for angle conversions; don't duplicate `* Math.PI / 180.0` in new code.
- Overlay text style: `DrawOutlined` in `OverlayRenderer` produces a thin outline + soft shadow (not a thick outline) - this is a deliberate visual decision, don't change it without an explicit request.

## Secondary tools (GUI `ToolsWindow`)

A separate window (opened from the main GUI) hosts standalone utilities unrelated to the main render flow:

- **Color tag fix** (`ColorTagFixer`): some NLEs (confirmed on Vegas Pro 2026's MainConcept muxer, HEVC/NVENC export path only - AVC/H.264 export hasn't been tested) leave `color_primaries`/`color_transfer`/`color_space` unset on export even when the project is explicitly Rec.709, causing players to guess the matrix (often wrongly) and shift colors. This remuxes just the container tags (`-c copy`, no re-encode) to force `bt709` when eligible; the fix itself supports both HEVC and H.264 streams regardless of which export path is confirmed affected.
- **Compare videos** (`CompareVideosWindow`): a hand-built comparison table (up to 6 files) of codec/resolution/bitrate/color tags side by side - not a `DataGrid`, this repo has no such dependency and the table's shape doesn't need one. This is what backs the quality-comparison claims in README.md.
- Cache/data folder management: "Open data folder" and "Clear cache" against the same `%LocalAppData%\OsmoOverlay` root that `FileSummaryCache`/`OverlayPresetStore`/`OverlaySettingsStore`/`MapTileFetcher`/NLog each independently combine for their own subfolder.
- **Dependency updates** (`DependencyUpdateWindow`, backed by `DependencyVersionChecker`): checks the installed vs. latest version of ffmpeg/ffprobe and exiftool and offers a per-tool update. Installed version comes from parsing the tool's own `-version`/`-ver` output; latest version comes from whatever package manager `DependencyInstaller` already installs through - `winget show` on Windows, `brew info --json=v2` on macOS, and on Linux a read-only query against the local package index (`apt-cache policy`, `dnf --cacheonly list available`, or `pacman -Si`, whichever manager is present) rather than forcing a metadata refresh. That Linux path's "latest" is therefore only as fresh as the local index, not necessarily true upstream latest. Separate from `DependencyPromptWindow` (startup prompt for a *missing* tool, which only checks presence, not version).
