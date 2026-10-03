using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Mapping;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Core;

public enum BenchmarkStage
{
	Preparing,
	Decode,
	Overlay,
	Pipe,
	Encode,
	Render
}

/// <summary>The step running and how many of the run's steps came before it - Total 0 while it's still being worked out.</summary>
public sealed record BenchmarkProgress(BenchmarkStage Stage, int Done, int Total);

/// <summary>What the benchmark runs on: the loaded recording (InputPaths null = a synthetic 4K clip) and the layout a render would draw.</summary>
public sealed record BenchmarkInput(
	IReadOnlyList<string>? InputPaths,
	IReadOnlyList<TelemetryFrame>? TelemetryFrames,
	IReadOnlyList<DerivedFrame>? Frames,
	IReadOnlyList<OverlayElement> Layout,
	int PreviewWidth);

/// <summary>Repeated runs of one measurement (frames a second): their median, the slowest and the fastest.</summary>
public sealed record BenchmarkStat(double Median, double Min, double Max, int Runs)
{
	/// <summary>Runs further apart than this (relative to the median) aren't a quiet computer's - something else was busy meanwhile.</summary>
	public const double UnstableSpread = 0.1;

	public double Spread => Median > 0 ? (Max - Min) / Median : 0;
	public bool IsUnstable => Runs > 1 && Spread > UnstableSpread;

	/// <summary>Null when no run gave a number.</summary>
	public static BenchmarkStat? Of(IEnumerable<double?> runs)
	{
		List<double> values = [.. runs.OfType<double>().Where(v => double.IsFinite(v) && v > 0).Order()];
		return values.Count == 0 ? null : new BenchmarkStat(MedianOf(values), values[0], values[^1], values.Count);
	}

	internal static double MedianOf(IReadOnlyList<double> sorted)
	{
		int middle = sorted.Count / 2;
		return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
	}
}

/// <summary>Milliseconds a frame took: the typical one (median) and the slowest one in twenty (95th percentile).</summary>
public sealed record FrameTimes(double MedianMs, double P95Ms)
{
	public double Fps => MedianMs > 0 ? 1000 / MedianMs : 0;

	public static FrameTimes Of(IReadOnlyList<double> milliseconds)
	{
		if (milliseconds.Count == 0) return new FrameTimes(0, 0);

		List<double> sorted = [.. milliseconds.Order()];
		return new FrameTimes(BenchmarkStat.MedianOf(sorted), sorted[Math.Max(0, (int)Math.Ceiling(sorted.Count * 0.95) - 1)]);
	}
}

/// <summary>LimitedByDecoding: the encoder was fed by the decoder and kept up with it - it's at least this fast, maybe more.</summary>
public sealed record EncoderSpeed(string Encoder, string? Preset, BenchmarkStat? Speed, bool LimitedByDecoding);

/// <summary>Every measurement of a run - frames a second, or milliseconds a frame for the overlay; null where a step couldn't run.</summary>
public sealed record BenchmarkResult(
	BenchmarkSystem System,
	BenchmarkLoad Load,
	VideoInfo Source,
	VideoInfo Output,
	bool Synthetic,
	BenchmarkStat? HardwareDecode,
	BenchmarkStat? SoftwareDecode,
	FrameTimes Overlay,
	FrameTimes OverlayPlain,
	int PreviewWidth,
	int PreviewHeight,
	FrameTimes PreviewOverlay,
	FrameTimes PreviewOverlayPlain,
	BenchmarkStat? Pipe,
	IReadOnlyList<EncoderSpeed> Encoders,
	BenchmarkStat? HardwareRender,
	BenchmarkStat? SoftwareRender,
	TimeSpan Duration)
{
	public double Fps => Source.Fps;

	/// <summary>Some measurement's runs disagreed - the computer wasn't left alone for the test.</summary>
	public bool IsUnstable =>
		new[] { HardwareDecode, SoftwareDecode, Pipe, HardwareRender, SoftwareRender }.Concat(Encoders.Select(e => e.Speed)).Any(s => s?.IsUnstable == true);
}

public enum BenchmarkAdviceKind
{
	SoftwareDecode,
	HardwareDecode,
	PreviewShadowsOff,
	PreviewShadowsOn
}

/// <summary>A setting worth changing: Gain is how much faster it is (0.4 = 40%), or for the preview's shadows how many times.</summary>
public sealed record BenchmarkAdvice(BenchmarkAdviceKind Kind, double Gain);

/// <summary>A render's slowest stage with the settings in use, and how close a real render came to it (null without one).</summary>
public sealed record BenchmarkBottleneck(BenchmarkStage Stage, double Fps, double? RenderFps)
{
	/// <summary>The real render over its slowest stage - well below 1, the stages slowed each other down sharing the CPU and GPU.</summary>
	public double? Efficiency => RenderFps / Fps;
}

/// <summary>
///     Measures each stage a render goes through on this computer - decoding (on the GPU and on the CPU), drawing the overlay
///     (with and without the widgets' shadows, at the render's and the preview's size), handing frames to ffmpeg, encoding (each
///     NVENC preset and the CPU encoder) and, for a recording with telemetry, a short real render either way of decoding - and
///     says which settings would be faster here (Advise) and which stage holds a render back (Bottleneck). Every ffmpeg step
///     runs several times, alternating which of the compared ones goes first so a computer warming up favors neither, after a
///     warm-up run; a result is the runs' median with their spread. Measured for the output Settings ask for (OutputVideo).
///     Nothing is saved: a run under load would skew the main window's estimate, which only real renders feed.
/// </summary>
public static class RenderBenchmark
{
	private const int DecodeFrames = 480;
	private const int DecodeRuns = 3;
	private const int NvencFrames = 240;
	private const int CpuEncodeFrames = 24;
	private const int EncodeRuns = 2;
	private const int PipeFrames = 240;
	private const int PipeWarmUpFrames = 30;
	private const int PipeRuns = 2;
	// RenderJob reports its progress every 60 frames - this many give a few reports at any frame rate.
	private const int RenderFrames = 240;
	private const int RenderRuns = 2;
	// The overlay is timed over three stretches of this many frames - early, middle and late in the recording, as the route
	// drawn grows - each after a frame that brings the renderer there untimed.
	private const int WindowFrames = 30;
	// A setting is only worth suggesting when it's clearly faster.
	internal const double MinGain = 0.15;
	// The preview shows its frames on time while the overlay takes at most this share of a frame's time.
	internal const double PreviewFrameBudget = 0.5;
	// An encoder this close to its decoder's speed was waiting for frames.
	private const double DecodeLimit = 0.9;
	private static readonly string[] NvencPresets = ["p1", "p4", "p7"];

	public static async Task<BenchmarkResult> RunAsync(BenchmarkInput input, Action<BenchmarkProgress>? onProgress, CancellationToken ct)
	{
		var clock = Stopwatch.StartNew();
		OverlaySettings settings = OverlaySettingsStore.Load();
		List<string> tempFiles = [];
		int done = 0, total = 0;

		void Step(BenchmarkStage stage)
		{
			onProgress?.Invoke(new BenchmarkProgress(stage, done, total));
			done++;
		}

		try
		{
			onProgress?.Invoke(new BenchmarkProgress(BenchmarkStage.Preparing, 0, 0));
			// Before anything of the test's own runs: what else keeps the computer busy.
			BenchmarkLoad load = await BenchmarkSystemInfo.SampleLoadAsync(ct);
			BenchmarkSystem system = await BenchmarkSystemInfo.CollectAsync(ct);

			bool synthetic = input.InputPaths is not { Count: > 0 };
			string source = synthetic ? await CreateSyntheticSourceAsync(tempFiles, ct) : input.InputPaths![0];
			VideoInfo video = await Task.Run(() => SourceProbe.Probe(source).Video, ct);
			VideoInfo output = OutputVideo.For(video, settings);
			(int num, int den) = FfmpegPipeline.ParseFrameRate(video.FrameRate);
			string encoder = await Task.Run(() => FfmpegPipeline.SelectVideoEncoder(output), ct);
			string[] presets = FfmpegPipeline.IsGpuEncoder(encoder) ? NvencPresets : [];
			bool render = !synthetic && input.TelemetryFrames is { Count: > 0 };
			total = 1 + DecodeRuns * 2 + 3 + PipeRuns + (presets.Length + 1) * EncodeRuns + (render ? RenderRuns * 2 : 0);
			AppLogger.Info($"Benchmark on {(synthetic ? "a synthetic clip" : Path.GetFileName(source))}: {video.CodecName} {video.Width}x{video.Height} " +
			               $"{video.FrameRate} {video.PixFmt}, output {output.CodecName} {output.Width}x{output.Height} {output.PixFmt}, {system}, {load}");

			// Warm-up: the first run pays for the GPU's decoder starting up and the file coming off the disk.
			Step(BenchmarkStage.Decode);
			await MeasureFfmpegAsync([.. DecodeArgs(source, true), "-frames:v", Invariant(DecodeFrames / 4), "-f", "null", "-"], ct);

			List<double?> hardwareRuns = [], softwareRuns = [];
			for (int run = 0; run < DecodeRuns; run++)
			{
				foreach (bool hardware in Alternate(run))
				{
					Step(BenchmarkStage.Decode);
					(hardware ? hardwareRuns : softwareRuns).Add(
						await MeasureFfmpegAsync([.. DecodeArgs(source, hardware), "-frames:v", Invariant(DecodeFrames), "-f", "null", "-"], ct));
				}
			}

			BenchmarkStat? hardwareDecode = BenchmarkStat.Of(hardwareRuns), softwareDecode = BenchmarkStat.Of(softwareRuns);

			(int previewWidth, int previewHeight) = PreviewSize(video.Width, video.Height, input.PreviewWidth);
			IReadOnlyList<DerivedFrame> frames = input.Frames is { Count: > WindowFrames + 1 } loaded ? loaded : SyntheticFrames(video.Fps);
			(FrameTimes overlay, FrameTimes overlayPlain, FrameTimes preview, FrameTimes previewPlain) =
				await TimeOverlayAsync(input.Layout, frames, video, output, previewWidth, previewHeight, () => Step(BenchmarkStage.Overlay), ct);

			List<double?> pipeRuns = [];
			for (int run = 0; run < PipeRuns; run++)
			{
				Step(BenchmarkStage.Pipe);
				pipeRuns.Add(await MeasurePipeAsync(output, num, den, ct));
			}

			bool decodeOnGpu = !(softwareDecode?.Median > hardwareDecode?.Median);
			BenchmarkStat? feedingDecode = decodeOnGpu ? hardwareDecode : softwareDecode;
			string cpuEncoder = output.CodecName == "h264" ? "libx264" : "libx265";
			Dictionary<string, List<double?>> encodeRuns = presets.ToDictionary(p => p, _ => new List<double?>());
			List<double?> cpuRuns = [];
			for (int run = 0; run < EncodeRuns; run++)
			{
				foreach (string preset in run % 2 == 0 ? presets : presets.Reverse())
				{
					Step(BenchmarkStage.Encode);
					encodeRuns[preset].Add(await MeasureEncodeAsync(source, video, output, encoder, preset, NvencFrames, decodeOnGpu, num, den, ct));
				}

				Step(BenchmarkStage.Encode);
				cpuRuns.Add(await MeasureEncodeAsync(source, video, output, cpuEncoder, null, CpuEncodeFrames, decodeOnGpu, num, den, ct));
			}

			List<EncoderSpeed> encoders =
			[
				.. presets.Select(p => Encoder(encoder, p, encodeRuns[p], feedingDecode)),
				Encoder(cpuEncoder, null, cpuRuns, feedingDecode)
			];

			List<double?> hardwareRenderRuns = [], softwareRenderRuns = [];
			if (render)
			{
				for (int run = 0; run < RenderRuns; run++)
				{
					foreach (bool hardware in Alternate(run))
					{
						Step(BenchmarkStage.Render);
						(hardware ? hardwareRenderRuns : softwareRenderRuns).Add(
							await MeasureRenderAsync(input, input.TelemetryFrames!, settings, hardware, video.Fps, tempFiles, ct));
					}
				}
			}

			var result = new BenchmarkResult(system, load, video, output, synthetic, hardwareDecode, softwareDecode, overlay, overlayPlain, previewWidth,
				previewHeight, preview, previewPlain, BenchmarkStat.Of(pipeRuns), encoders, BenchmarkStat.Of(hardwareRenderRuns),
				BenchmarkStat.Of(softwareRenderRuns), clock.Elapsed);
			AppLogger.Info($"Benchmark: {result}");
			return result;
		}
		finally
		{
			foreach (string file in tempFiles) TryDelete(file);
		}
	}

	/// <summary>How busy the computer is right now, over about a second - the benchmark's check, also asked before a render.</summary>
	public static Task<BenchmarkLoad> SampleLoadAsync(CancellationToken ct)
	{
		return BenchmarkSystemInfo.SampleLoadAsync(ct);
	}

	/// <summary>
	///     The settings this computer would be faster with, given the ones in use. Nothing about decoding when the computer was
	///     busy before the test: a game on the GPU slows its decoder down, and the CPU would win only because of it.
	/// </summary>
	public static List<BenchmarkAdvice> Advise(BenchmarkResult result, OverlaySettings settings)
	{
		List<BenchmarkAdvice> advice = [];

		// A real render says it best: decoding shares the CPU with drawing the overlay there. Decoding alone only counts
		// when it's far apart.
		(BenchmarkStat? hardware, BenchmarkStat? software, double minGain) = result.HardwareRender is not null && result.SoftwareRender is not null
			? (result.HardwareRender, result.SoftwareRender, MinGain)
			: (result.HardwareDecode, result.SoftwareDecode, MinGain * 2);
		if (hardware is not null && software is not null && !result.Load.IsBusy)
		{
			if (settings.HardwareDecoding && ClearlyFaster(software, hardware, minGain))
				advice.Add(new BenchmarkAdvice(BenchmarkAdviceKind.SoftwareDecode, software.Median / hardware.Median - 1));
			else if (!settings.HardwareDecoding && ClearlyFaster(hardware, software, minGain))
				advice.Add(new BenchmarkAdvice(BenchmarkAdviceKind.HardwareDecode, hardware.Median / software.Median - 1));
		}

		double frameMs = 1000 / Math.Max(result.Fps, 1);
		double gain = result.PreviewOverlay.MedianMs / Math.Max(result.PreviewOverlayPlain.MedianMs, 0.01);
		if (settings.PreviewShadows && result.PreviewOverlay.MedianMs > frameMs * PreviewFrameBudget)
			advice.Add(new BenchmarkAdvice(BenchmarkAdviceKind.PreviewShadowsOff, gain));
		else if (!settings.PreviewShadows && result.PreviewOverlay.P95Ms <= frameMs * PreviewFrameBudget / 2)
			advice.Add(new BenchmarkAdvice(BenchmarkAdviceKind.PreviewShadowsOn, gain));

		return advice;
	}

	/// <summary>Faster by at least `minGain` at the median, and - with repeated runs - every run of it faster than every run of the other.</summary>
	internal static bool ClearlyFaster(BenchmarkStat faster, BenchmarkStat slower, double minGain)
	{
		return slower.Median > 0 && faster.Median / slower.Median - 1 >= minGain && (faster.Runs < 2 || slower.Runs < 2 || faster.Min > slower.Max);
	}

	/// <summary>The stage that holds a render back with the settings in use - the overlay as a render draws it, with shadows.</summary>
	public static BenchmarkBottleneck? Bottleneck(BenchmarkResult result, OverlaySettings settings)
	{
		List<(BenchmarkStage Stage, double Fps)> stages = [];
		if ((settings.HardwareDecoding ? result.HardwareDecode : result.SoftwareDecode) is { } decode) stages.Add((BenchmarkStage.Decode, decode.Median));
		if (result.Overlay.Fps > 0) stages.Add((BenchmarkStage.Overlay, result.Overlay.Fps));
		if (result.Pipe is { } pipe) stages.Add((BenchmarkStage.Pipe, pipe.Median));
		if (EncoderInUse(result, settings)?.Speed is { } encode) stages.Add((BenchmarkStage.Encode, encode.Median));
		if (stages.Count == 0) return null;

		(BenchmarkStage stage, double fps) = stages.MinBy(s => s.Fps);
		return new BenchmarkBottleneck(stage, fps, (settings.HardwareDecoding ? result.HardwareRender : result.SoftwareRender)?.Median);
	}

	/// <summary>The encoder a render uses: NVENC at the chosen preset, or the CPU one where there's no NVENC.</summary>
	public static EncoderSpeed? EncoderInUse(BenchmarkResult result, OverlaySettings settings)
	{
		bool gpu = result.Encoders.Any(e => FfmpegPipeline.IsGpuEncoder(e.Encoder));
		return result.Encoders.FirstOrDefault(e => !gpu || FfmpegPipeline.IsGpuEncoder(e.Encoder) && e.Preset == settings.NvencPreset);
	}

	/// <summary>Frames a second between ffmpeg's first progress report and its last - without its start-up. Null with fewer than two.</summary>
	internal static double? SteadyFps(IReadOnlyList<(double Seconds, long Frame)> samples)
	{
		if (samples.Count < 2) return null;

		(double t0, long f0) = samples[0];
		(double t1, long f1) = samples[^1];
		return t1 > t0 && f1 > f0 ? (f1 - f0) / (t1 - t0) : null;
	}

	/// <summary>The preview's size for a recording: its width capped at the preview's, even (as the decoder makes it).</summary>
	internal static (int Width, int Height) PreviewSize(int width, int height, int previewWidth)
	{
		if (previewWidth <= 0 || previewWidth >= width) return (width, height);

		int scaledHeight = (int)Math.Round(height * (double)previewWidth / width) & ~1;
		return (previewWidth & ~1, Math.Max(scaledHeight, 2));
	}

	/// <summary>Where each stretch the overlay is timed over starts: early, middle and late, or just the start of a short recording.</summary>
	internal static int[] WindowStarts(int frameCount)
	{
		if (frameCount < 3 * (WindowFrames + 1)) return [0];

		int last = frameCount - WindowFrames - 1;
		return [frameCount / 10, Math.Min(frameCount / 2, last), Math.Min(frameCount * 9 / 10, last)];
	}

	/// <summary>Which of two compared runs goes first - turn about, so a computer warming up (or cooling down) favors neither.</summary>
	private static bool[] Alternate(int run)
	{
		return run % 2 == 0 ? [true, false] : [false, true];
	}

	private static EncoderSpeed Encoder(string encoder, string? preset, List<double?> runs, BenchmarkStat? decode)
	{
		BenchmarkStat? speed = BenchmarkStat.Of(runs);
		return new EncoderSpeed(encoder, preset, speed, speed is not null && decode is not null && speed.Median >= decode.Median * DecodeLimit);
	}

	/// <summary>The source looped, so a short recording (or the test clip) still gives every step the frames it measures over.</summary>
	private static string[] DecodeArgs(string source, bool hardware)
	{
		string[] input = ["-stream_loop", "-1", "-i", source, "-map", "0:v:0"];
		return hardware ? ["-hwaccel", "auto", .. input] : input;
	}

	private static async Task<double?> MeasureEncodeAsync(string source, VideoInfo video, VideoInfo output, string encoder, string? preset, int frames,
		bool decodeOnGpu, int num, int den, CancellationToken ct)
	{
		List<string> args = [.. DecodeArgs(source, decodeOnGpu), "-frames:v", Invariant(frames)];
		if (FfmpegPipeline.ResizeFilter(video, output) is { Length: > 0 } resize) args.AddRange(["-vf", resize.TrimEnd(',')]);
		FfmpegPipeline.AddVideoEncoderArgs(args, output, encoder, new RenderEncodeSettings(preset ?? "p7", decodeOnGpu, 1, false), num, den);
		args.AddRange(["-f", "null", "-"]);
		return await MeasureFfmpegAsync(args, ct);
	}

	/// <summary>
	///     Frames handed to ffmpeg the way a render does - raw BGRA at the output's size through its stdin - and only read there:
	///     how fast the pipe alone carries them. Timed after the first PipeWarmUpFrames.
	/// </summary>
	private static async Task<double?> MeasurePipeAsync(VideoInfo output, int num, int den, CancellationToken ct)
	{
		ProcessStartInfo psi = ProcessHelper.CreateHiddenQuietWithStdin("ffmpeg",
		[
			"-hide_banner", "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{output.Width}x{output.Height}", "-r", $"{num}/{den}", "-i", "pipe:0",
			"-f", "null", "-"
		]);
		using Process process = Start(psi);
		using (ct.Register(() => Kill(process)))
		{
			Task<string> stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
			double? fps = await Task.Run(() =>
			{
				byte[] frame = new byte[output.Width * output.Height * 4];
				Stream stdin = process.StandardInput.BaseStream;
				var clock = new Stopwatch();
				try
				{
					for (int i = 0; i < PipeFrames; i++)
					{
						if (i == PipeWarmUpFrames) clock.Start();
						stdin.Write(frame, 0, frame.Length);
					}

					stdin.Close();
					return (PipeFrames - PipeWarmUpFrames) / clock.Elapsed.TotalSeconds;
				}
				catch (IOException)
				{
					// The process died (or was killed for a cancel) - reported below.
					return (double?)null;
				}
			}, CancellationToken.None);

			await process.WaitForExitAsync(CancellationToken.None);
			string errors = await stderr;
			ct.ThrowIfCancellationRequested();
			if (process.ExitCode == 0 && fps is not null) return fps;

			AppLogger.Warn($"Benchmark step failed ({process.ExitCode}): {RenderJob.FfmpegErrorSummary(errors, 3)}");
			return null;
		}
	}

	/// <summary>
	///     A few seconds of a real render (RenderJob) - the overlay, the pipe and ffmpeg sharing the computer as they do - without
	///     the route intro (a still card, quick to draw) or the camera metadata copied after it. Frames a second from its progress.
	/// </summary>
	private static async Task<double?> MeasureRenderAsync(BenchmarkInput input, IReadOnlyList<TelemetryFrame> telemetry, OverlaySettings settings,
		bool hardwareDecoding, double fps, List<string> tempFiles, CancellationToken ct)
	{
		string output = Path.Combine(Path.GetTempPath(), $"osmooverlay-benchmark-{Guid.NewGuid():N}.mp4");
		tempFiles.Add(output);
		var progress = new RenderSamples();
		OverlaySettings renderSettings = settings with
		{
			HardwareDecoding = hardwareDecoding, ShowRouteIntro = false, PreserveCameraMetadata = false, FastStart = false
		};
		var options = new RenderOptions(input.InputPaths!, output, TelemetryFrames: telemetry, Layout: input.Layout,
			RangeEndSeconds: RenderFrames / Math.Max(fps, 1), Settings: renderSettings);

		RenderResult result = await RenderJob.RunAsync(options, progress, ct);
		ct.ThrowIfCancellationRequested();
		return result.Success ? SteadyFps(progress.Samples) : null;
	}

	/// <summary>Synchronous - Progress&lt;T&gt; would post the samples to another thread, late and out of step with the clock.</summary>
	private sealed class RenderSamples : IProgress<RenderStatus>
	{
		private readonly Lock _lock = new();
		private readonly List<(double Seconds, long Frame)> _samples = [];

		public IReadOnlyList<(double Seconds, long Frame)> Samples
		{
			get
			{
				lock (_lock) return [.. _samples];
			}
		}

		public void Report(RenderStatus value)
		{
			if (value.Phase != RenderPhase.Rendering || value.CurrentFrame <= 0) return;

			lock (_lock) _samples.Add((value.Elapsed.TotalSeconds, value.CurrentFrame));
		}
	}

	/// <summary>
	///     The overlay timed frame by frame: at the render's size the way a render draws it (onto a transparent frame, then
	///     unpremultiplied), at the preview's onto a video frame as the preview does - each with and without shadows. One
	///     renderer, at the recording's size as the preview's is, the Map widget's tiles fetched first (from the disk cache after
	///     the first render) or it would time a placeholder.
	/// </summary>
	private static async Task<(FrameTimes Render, FrameTimes RenderPlain, FrameTimes Preview, FrameTimes PreviewPlain)> TimeOverlayAsync(
		IReadOnlyList<OverlayElement> layout, IReadOnlyList<DerivedFrame> frames, VideoInfo video, VideoInfo output, int previewWidth, int previewHeight,
		Action step, CancellationToken ct)
	{
		using var renderer = new OverlayRenderer(video.Width, video.Height, frames[0].Raw.AltitudeMeters, layout, frames,
			TelemetryProcessor.Summarize(frames).MaxSpeedKmh, true, mapSources: MapSources.From(OverlaySettingsStore.Load()));
		step();
		await renderer.PrepareMapAsync(ct: ct);

		return await Task.Run(() =>
		{
			byte[] rendered = new byte[renderer.FrameBufferSize(output.Width, output.Height)];
			byte[] picture = new byte[previewWidth * previewHeight * 4];
			Array.Fill(picture, (byte)96);

			FrameTimes Time(bool shadows, Action<DerivedFrame> draw)
			{
				ct.ThrowIfCancellationRequested();
				renderer.DrawShadows = shadows;
				return TimeFrames(frames, draw);
			}

			FrameTimes render = Time(true, f => renderer.RenderInto(f, rendered, output.Width, output.Height));
			FrameTimes renderPlain = Time(false, f => renderer.RenderInto(f, rendered, output.Width, output.Height));
			step();
			FrameTimes preview = Time(true, f => renderer.RenderOnto(f, picture, previewWidth, previewHeight));
			FrameTimes previewPlain = Time(false, f => renderer.RenderOnto(f, picture, previewWidth, previewHeight));
			step();
			return (render, renderPlain, preview, previewPlain);
		}, ct);
	}

	private static FrameTimes TimeFrames(IReadOnlyList<DerivedFrame> frames, Action<DerivedFrame> draw)
	{
		List<double> milliseconds = [];
		foreach (int start in WindowStarts(frames.Count))
		{
			// Brings the route drawn (and every cache) up to here untimed - a render arrives at it one frame at a time.
			draw(frames[start]);
			for (int i = start + 1; i <= start + WindowFrames && i < frames.Count; i++)
			{
				long started = Stopwatch.GetTimestamp();
				draw(frames[i]);
				milliseconds.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
			}
		}

		return FrameTimes.Of(milliseconds);
	}

	/// <summary>Riding in a gentle curve - what the widgets need to draw something, without a recording.</summary>
	private static List<DerivedFrame> SyntheticFrames(double fps)
	{
		List<DerivedFrame> frames = [];
		for (int i = 0; i < 3 * (WindowFrames + 1) * 4; i++)
		{
			double seconds = i / Math.Max(fps, 1);
			double east = 150 * Math.Sin(seconds * 0.05), north = seconds * 7;
			var raw = new TelemetryFrame(i, seconds, 50 + north / 111_320.0, 20 + east / 71_560.0, 200 + seconds * 0.2, DateTime.UtcNow, 0, 0, 1, 7);
			frames.Add(new DerivedFrame(raw, 25 + Math.Sin(seconds) * 3, (seconds * 4) % 360, 3, seconds * 7, 4, 2, new SunPosition(140, 35), east, north, 1,
				0.1, 0.05));
		}

		return frames;
	}

	/// <summary>
	///     Without a recording: a 4K 59.94 10-bit HEVC clip at the Osmo Action's ~73 Mbps, encoded on the GPU when it can. A test
	///     pattern decodes faster than footage, which the window says next to its numbers.
	/// </summary>
	private static async Task<string> CreateSyntheticSourceAsync(List<string> tempFiles, CancellationToken ct)
	{
		string path = Path.Combine(Path.GetTempPath(), $"osmooverlay-benchmark-{Guid.NewGuid():N}.mp4");
		tempFiles.Add(path);
		string[] input = ["-f", "lavfi", "-i", "testsrc2=s=3840x2160:r=60000/1001,format=yuv420p10le", "-t", "3"];
		string[] rate = ["-rc", "cbr", "-b:v", "73M", "-bufsize", "73M", "-bf", "0", "-g", "60"];
		foreach (string[] encoder in new[] { ["-c:v", "hevc_nvenc", "-preset", "p1", "-profile:v", "main10", .. rate], new[] { "-c:v", "libx265", "-preset", "ultrafast", "-b:v", "73M" } })
		{
			if ((await RunFfmpegAsync([.. input, .. encoder, "-tag:v", "hvc1", path], ct)).Ok) return path;
		}

		throw new InvalidOperationException("ffmpeg couldn't make the benchmark's test clip.");
	}

	/// <summary>Frames a second ffmpeg ran `args` at - null when it failed.</summary>
	private static async Task<double?> MeasureFfmpegAsync(IReadOnlyList<string> args, CancellationToken ct)
	{
		return (await RunFfmpegAsync(args, ct)).Fps;
	}

	/// <summary>Runs ffmpeg reporting its progress to stdout, killed on cancellation.</summary>
	private static async Task<(bool Ok, double? Fps)> RunFfmpegAsync(IReadOnlyList<string> args, CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		ProcessStartInfo psi = ProcessHelper.CreateHiddenQuiet("ffmpeg",
			["-hide_banner", "-nostdin", "-y", "-nostats", "-stats_period", "0.25", "-progress", "pipe:1", .. args]);
		using Process process = Start(psi);
		using (ct.Register(() => Kill(process)))
		{
			Task<string> stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
			List<(double Seconds, long Frame)> samples = [];
			var clock = Stopwatch.StartNew();
			long frame = 0;
			while (await process.StandardOutput.ReadLineAsync(CancellationToken.None) is { } line)
			{
				if (line.StartsWith("frame=", StringComparison.Ordinal))
					long.TryParse(line.AsSpan(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out frame);
				// Only the periodic reports: the last one ("progress=end") comes after ffmpeg has flushed and closed everything.
				else if (line == "progress=continue" && frame > 0)
					samples.Add((clock.Elapsed.TotalSeconds, frame));
			}

			await process.WaitForExitAsync(CancellationToken.None);
			string errors = await stderr;
			ct.ThrowIfCancellationRequested();
			if (process.ExitCode != 0)
			{
				AppLogger.Warn($"Benchmark step failed ({process.ExitCode}): {RenderJob.FfmpegErrorSummary(errors, 3)}");
				return (false, null);
			}

			return (true, SteadyFps(samples) ?? (samples.Count > 0 ? samples[^1].Frame / clock.Elapsed.TotalSeconds : null));
		}
	}

	private static Process Start(ProcessStartInfo psi)
	{
		try
		{
			return Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
		}
		catch (Win32Exception ex)
		{
			throw new InvalidOperationException(ex.Message, ex);
		}
	}

	private static void Kill(Process process)
	{
		try
		{
			if (!process.HasExited) process.Kill(true);
		}
		catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
		{
			// Already gone.
		}
	}

	private static void TryDelete(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			AppLogger.Warn(ex, $"Couldn't delete the benchmark's temporary file {path}");
		}
	}

	private static string Invariant(int value)
	{
		return value.ToString(CultureInfo.InvariantCulture);
	}
}
