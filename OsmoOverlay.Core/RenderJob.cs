using System.Diagnostics;
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
	bool? ShowWatermark = null)
{
	public static string DefaultOutputPath(IReadOnlyList<string> inputPaths)
	{
		var inputPath = inputPaths[0];
		var dir = Path.GetDirectoryName(inputPath) ?? ".";
		var name = Path.GetFileNameWithoutExtension(inputPath);
		return Path.Combine(dir, $"{name}_overlay.mp4");
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
			if (options.TelemetryFrames is { Count: > 0 })
			{
				rawFrames = options.TelemetryFrames;
				Report(RenderPhase.ExtractingTelemetry, $"Using {rawFrames.Count} previously extracted telemetry samples.");
			}
			else
			{
				Report(RenderPhase.ExtractingTelemetry, "Extracting telemetry (djmd stream)...");
				rawFrames = TelemetryExtraction.ExtractCombined(segments).Frames;
				if (rawFrames.Count == 0)
					return new RenderResult(false, "No telemetry samples found in the file(s).", sw.Elapsed);
				Report(RenderPhase.ExtractingTelemetry, $"Extracted {rawFrames.Count} telemetry samples.");
			}

			List<DerivedFrame> derived = TelemetryProcessor.Process(rawFrames);
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
			var showWatermark = options.ShowWatermark ?? OverlaySettingsStore.Load().ShowWatermark;
			using var renderer = new OverlayRenderer(first.Source.Video.Width, first.Source.Video.Height,
				startAltitude, layout, derived, maxSpeedKmh, showWatermark);

			if (layout.Any(e => e is { Type: OverlayElementType.MapWidget, Visible: true }))
			{
				Report(RenderPhase.Rendering, "Fetching map tiles for the route...");
				await renderer.PrepareMapAsync(ct);
			}

			using Process ffmpeg = FfmpegPipeline.StartRender(options.InputPaths, options.OutputPath, first.Source,
				encoder, options.Overwrite, limitSeconds);
			Report(RenderPhase.Rendering,
				$"ffmpeg command: {ProcessHelper.FormatCommand(ffmpeg.StartInfo.FileName, ffmpeg.StartInfo.ArgumentList)}");

			Stream stdin = ffmpeg.StandardInput.BaseStream;
			Task<string> stderrTask = ffmpeg.StandardError.ReadToEndAsync(ct);

			var cancelled = false;
			try
			{
				for (var i = 0; i < totalFrames; i++)
				{
					if (ct.IsCancellationRequested)
					{
						cancelled = true;
						break;
					}

					DerivedFrame frame = TelemetryProcessor.FindNearest(derived, i / fps);
					var pixels = renderer.Render(frame);
					stdin.Write(pixels, 0, pixels.Length);

					if (i % 60 == 0)
						Report(RenderPhase.Rendering, $"Frame {i}/{totalFrames}", i, totalFrames);
				}

				stdin.Flush();
			}
			catch (IOException)
			{
				// ffmpeg likely died mid-write - fall through and surface its stderr instead of a bare pipe exception
			}
			finally
			{
				stdin.Close();
			}

			ffmpeg.WaitForExit();
			var stderr = await stderrTask;

			if (cancelled)
				return new RenderResult(false, "Cancelled by user.", sw.Elapsed);

			if (ffmpeg.ExitCode != 0)
				return new RenderResult(false, $"ffmpeg exited with an error ({ffmpeg.ExitCode}): {stderr}",
					sw.Elapsed);

			return new RenderResult(true, null, sw.Elapsed);
		}
		catch (Exception ex)
		{
			AppLogger.Error(ex, $"Render failed for {string.Join(", ", options.InputPaths)}");
			Report(RenderPhase.Failed, $"Error: {ex.Message}");
			return new RenderResult(false, ex.Message, sw.Elapsed);
		}
	}

	private static IReadOnlyList<OverlayElement> LoadActiveLayout(int width, int height)
	{
		(List<OverlayPreset> presets, var activeId) = OverlayPresetStore.Load(width, height);
		return presets.First(p => p.Id == activeId).Elements;
	}
}
