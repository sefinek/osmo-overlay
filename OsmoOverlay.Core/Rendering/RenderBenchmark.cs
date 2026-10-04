using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
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
	Graph,
	Render
}

/// <summary>
///     The step running and how many of the run's steps came before it - Total 0 while it's still being worked out. Encoder: the
///     one the test measures with, once picked (its card is the one the test keeps busy).
/// </summary>
public sealed record BenchmarkProgress(BenchmarkStage Stage, int Done, int Total, string? Encoder = null);

/// <summary>
///     What the benchmark runs on: the loaded recording (a few seconds of it are copied out as the sample; null = a generated
///     4K clip) and the layout a render would draw. No file is picked: the test makes its own sample either way.
/// </summary>
public sealed record BenchmarkInput(string? RecordingPath, IReadOnlyList<OverlayElement> Layout, int PreviewWidth);

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

/// <summary>
///     Every measurement of a run - frames a second, or milliseconds a frame for the overlay; null where a step couldn't run.
///     FromRecording: the sample was cut from the loaded recording - otherwise generated, which decodes on the CPU far more
///     easily than a camera's own (measured 105-139 against 37 fps), so decoding isn't compared then.
/// </summary>
public sealed record BenchmarkResult(
	BenchmarkSystem System,
	BenchmarkLoad Load,
	VideoInfo Source,
	VideoInfo Output,
	bool FromRecording,
	BenchmarkStat? HardwareDecode,
	BenchmarkStat? SoftwareDecode,
	FrameTimes Overlay,
	FrameTimes OverlayPlain,
	int PreviewWidth,
	int PreviewHeight,
	FrameTimes PreviewOverlay,
	FrameTimes PreviewOverlayPlain,
	BenchmarkStat? Pipe,
	BenchmarkStat? Graph,
	IReadOnlyList<EncoderSpeed> Encoders,
	BenchmarkStat? HardwareRender,
	BenchmarkStat? SoftwareRender,
	TimeSpan Duration)
{
	public double Fps => Source.Fps;

	/// <summary>Some measurement's runs disagreed - the computer wasn't left alone for the test.</summary>
	public bool IsUnstable =>
		new[] { HardwareDecode, SoftwareDecode, Pipe, Graph, HardwareRender, SoftwareRender }.Concat(Encoders.Select(e => e.Speed)).Any(s => s?.IsUnstable == true);
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

/// <summary>A render's slowest stage with the settings in use, and how close the short render came to it (null without one).</summary>
public sealed record BenchmarkBottleneck(BenchmarkStage Stage, double Fps, double? RenderFps)
{
	/// <summary>The short render over its slowest stage - well below 1, the stages slowed each other down sharing the CPU and GPU.</summary>
	public double? Efficiency => RenderFps / Fps;
}

/// <summary>
///     Measures each stage a render goes through on this computer - decoding (on the GPU and on the CPU), drawing the overlay
///     (with and without the widgets' shadows, at the render's and the preview's size), handing frames to ffmpeg, encoding (each
///     NVENC preset and the CPU encoder), ffmpeg as a whole, and a short render the way RenderJob runs one (the overlay of the
///     layout with a sample route, over the pipe, through the render's own graph and encoder) - and says which settings would
///     be faster here (Advise) and which stage holds a render back (Bottleneck). It makes its own sample: a few seconds copied
///     out of the loaded recording, or a generated 4K clip. Every ffmpeg step runs several times, alternating which of the
///     compared ones goes first so a computer warming up favors neither, after a warm-up run; a result is the runs' median
///     with their spread. Measured for the output Settings ask for (OutputVideo). Nothing is saved: a run under load would
///     skew the main window's estimate, which only real renders feed.
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
	private const int GraphRuns = 2;
	private const int RenderFrames = 240;
	private const int RenderRuns = 2;
	// The short render is timed after this many frames, past ffmpeg setting its graph up.
	private const int RenderWarmUpFrames = 30;
	// As RenderJob's RenderPrefetchFrames.
	private const int RenderPrefetch = 3;
	// Seconds of the loaded recording copied out as the sample - looped where a step needs more.
	public const int SampleSeconds = 5;
	// The overlay is timed over three stretches of this many frames - early, middle and late in the route, as the route
	// drawn grows - each after a frame that brings the renderer there untimed.
	private const int WindowFrames = 30;
	// A setting is only worth suggesting when it's clearly faster.
	internal const double MinGain = 0.15;
	// The preview shows its frames on time while the overlay takes at most this share of a frame's time.
	internal const double PreviewFrameBudget = 0.5;
	// An encoder this close to its decoder's speed was waiting for frames.
	private const double DecodeLimit = 0.9;
	// Settings' scale (FfmpegPipeline.PresetName) - for AMF its three steps.
	private static readonly string[] GpuPresets = ["p1", "p4", "p7"];

	public static async Task<BenchmarkResult> RunAsync(BenchmarkInput input, Action<BenchmarkProgress>? onProgress, CancellationToken ct)
	{
		var clock = Stopwatch.StartNew();
		OverlaySettings settings = OverlaySettingsStore.Load();
		List<string> tempFiles = [];
		int done = 0, total = 0;
		string? measuredWith = null;

		void Step(BenchmarkStage stage)
		{
			onProgress?.Invoke(new BenchmarkProgress(stage, done, total, measuredWith));
			done++;
		}

		try
		{
			onProgress?.Invoke(new BenchmarkProgress(BenchmarkStage.Preparing, 0, 0));
			BenchmarkSystem system = await BenchmarkSystemInfo.CollectAsync(ct);

			string? fromRecording = input.RecordingPath is { } recording ? await CutSampleAsync(recording, tempFiles, ct) : null;
			string source = fromRecording ?? await GenerateSampleAsync(tempFiles, ct);
			VideoInfo video = await Task.Run(() => SourceProbe.Probe(source).Video, ct);
			VideoInfo output = OutputVideo.For(video, settings);
			(int num, int den) = FfmpegPipeline.ParseFrameRate(video.FrameRate);
			string encoder = await Task.Run(() => FfmpegPipeline.SelectVideoEncoder(output), ct);
			measuredWith = encoder;
			// Before any measurement - after only the sample's copy and the encoder's probe, both done by now: what else keeps
			// the computer busy, on the card the encoder runs on (a laptop's other one drawing the desktop isn't the test's).
			BenchmarkLoad load = await SampleLoadAsync(encoder, ct);
			string[] presets = FfmpegPipeline.IsGpuEncoder(encoder) ? GpuPresets : [];
			total = 1 + DecodeRuns * 2 + 3 + PipeRuns + (presets.Length + 1) * EncodeRuns + GraphRuns + RenderRuns * 2;
			AppLogger.Info($"Benchmark on {(fromRecording is not null ? $"{SampleSeconds} s of {Path.GetFileName(input.RecordingPath)}" : "a generated clip")}: " +
			               $"{video.CodecName} {video.Width}x{video.Height} {video.FrameRate} {video.PixFmt}, output {output.CodecName} {output.Width}x{output.Height} " +
			               $"{output.PixFmt}, {system}, GPUs: {string.Join("; ", system.Gpus)}, {load}");

			// Warm-up: the first run pays for the GPU's decoder starting up and the file coming off the disk.
			Step(BenchmarkStage.Decode);
			await MeasureFfmpegAsync([.. DecodeInput(source, true, encoder), "-map", "0:v:0", "-frames:v", Invariant(DecodeFrames / 4), "-f", "null", "-"], ct);

			List<double?> hardwareRuns = [], softwareRuns = [];
			for (int run = 0; run < DecodeRuns; run++)
			{
				foreach (bool hardware in Alternate(run))
				{
					Step(BenchmarkStage.Decode);
					(hardware ? hardwareRuns : softwareRuns).Add(
						await MeasureFfmpegAsync([.. DecodeInput(source, hardware, encoder), "-map", "0:v:0", "-frames:v", Invariant(DecodeFrames), "-f", "null", "-"], ct));
				}
			}

			BenchmarkStat? hardwareDecode = BenchmarkStat.Of(hardwareRuns), softwareDecode = BenchmarkStat.Of(softwareRuns);

			(int previewWidth, int previewHeight) = PreviewSize(video.Width, video.Height, input.PreviewWidth);
			List<DerivedFrame> route = SampleRoute(video.Fps);
			(FrameTimes overlay, FrameTimes overlayPlain, FrameTimes preview, FrameTimes previewPlain) =
				await TimeOverlayAsync(input.Layout, route, video, output, previewWidth, previewHeight, () => Step(BenchmarkStage.Overlay), ct);

			List<double?> pipeRuns = [];
			for (int run = 0; run < PipeRuns; run++)
			{
				Step(BenchmarkStage.Pipe);
				pipeRuns.Add(await MeasurePipeAsync(output, num, den, ct));
			}

			bool decodeOnGpu = !(softwareDecode?.Median > hardwareDecode?.Median);
			BenchmarkStat? feedingDecode = decodeOnGpu ? hardwareDecode : softwareDecode;
			string cpuEncoder = output.CodecName == "h264" ? "libx264" : "libx265";
			var encodeRuns = presets.ToDictionary(p => p, _ => new List<double?>());
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

			// ffmpeg as a whole, the way a render runs it (FfmpegPipeline.FilterGraph) - decoding, laying the overlay on and
			// encoding at once, slower than any of them alone. Only the overlay is a still picture instead of the C# side's.
			List<double?> graphRuns = [];
			for (int run = 0; run < GraphRuns; run++)
			{
				Step(BenchmarkStage.Graph);
				graphRuns.Add(await MeasureGraphAsync(source, video, output, encoder, settings, num, den, ct));
			}

			List<double?> hardwareRenderRuns = [], softwareRenderRuns = [];
			for (int run = 0; run < RenderRuns; run++)
			{
				foreach (bool hardware in Alternate(run))
				{
					Step(BenchmarkStage.Render);
					(hardware ? hardwareRenderRuns : softwareRenderRuns).Add(
						await MeasureRenderAsync(source, video, output, encoder, settings, hardware, input.Layout, route, num, den, ct));
				}
			}

			var result = new BenchmarkResult(system, load, video, output, fromRecording is not null, hardwareDecode, softwareDecode, overlay, overlayPlain,
				previewWidth, previewHeight, preview, previewPlain, BenchmarkStat.Of(pipeRuns), BenchmarkStat.Of(graphRuns), encoders,
				BenchmarkStat.Of(hardwareRenderRuns), BenchmarkStat.Of(softwareRenderRuns), clock.Elapsed);
			AppLogger.Info($"Benchmark: {result}, encoders: {string.Join("; ", encoders)}");
			return result;
		}
		finally
		{
			foreach (string file in tempFiles) TryDelete(file);
		}
	}

	/// <summary>How busy the computer is right now, over about a second, on the card `encoder` runs on - the benchmark's check, also asked before a render.</summary>
	public static Task<BenchmarkLoad> SampleLoadAsync(string encoder, CancellationToken ct)
	{
		return BenchmarkSystemInfo.SampleLoadAsync(FfmpegPipeline.GpuVendor(encoder), ct);
	}

	/// <summary>
	///     The settings this computer would be faster with, given the ones in use. Nothing about decoding when the computer was
	///     busy before the test (a game on the GPU slows its decoder down, and the CPU would win only because of it), nor on a
	///     generated sample (it decodes on the CPU far more easily than a camera's recording).
	/// </summary>
	public static List<BenchmarkAdvice> Advise(BenchmarkResult result, OverlaySettings settings)
	{
		List<BenchmarkAdvice> advice = [];

		// A render says it best: decoding shares the CPU with drawing the overlay there. Decoding alone only counts when it's
		// far apart.
		(BenchmarkStat? hardware, BenchmarkStat? software, double minGain) = result.HardwareRender is not null && result.SoftwareRender is not null
			? (result.HardwareRender, result.SoftwareRender, MinGain)
			: (result.HardwareDecode, result.SoftwareDecode, MinGain * 2);
		if (hardware is not null && software is not null && !result.Load.IsBusy && result.FromRecording)
		{
			if (settings.HardwareDecoding && ClearlyFaster(software, hardware, minGain))
				advice.Add(new BenchmarkAdvice(BenchmarkAdviceKind.SoftwareDecode, software.Median / hardware.Median - 1));
			else if (!settings.HardwareDecoding && ClearlyFaster(hardware, software, minGain))
				advice.Add(new BenchmarkAdvice(BenchmarkAdviceKind.HardwareDecode, hardware.Median / software.Median - 1));
		}

		double frameMs = 1000 / Math.Max(result.Fps, 1);
		double gain = result.PreviewOverlay.MedianMs / Math.Max(result.PreviewOverlayPlain.MedianMs, 0.01);
		// Shadows turned off everywhere leave the preview's own switch nothing to change.
		if (settings.DisableShadows) return advice;
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
		if (result.Graph is { } graph) stages.Add((BenchmarkStage.Graph, graph.Median));
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

	/// <summary>Where each stretch the overlay is timed over starts: early, middle and late, or just the start of a short route.</summary>
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
		var speed = BenchmarkStat.Of(runs);
		return new EncoderSpeed(encoder, preset, speed, speed is not null && decode is not null && speed.Median >= decode.Median * DecodeLimit);
	}

	/// <summary>
	///     The sample as an input, looped so every step gets the frames it measures over, decoded as a render with `encoder`
	///     decodes it (FfmpegPipeline.HwDecodeArgs) - input options only: an output option here (a -map) would land before a
	///     second input, which ffmpeg refuses.
	/// </summary>
	private static string[] DecodeInput(string source, bool hardware, string encoder)
	{
		string[] input = ["-stream_loop", "-1", "-i", source];
		return [.. FfmpegPipeline.EncoderDeviceArgs(encoder), .. hardware ? FfmpegPipeline.HwDecodeArgs(encoder) : [], .. input];
	}

	private static async Task<double?> MeasureEncodeAsync(string source, VideoInfo video, VideoInfo output, string encoder, string? preset, int frames,
		bool decodeOnGpu, int num, int den, CancellationToken ct)
	{
		List<string> args = [.. DecodeInput(source, decodeOnGpu, encoder), "-map", "0:v:0", "-frames:v", Invariant(frames)];
		if (FfmpegPipeline.ResizeFilter(video, output) is { Length: > 0 } resize) args.AddRange(["-vf", resize.TrimEnd(',')]);
		FfmpegPipeline.AddVideoEncoderArgs(args, output, encoder, new RenderEncodeSettings(preset ?? "p7", decodeOnGpu, 1, false), num, den);
		args.AddRange(["-f", "null", "-"]);
		return await MeasureFfmpegAsync(args, ct);
	}

	/// <summary>
	///     The render's whole ffmpeg side with the settings in use - its decoding, its filter graph and its encoder at the
	///     chosen preset - with a still half-transparent overlay generated in ffmpeg instead of the pipe.
	/// </summary>
	private static async Task<double?> MeasureGraphAsync(string source, VideoInfo video, VideoInfo output, string encoder, OverlaySettings settings,
		int num, int den, CancellationToken ct)
	{
		bool gpu = FfmpegPipeline.IsGpuEncoder(encoder);
		List<string> args =
		[
			.. DecodeInput(source, settings.HardwareDecoding, encoder),
			"-f", "lavfi", "-i", $"color=c=red@0.3:s={output.Width}x{output.Height}:r={num}/{den},format=bgra",
			"-filter_complex", FfmpegPipeline.FilterGraph("[0:v]", 1, video, output, false, false), "-map", "[v]",
			"-frames:v", Invariant(gpu ? NvencFrames : CpuEncodeFrames)
		];
		FfmpegPipeline.AddVideoEncoderArgs(args, output, encoder, new RenderEncodeSettings(settings.NvencPreset, settings.HardwareDecoding, 1, false), num, den);
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
	///     A short render the way RenderJob runs one: the overlay of the layout (with the sample route) drawn frame by frame and
	///     handed over the pipe to ffmpeg running the render's own graph and encoder - only without the sound and the camera
	///     metadata. Frames a second as the pipe takes them, after RenderWarmUpFrames. The drawing stops when ffmpeg does, as
	///     RenderJob's: a producer left waiting on a full channel nobody reads would never finish.
	/// </summary>
	private static async Task<double?> MeasureRenderAsync(string source, VideoInfo video, VideoInfo output, string encoder, OverlaySettings settings,
		bool hardwareDecoding, IReadOnlyList<OverlayElement> layout, IReadOnlyList<DerivedFrame> route, int num, int den, CancellationToken ct)
	{
		using var renderer = new OverlayRenderer(output.Width, output.Height, route[0].Raw.AltitudeMeters, layout, route,
			TelemetryProcessor.Summarize(route).MaxSpeedKmh, settings.ShowWatermark, mapSources: MapSources.From(settings))
		{
			DrawShadows = !settings.DisableShadows
		};
		await renderer.PrepareMapAsync(ct: ct);

		List<string> args =
		[
			"-hide_banner", .. DecodeInput(source, hardwareDecoding, encoder),
			"-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{output.Width}x{output.Height}", "-r", $"{num}/{den}", "-i", "pipe:0",
			"-filter_complex", FfmpegPipeline.FilterGraph("[0:v]", 1, video, output, false, false), "-map", "[v]", "-frames:v", Invariant(RenderFrames)
		];
		FfmpegPipeline.AddVideoEncoderArgs(args, output, encoder, new RenderEncodeSettings(settings.NvencPreset, hardwareDecoding, 1, false), num, den);
		args.AddRange(["-f", "null", "-"]);

		using Process process = Start(ProcessHelper.CreateHiddenQuietWithStdin("ffmpeg", args));
		using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
		using (ct.Register(() => Kill(process)))
		{
			Task<string> stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
			var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(RenderPrefetch) { SingleReader = true, SingleWriter = true });
			var freeBuffers = new ConcurrentQueue<byte[]>();
			CancellationToken producerCt = stop.Token;
			var producer = Task.Run(async () =>
			{
				try
				{
					for (int i = 0; i < RenderFrames; i++)
					{
						if (!freeBuffers.TryDequeue(out byte[]? pixels)) pixels = new byte[renderer.FrameBufferSize()];
						renderer.RenderInto(route[i % route.Count], pixels);
						await channel.Writer.WriteAsync(pixels, producerCt);
					}
				}
				catch (OperationCanceledException)
				{
					// ffmpeg stopped taking frames, or the test was stopped.
				}
				finally
				{
					channel.Writer.TryComplete();
				}
			}, CancellationToken.None);

			double? fps = null;
			try
			{
				Stream stdin = process.StandardInput.BaseStream;
				var clock = new Stopwatch();
				int written = 0;
				await foreach (byte[] pixels in channel.Reader.ReadAllAsync(CancellationToken.None))
				{
					stdin.Write(pixels, 0, pixels.Length);
					freeBuffers.Enqueue(pixels);
					if (++written == RenderWarmUpFrames) clock.Start();
				}

				stdin.Close();
				if (written > RenderWarmUpFrames) fps = (written - RenderWarmUpFrames) / clock.Elapsed.TotalSeconds;
			}
			catch (IOException)
			{
				// ffmpeg died (or was killed for a cancel) - reported below.
			}
			finally
			{
				await stop.CancelAsync();
				await producer;
			}

			await process.WaitForExitAsync(CancellationToken.None);
			string errors = await stderr;
			ct.ThrowIfCancellationRequested();
			if (process.ExitCode == 0 && fps is not null) return fps;

			AppLogger.Warn($"Benchmark step failed ({process.ExitCode}): {RenderJob.FfmpegErrorSummary(errors, 3)}");
			return null;
		}
	}

	/// <summary>
	///     The overlay timed frame by frame: at the render's size the way a render draws it (onto a transparent frame, then
	///     unpremultiplied), at the preview's onto a video frame as the preview does - each with and without shadows. One
	///     renderer, at the recording's size as the preview's is, the Map widget's tiles fetched first (from the disk cache after
	///     the first render) or it would time a placeholder.
	/// </summary>
	private static async Task<(FrameTimes Render, FrameTimes RenderPlain, FrameTimes Preview, FrameTimes PreviewPlain)> TimeOverlayAsync(
		IReadOnlyList<OverlayElement> layout, IReadOnlyList<DerivedFrame> route, VideoInfo video, VideoInfo output, int previewWidth, int previewHeight,
		Action step, CancellationToken ct)
	{
		using var renderer = new OverlayRenderer(video.Width, video.Height, route[0].Raw.AltitudeMeters, layout, route,
			TelemetryProcessor.Summarize(route).MaxSpeedKmh, mapSources: MapSources.From(OverlaySettingsStore.Load()));
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
				return TimeFrames(route, draw);
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

	/// <summary>
	///     The sample route every test draws, the same on every computer: riding a gentle curve at about 25 km/h, climbing a
	///     little. Long enough for the overlay's three timed stretches and for the short render.
	/// </summary>
	private static List<DerivedFrame> SampleRoute(double fps)
	{
		List<DerivedFrame> frames = [];
		for (int i = 0; i < Math.Max(RenderFrames, 3 * (WindowFrames + 1) * 4); i++)
		{
			double seconds = i / Math.Max(fps, 1);
			double east = 150 * Math.Sin(seconds * 0.05), north = seconds * 7;
			var raw = new TelemetryFrame(i, seconds, 50 + north / 111_320.0, 20 + east / 71_560.0, 200 + seconds * 0.2, DateTime.UtcNow, 0, 0, 1, 7);
			frames.Add(new DerivedFrame(raw, 25 + Math.Sin(seconds) * 3, (seconds * 4) % 360, 3, seconds * 7, 4, 2, new SunPosition(140, 35), east, north, 1,
				0.1, 0.05));
		}

		return frames;
	}

	/// <summary>The first SampleSeconds of the loaded recording's picture, copied as it is - instant, and the camera's own bitstream.</summary>
	private static async Task<string?> CutSampleAsync(string recording, List<string> tempFiles, CancellationToken ct)
	{
		string path = Path.Combine(Path.GetTempPath(), $"osmooverlay-benchmark-{Guid.NewGuid():N}.mp4");
		tempFiles.Add(path);
		bool ok = (await RunFfmpegAsync(["-i", recording, "-map", "0:v:0", "-t", Invariant(SampleSeconds), "-c", "copy", path], ct)).Ok;
		return ok ? path : null;
	}

	/// <summary>
	///     Without a recording: a 4K 59.94 10-bit HEVC clip at the Osmo Action's ~90 Mbps, with film grain so it isn't trivial to
	///     decode, encoded on the GPU when it can. It still decodes on the CPU far more easily than a camera's own (Advise).
	/// </summary>
	private static async Task<string> GenerateSampleAsync(List<string> tempFiles, CancellationToken ct)
	{
		string path = Path.Combine(Path.GetTempPath(), $"osmooverlay-benchmark-{Guid.NewGuid():N}.mp4");
		tempFiles.Add(path);
		string[] input = ["-f", "lavfi", "-i", "testsrc2=s=3840x2160:r=60000/1001,format=yuv420p10le,noise=alls=10:allf=t", "-t", "4"];
		string[] rate = ["-rc", "cbr", "-b:v", "90M", "-bufsize", "90M", "-bf", "0", "-g", "60"];
		foreach (string[] encoder in new[] { ["-c:v", "hevc_nvenc", "-preset", "p1", "-profile:v", "main10", .. rate], new[] { "-c:v", "libx265", "-preset", "ultrafast", "-b:v", "90M" } })
		{
			if ((await RunFfmpegAsync([.. input, .. encoder, "-tag:v", "hvc1", path], ct)).Ok) return path;
		}

		throw new InvalidOperationException("ffmpeg couldn't make the benchmark's sample.");
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
