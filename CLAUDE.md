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

External runtime dependencies: `ffmpeg`/`ffprobe` on PATH (required), `exiftool` (optional fallback, see below). `OsmoOverlay.Core/Dependencies/` checks/installs these in the GUI.

## Telemetry architecture (the most important part to understand)

DJI Osmo Action cameras embed an extra MP4 stream tagged `codec_tag` `djmd` containing protobuf-encoded data: GPS position/velocity, accelerometer XYZ, ISO, shutter speed, color temperature, device name. `SourceProbe.Probe` detects this stream (`SourceInfo.DjmdStreamIndex`/`HasDjmdTrack`).

**Primary path (native)**: `DjiMetaTelemetryParser` manually decodes this protobuf (varints, field/wire-type tags) with no protobuf library at all - the field mapping is documented in the XML comment at the top of the file and was verified byte-for-byte against exiftool across an entire real recording (zero mismatches, DJI Osmo Action 6). `SampleTimeSeconds` is deliberately `index / fps` (from ffprobe), not some raw timestamp in the stream - confirmed that exiftool itself computes it exactly this way too.

**Fallback**: `ExifToolRunner` (calls the external `exiftool`) - used only when the native decoder throws (a different model/firmware may lay out the stream differently) or when the file has no `djmd` stream at all. All the selection logic lives in one place: `TelemetryExtraction.Extract`. Don't add other call sites that invoke `DjiMetaTelemetryParser` or `ExifToolRunner` directly - always go through `TelemetryExtraction`.

Both paths share `GpsForwardFill` (a dropped GPS fix in a tunnel/building should "hold" the last known position rather than snap to (0,0)) - if you change the forward-fill logic on one path, change it on both (through this shared file, don't duplicate it).

`TelemetryProcessor.Process` takes raw `TelemetryFrame`s and computes `DerivedFrame`s (speed, heading, gradient, cumulative distance, smoothed tilt/G-force). Key decisions:
- Time windows (speed/heading/gradient) and the EMA smoothing time constants are expressed in **seconds, not sample count** - behavior is meant to stay independent of the given file/camera's telemetry sampling rate.
- Speed prefers `GpsSpeedMs` from the GPS receiver itself (when available) over differentiating position - it isn't affected by GPS position quantization/lag.
- The tilt gauge uses `AccelY` (not `AccelX`) and is negated - confirmed empirically on a controlled test recording (isolated camera tilting, verified by extracting video frames and comparing them against the raw accelerometer values), see the comment next to `rawPitch` in `TelemetryProcessor.cs`. Don't change this without similar verification - it is not an obvious axis choice.

**Cache**: `FileSummaryCache` persists `FileSummary` as JSON to `%LOCALAPPDATA%\OsmoOverlay\cache\<sha256>.json`, keyed by full path + file size + modification time. `FileSummaryCache.FormatVersion` **must be bumped by 1 whenever telemetry extraction/processing logic changes** - otherwise a stale cache entry will silently return data computed with the old logic, making the change look like it didn't work.

## Overlay - data model and renderer

`OverlayRenderer` (SkiaSharp) is fully data-driven: it draws a list of `OverlayElement`s (type + X/Y + visibility) from the active `OverlayPreset`, instead of having positions hardcoded. Per-type sizes/radii live in `OverlayElementBounds` - the single source of truth shared by the renderer (drawing) and the GUI (hit-testing while dragging), so the two can't drift apart.

`OverlayPresetStore` holds multiple named presets in `%LOCALAPPDATA%\OsmoOverlay\overlay-presets.json`, one marked active. Both the GUI (editor) and the CLI/`RenderJob` (which has no editor) use the active preset whenever `RenderOptions.Layout` isn't explicitly supplied.

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
