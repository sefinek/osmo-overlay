using System.Diagnostics;
using System.Threading.Channels;
using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Core;

public sealed record RenderOptions(
	IReadOnlyList<string> InputPaths,
	string OutputPath,
	int? FrameLimit = null,
	string? Encoder = null,
	IReadOnlyList<TelemetryFrame>? TelemetryFrames = null,
	bool Overwrite = true,
	IReadOnlyList<OverlayElement>? Layout = null,
	bool? ShowWatermark = null,
	bool? SmoothGpsMotion = null,
	string? CameraModel = null,
	bool GreenScreen = false)
{
	public static string DefaultOutputPath(IReadOnlyList<string> inputPaths)
	{
		var inputPath = inputPaths[0];
		var dir = Path.GetDirectoryName(inputPath) ?? ".";
		var name = Path.GetFileNameWithoutExtension(inputPath);
		return Path.Combine(dir, $"{name}_overlay.mp4");
	}

	/// <summary>
	///     Derives the green-screen sibling of an already-chosen normal output path (same directory,
	///     "_greenscreen" instead of whatever suffix the normal path used) rather than starting fresh
	///     from the input paths, so it still respects a location the user picked via "..." for the
	///     normal render instead of silently writing somewhere else.
	/// </summary>
	public static string GreenScreenOutputPath(string outputPath)
	{
		var dir = Path.GetDirectoryName(outputPath) ?? ".";
		var name = Path.GetFileNameWithoutExtension(outputPath);
		var ext = Path.GetExtension(outputPath);
		if (name.EndsWith("_overlay", StringComparison.OrdinalIgnoreCase))
			name = name[..^"_overlay".Length];
		return Path.Combine(dir, $"{name}_greenscreen{ext}");
	}
}

public enum RenderPhase
{
	Probing,
	ExtractingTelemetry,
	SelectingEncoder,
	Rendering,
	Done,
	Failed
}

public sealed record RenderStatus(
	RenderPhase Phase,
	string Message,
	int CurrentFrame,
	int TotalFrames,
	TimeSpan Elapsed);

public sealed record RenderResult(bool Success, string? ErrorMessage, TimeSpan Elapsed);

public static class RenderJob
{
	// How many rendered-but-not-yet-written frames the producer may get ahead of ffmpeg by. Each one
	// is a full native-resolution BGRA frame (tens of MB at 4K), so this stays modest - just enough
	// to overlap render and encode, not to buffer a meaningful chunk of the render in memory.
	private const int RenderPrefetchFrames = 3;

	public static Task<RenderResult> RunAsync(RenderOptions options, IProgress<RenderStatus>? progress,
		CancellationToken ct)
	{
		return Task.Run(() => RunAsyncCore(options, progress, ct), ct);
	}

	private static async Task<RenderResult> RunAsyncCore(RenderOptions options, IProgress<RenderStatus>? progress,
		CancellationToken ct)
	{
		var sw = Stopwatch.StartNew();

		void Report(RenderPhase phase, string message, int current = 0, int total = 0)
		{
			progress?.Report(new RenderStatus(phase, message, current, total, sw.Elapsed));
		}

		foreach (var path in options.InputPaths)
			if (!File.Exists(path))
				return new RenderResult(false, $"File not found: {path}", sw.Elapsed);

		try
		{
			Report(RenderPhase.Probing, "Probing source file(s) (ffprobe)...");
			IReadOnlyList<VideoSegment> segments = VideoSegments.ProbeAll(options.InputPaths);
			VideoSegments.Validate(segments);
			VideoSegment first = segments[0];

			Report(RenderPhase.Probing,
				$"{first.Source.Video.CodecName} {first.Source.Video.Profile}, {first.Source.Video.Width}x{first.Source.Video.Height}, " +
				$"{first.Source.Video.FrameRate} fps, {first.Source.Video.PixFmt}, ~{first.Source.Video.BitRate / 1_000_000} Mbps" +
				(segments.Count > 1 ? $" ({segments.Count} segments)" : ""));

			if (!segments.AllHaveDjmdTrack())
				return new RenderResult(false,
					$"{first.InputPath} has no 'djmd' telemetry stream - this is likely a proxy/preview file, not an original DJI Osmo Action recording.",
					sw.Elapsed);

			if (!options.Overwrite && File.Exists(options.OutputPath))
				return new RenderResult(false, $"Output file already exists: {options.OutputPath}", sw.Elapsed);

			IReadOnlyList<TelemetryFrame> rawFrames;
			var cameraModel = options.CameraModel;
			if (options.TelemetryFrames is { Count: > 0 })
			{
				rawFrames = options.TelemetryFrames;
				Report(RenderPhase.ExtractingTelemetry, $"Using {rawFrames.Count} previously extracted telemetry samples.");
			}
			else
			{
				Report(RenderPhase.ExtractingTelemetry, "Extracting telemetry (djmd stream)...");
				TelemetryExtractionResult extraction = TelemetryExtraction.ExtractCombined(segments);
				rawFrames = extraction.Frames;
				cameraModel ??= extraction.CameraModel;
				if (rawFrames.Count == 0)
					return new RenderResult(false, "No telemetry samples found in the file(s).", sw.Elapsed);
				Report(RenderPhase.ExtractingTelemetry, $"Extracted {rawFrames.Count} telemetry samples.");
			}

			OverlaySettings settings = OverlaySettingsStore.Load();
			var smoothGps = options.SmoothGpsMotion ?? settings.SmoothGpsMotion;
			List<DerivedFrame> derived = TelemetryProcessor.Process(rawFrames, smoothGps);
			var startAltitude = derived[0].Raw.AltitudeMeters;
			var maxSpeedKmh = TelemetryProcessor.Summarize(derived).MaxSpeedKmh;

			if (options.Encoder is null)
				Report(RenderPhase.SelectingEncoder, "Checking NVENC availability...");
			var encoder = options.Encoder ?? FfmpegPipeline.SelectVideoEncoder();
			Report(RenderPhase.SelectingEncoder,
				$"Using encoder: {encoder}" +
				(encoder == "libx265" ? " (NVENC unavailable - rendering on CPU)" : " (GPU)"));

			var fps = first.Source.Video.Fps;
			var totalFrames = options.FrameLimit ?? (int)Math.Ceiling(segments.TotalDurationSeconds() * fps);
			double? limitSeconds = options.FrameLimit is not null ? options.FrameLimit.Value / fps : null;
			Report(RenderPhase.Rendering, $"Rendering {totalFrames} frames to {options.OutputPath}...", 0, totalFrames);

			IReadOnlyList<OverlayElement> layout =
				options.Layout ?? LoadActiveLayout(first.Source.Video.Width, first.Source.Video.Height);
			var hasGpsFix = TelemetryProcessor.HasAnyGpsFix(rawFrames);
			// Forces off any widget this file's telemetry can't support (e.g. Map/Compass checked from
			// a previous, GPS-capable file) instead of burning a "--"/0/placeholder into the export -
			// same filter PreviewPlayer applies for the live preview, see OverlayDataRequirements.
			layout = OverlayDataRequirements.ApplyAvailability(layout,
				hasGpsFix, TelemetryProcessor.HasAnyGpsTimestamp(rawFrames),
				first.Source.ContainerCreationTimeUtc is not null);
			var showWatermark = options.ShowWatermark ?? settings.ShowWatermark;
			using var renderer = new OverlayRenderer(first.Source.Video.Width, first.Source.Video.Height,
				startAltitude, layout, derived, maxSpeedKmh, showWatermark, cameraModel,
				first.Source.ContainerCreationTimeUtc, settings.MapTileUrlTemplate, settings.MapAttribution,
				settings.MapShowAttribution, settings.MapApiKey, RouteIntroSettings.From(settings));

			if (layout.Any(e => e is { Type: OverlayElementType.MapWidget, Visible: true }))
			{
				Report(RenderPhase.Rendering, "Fetching map tiles for the route...");
				try
				{
					await renderer.PrepareMapAsync(
						(fetched, total) => Report(RenderPhase.Rendering, $"Fetching map tiles: {fetched}/{total}", fetched, total),
						ct);
				}
				catch (OperationCanceledException)
				{
					return new RenderResult(false, "Cancelled by user.", sw.Elapsed);
				}
			}

			if (settings.ShowRouteIntro && hasGpsFix)
			{
				Report(RenderPhase.Rendering, "Fetching route overview map...");
				try
				{
					await renderer.PrepareRouteIntroMapAsync(
						(fetched, total) => Report(RenderPhase.Rendering, $"Fetching route overview map: {fetched}/{total}", fetched, total),
						ct);
				}
				catch (OperationCanceledException)
				{
					return new RenderResult(false, "Cancelled by user.", sw.Elapsed);
				}
			}

			using Process ffmpeg = FfmpegPipeline.StartRender(options.InputPaths, options.OutputPath, first.Source, encoder,
				options.Overwrite, limitSeconds, options.GreenScreen, totalFrames);
			Report(RenderPhase.Rendering, ProcessHelper.FormatCommand(ffmpeg.StartInfo.FileName, ffmpeg.StartInfo.ArgumentList));

			// `using Process ffmpeg` above only releases managed handles on Dispose - it does NOT
			// terminate the OS process. Every path out of the try below - including one that has
			// nothing to do with cancellation, e.g. a genuine exception thrown by renderer.Render() -
			// must still guarantee ffmpeg.exe is dead before this method returns; otherwise it's
			// orphaned running against a closed stdin, and for a green-screen render (infinite main
			// input, see FfmpegPipeline.StartRender) that can mean forever, not just "a while".
			try
			{
				Stream stdin = ffmpeg.StandardInput.BaseStream;
				Task<string> stderrTask = ffmpeg.StandardError.ReadToEndAsync(ct);

				// stdin.Write below is a plain blocking write with no cancellation of its own - if ffmpeg
				// ever stops draining its input (a genuine hang), that write would otherwise block forever
				// with no way for Cancel to interrupt it. Killing the process here breaks the pipe, which
				// unblocks the write as an IOException (caught below) instead.
				using CancellationTokenRegistration killOnCancel = ct.Register(() => KillFfmpegIfRunning(ffmpeg));

				// Rendering the overlay (CPU-bound SkiaSharp drawing) and feeding ffmpeg (I/O-bound,
				// throttled by however fast the encoder drains its input) are two different bottlenecks -
				// running them on separate threads lets one overlap the other instead of strictly
				// alternating "render a frame, then sit idle waiting for ffmpeg to catch up, repeat".
				// Render() itself stays strictly sequential - see its own doc comment, its cached paint
				// objects aren't safe to call concurrently - only this one producer thread ever calls it.
				var channel = Channel.CreateBounded<byte[]>(
					new BoundedChannelOptions(RenderPrefetchFrames) { SingleReader = true, SingleWriter = true });

				// Linked, not just `ct` directly: if the consumer loop below stops for a reason that has
				// nothing to do with `ct` (e.g. stdin.Write hits an IOException because ffmpeg crashed on
				// its own), the producer can otherwise be left blocked forever on a full channel nobody is
				// draining anymore - awaiting it in the consumer's finally would then hang too. Cancelling
				// this in that finally, unconditionally, guarantees the producer can always be unblocked
				// regardless of why the consumer stopped.
				using var producerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
				CancellationToken producerCt = producerCts.Token;

				Task producer = Task.Run(async () =>
				{
					try
					{
						for (var i = 0; i < totalFrames && !producerCt.IsCancellationRequested; i++)
						{
							DerivedFrame frame = TelemetryProcessor.FindNearest(derived, i / fps);
							var pixels = renderer.Render(frame);
							await channel.Writer.WriteAsync(pixels, producerCt);
						}

						channel.Writer.TryComplete();
					}
					catch (OperationCanceledException)
					{
						channel.Writer.TryComplete();
					}
					catch (Exception ex)
					{
						channel.Writer.TryComplete(ex);
					}
				}, producerCt);

				var written = 0;
				try
				{
					await foreach (var pixels in channel.Reader.ReadAllAsync(ct))
					{
						stdin.Write(pixels, 0, pixels.Length);
						written++;

						if (written % 60 == 0)
							Report(RenderPhase.Rendering, $"Frame {written}/{totalFrames}", written, totalFrames);
					}

					stdin.Flush();
				}
				catch (OperationCanceledException)
				{
					// Observed directly by this loop's own ReadAllAsync(ct) - the cancelled check below
					// (not this catch alone) is what decides whether to report a cancellation, since the
					// producer noticing ct first and completing the channel normally takes this same path
					// without throwing here.
				}
				catch (IOException)
				{
					// ffmpeg's pipe broke - either it died on its own, or killOnCancel above just killed it
					// because of a cancellation already in flight; the checks below sort out which.
				}
				finally
				{
					producerCts.Cancel();
					stdin.Close();

					try
					{
						await producer;
					}
					catch
					{
						// Any real failure was already observed via the channel (ReadAllAsync rethrows it
						// above) - a second throw here would only be a duplicate.
					}
				}

				var cancelled = ct.IsCancellationRequested;
				if (cancelled)
				{
					// Closing stdin alone doesn't make ffmpeg exit promptly - the overlay filter's default
					// eof_action (repeat) just freezes the last overlay frame it got and keeps encoding
					// against whatever's left on the main input regardless of the now-closed pipe: the real
					// source video's remaining length for a normal render, or - far worse - the render's
					// full original frame count for a green-screen render, whose main input is otherwise-
					// infinite (see FfmpegPipeline.StartRender). killOnCancel above already killed the
					// whole process tree the moment ct was cancelled, so this just waits for that to land.
					ffmpeg.WaitForExit();
					try
					{
						await stderrTask;
					}
					catch (Exception)
					{
						// Reading a killed process's stderr can fault in various ways (broken pipe, the
						// cancelled token itself) - none of it matters once this is already reporting a
						// user cancellation, not a render failure.
					}

					return new RenderResult(false, "Cancelled by user.", sw.Elapsed);
				}

				ffmpeg.WaitForExit();
				var stderr = await stderrTask;

				if (ffmpeg.ExitCode != 0)
				{
					var message = $"ffmpeg exited with an error ({ffmpeg.ExitCode}): {stderr}";
					AppLogger.Error(message);
					return new RenderResult(false, message, sw.Elapsed);
				}

				return new RenderResult(true, null, sw.Elapsed);
			}
			finally
			{
				KillFfmpegIfRunning(ffmpeg);
			}
		}
		catch (Exception ex)
		{
			// Single place a render failure is logged/surfaced - callers (GUI, CLI) read it back off
			// RenderResult.ErrorMessage for their own user-facing dialog/console line, but don't log it
			// again themselves, since AppLogger.Error already reaches the file log and (via Notified)
			// the GUI's on-screen panel.
			AppLogger.Error(ex, $"Render failed for {string.Join(", ", options.InputPaths)}: {ex.Message}");
			return new RenderResult(false, ex.Message, sw.Elapsed);
		}
	}

	private static void KillFfmpegIfRunning(Process ffmpeg)
	{
		try
		{
			if (!ffmpeg.HasExited) ffmpeg.Kill(true);
		}
		catch (InvalidOperationException)
		{
		}
	}

	private static IReadOnlyList<OverlayElement> LoadActiveLayout(int width, int height)
	{
		(List<OverlayPreset> presets, var activeId) = OverlayPresetStore.Load(width, height);
		return presets.First(p => p.Id == activeId).Elements;
	}
}
