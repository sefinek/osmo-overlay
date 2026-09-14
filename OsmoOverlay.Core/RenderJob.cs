using System.Diagnostics;
using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Core;

public sealed record RenderOptions(
	string InputPath,
	string OutputPath,
	int? FrameLimit = null,
	string? Encoder = null,
	IReadOnlyList<TelemetryFrame>? TelemetryFrames = null,
	bool Overwrite = true,
	IReadOnlyList<OverlayElement>? Layout = null)
{
	public static string DefaultOutputPath(string inputPath)
	{
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

		if (!File.Exists(options.InputPath))
			return new RenderResult(false, $"File not found: {options.InputPath}", sw.Elapsed);

		try
		{
			Report(RenderPhase.Probing, "Probing source file (ffprobe)...");
			SourceInfo source = SourceProbe.Probe(options.InputPath);
			Report(RenderPhase.Probing,
				$"{source.Video.CodecName} {source.Video.Profile}, {source.Video.Width}x{source.Video.Height}, " +
				$"{source.Video.FrameRate} fps, {source.Video.PixFmt}, ~{source.Video.BitRate / 1_000_000} Mbps");

			if (!source.HasDjmdTrack)
				return new RenderResult(false,
					$"{options.InputPath} has no 'djmd' telemetry stream - this is likely a proxy/preview file, not an original DJI Osmo Action recording.",
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
				rawFrames = TelemetryExtraction.Extract(options.InputPath, source).Frames;
				if (rawFrames.Count == 0)
					return new RenderResult(false, "No telemetry samples found in the file.", sw.Elapsed);
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

			var fps = source.Video.Fps;
			var totalFrames = options.FrameLimit ?? (int)Math.Ceiling(source.DurationSeconds * fps);
			double? limitSeconds = options.FrameLimit is not null ? options.FrameLimit.Value / fps : null;
			Report(RenderPhase.Rendering, $"Rendering {totalFrames} frames to {options.OutputPath}...", 0, totalFrames);

			IReadOnlyList<OverlayElement> layout = options.Layout ?? LoadActiveLayout(source.Video.Width, source.Video.Height);
			using var renderer = new OverlayRenderer(source.Video.Width, source.Video.Height, startAltitude, layout,
				maxSpeedKmh);
			using Process ffmpeg = FfmpegPipeline.StartRender(options.InputPath, options.OutputPath, source, encoder,
				options.Overwrite, limitSeconds);

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

			Report(RenderPhase.Done, $"Done: {options.OutputPath}", totalFrames, totalFrames);
			return new RenderResult(true, null, sw.Elapsed);
		}
		catch (Exception ex)
		{
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
