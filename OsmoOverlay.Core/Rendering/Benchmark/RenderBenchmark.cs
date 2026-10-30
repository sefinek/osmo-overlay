using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Mapping;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Telemetry;
using SkiaSharp;

namespace OsmoOverlay.Core;

/// <summary>
///     Measures each stage a render goes through on this computer - decoding (on the GPU and on the CPU), drawing the overlay
///     (with and without the widgets' shadows, at the render's and the preview's size), handing frames to ffmpeg, encoding (each
///     GPU preset and the CPU encoder), ffmpeg as a whole, and a short render the way RenderJob runs one (the overlay of the
///     layout over the pipe, through the render's own graph and encoder) - and says which settings would be faster here
///     (Advise) and which stage holds a render back (Bottleneck). The overlay always draws the test recording's telemetry,
///     anomalies and all. Every stage runs on the test recording - its three files joined, as a render joins a camera's - and
///     decoding and the short render again on a few seconds copied out of the loaded recording when it's asked for (Recording). Every ffmpeg step runs several times, alternating which of the compared ones goes first so a computer warming
///     up favors neither, after a warm-up run; a result is the runs' median with their spread. Measured for the output Settings
///     ask for (OutputVideo). Nothing is saved: a run under load would skew the main window's estimate, which only real renders
///     feed.
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

	public static async Task<BenchmarkResult> RunAsync(BenchmarkInput input, Action<BenchmarkProgress>? onProgress, Action<BenchmarkLive>? onLive,
		CancellationToken ct)
	{
		var clock = Stopwatch.StartNew();
		OverlaySettings settings = OverlaySettingsStore.Load();
		List<string> tempFiles = [];
		int done = 0, total = 0;
		string? measuredWith = null;
		var live = new Live(onLive);

		void Step(BenchmarkStage stage)
		{
			live.Stage = stage;
			onProgress?.Invoke(new BenchmarkProgress(stage, done, total, measuredWith));
			done++;
		}

		try
		{
			onProgress?.Invoke(new BenchmarkProgress(BenchmarkStage.Preparing, 0, 0));
			BenchmarkSystem system = await BenchmarkSystemInfo.CollectAsync(ct);

			string[] source = JoinedInput(input.Synthetic.InputPaths, tempFiles);
			string? sample = input.RecordingPath is { } recording ? await CutSampleAsync(recording, tempFiles, ct) : null;
			VideoInfo video = input.Synthetic.Video;
			VideoInfo output = OutputVideo.For(video, settings);
			(int num, int den) = FfmpegPipeline.ParseFrameRate(video.FrameRate);
			string encoder = await Task.Run(() => FfmpegPipeline.SelectVideoEncoder(output), ct);
			measuredWith = encoder;
			// Before any measurement - after only the sample's copy and the encoder's probe, both done by now: what else keeps
			// the computer busy, on the card the encoder runs on (a laptop's other one drawing the desktop isn't the test's).
			BenchmarkLoad load = await SampleLoadAsync(encoder, ct);
			string[] presets = FfmpegPipeline.IsGpuEncoder(encoder) ? GpuPresets : [];
			int recordingSteps = sample is not null ? DecodeRuns * 2 + RenderRuns * 2 : 0;
			total = 1 + DecodeRuns * 2 + 3 + PipeRuns + (presets.Length + 1) * EncodeRuns + GraphRuns + RenderRuns * 2 + recordingSteps;
			AppLogger.Info($"Benchmark on the test recording{(sample is not null ? $" and {SampleSeconds} s of {Path.GetFileName(input.RecordingPath)}" : "")}: " +
			               $"{video.CodecName} {video.Width}x{video.Height} {video.FrameRate} {video.PixFmt}, output {output.CodecName} {output.Width}x{output.Height} " +
			               $"{output.PixFmt}, {system}, GPUs: {string.Join("; ", system.Gpus)}, {load}");

			List<DerivedFrame> route = TelemetryProcessor.Process(input.Synthetic.TelemetryFrames!, input.Synthetic.CameraFormat, settings.SmoothGpsMotion,
				settings.SpeedCorrectionPercent);

			// Warm-up: the first run pays for the GPU's decoder starting up and the file coming off the disk.
			Step(BenchmarkStage.Decode);
			await MeasureFfmpegAsync([.. DecodeInput(source, true, encoder), "-map", "0:v:0", "-frames:v", Invariant(DecodeFrames / 4), "-f", "null", "-"], live, ct);
			(BenchmarkStat? hardwareDecode, BenchmarkStat? softwareDecode) = await MeasureDecodingAsync(source, encoder, Step, live, ct);

			(int previewWidth, int previewHeight) = PreviewSize(video.Width, video.Height, input.PreviewWidth);
			(FrameTimes overlay, FrameTimes overlayPlain, FrameTimes preview, FrameTimes previewPlain) =
				await TimeOverlayAsync(input.Layout, route, video, output, previewWidth, previewHeight, () => Step(BenchmarkStage.Overlay), live, ct);

			List<double?> pipeRuns = [];
			for (int run = 0; run < PipeRuns; run++)
			{
				Step(BenchmarkStage.Pipe);
				pipeRuns.Add(await MeasurePipeAsync(output, num, den, live, ct));
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
					encodeRuns[preset].Add(await MeasureEncodeAsync(source, video, output, encoder, preset, NvencFrames, decodeOnGpu, num, den, live, ct));
				}

				Step(BenchmarkStage.Encode);
				cpuRuns.Add(await MeasureEncodeAsync(source, video, output, cpuEncoder, null, CpuEncodeFrames, decodeOnGpu, num, den, live, ct));
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
				graphRuns.Add(await MeasureGraphAsync(source, video, output, encoder, settings, num, den, live, ct));
			}

			(BenchmarkStat? hardwareRender, BenchmarkStat? softwareRender) =
				await MeasureRendersAsync(source, video, output, encoder, settings, input.Layout, route, num, den, Step, live, ct);

			// The loaded recording: the camera's own bitstream, which decoding is judged on, rendered to the output Settings ask
			// for it with the encoder that picks.
			SourceSpeeds? recordingSpeeds = null;
			if (sample is not null)
			{
				string[] recordingSource = ["-i", sample];
				VideoInfo recordingVideo = await Task.Run(() => SourceProbe.Probe(sample).Video, ct);
				VideoInfo recordingOutput = OutputVideo.For(recordingVideo, settings);
				string recordingEncoder = await Task.Run(() => FfmpegPipeline.SelectVideoEncoder(recordingOutput), ct);
				(int recordingNum, int recordingDen) = FfmpegPipeline.ParseFrameRate(recordingVideo.FrameRate);
				(BenchmarkStat? recordingHardware, BenchmarkStat? recordingSoftware) =
					await MeasureDecodingAsync(recordingSource, recordingEncoder, Step, live, ct);
				(BenchmarkStat? recordingHardwareRender, BenchmarkStat? recordingSoftwareRender) = await MeasureRendersAsync(recordingSource, recordingVideo,
					recordingOutput, recordingEncoder, settings, input.Layout, route, recordingNum, recordingDen, Step, live, ct);
				recordingSpeeds = new SourceSpeeds(recordingVideo, recordingHardware, recordingSoftware, recordingHardwareRender, recordingSoftwareRender);
			}

			var result = new BenchmarkResult(system, load, video, output, hardwareDecode, softwareDecode, overlay, overlayPlain, previewWidth, previewHeight,
				preview, previewPlain, BenchmarkStat.Of(pipeRuns), BenchmarkStat.Of(graphRuns), encoders, hardwareRender, softwareRender, clock.Elapsed,
				recordingSpeeds);
			AppLogger.Info($"Benchmark: {result}, encoders: {string.Join("; ", encoders)}");
			return result;
		}
		finally
		{
			foreach (string file in tempFiles) TryDelete(file);
		}
	}

	/// <summary>Decoding on the GPU and on the CPU, DecodeRuns each, taking turns going first.</summary>
	private static async Task<(BenchmarkStat? Hardware, BenchmarkStat? Software)> MeasureDecodingAsync(string[] source, string encoder,
		Action<BenchmarkStage> step, Live live, CancellationToken ct)
	{
		List<double?> hardwareRuns = [], softwareRuns = [];
		for (int run = 0; run < DecodeRuns; run++)
		{
			foreach (bool hardware in Alternate(run))
			{
				step(BenchmarkStage.Decode);
				(hardware ? hardwareRuns : softwareRuns).Add(await MeasureFfmpegAsync(
					[.. DecodeInput(source, hardware, encoder), "-map", "0:v:0", "-frames:v", Invariant(DecodeFrames), "-f", "null", "-"], live, ct));
			}
		}

		return (BenchmarkStat.Of(hardwareRuns), BenchmarkStat.Of(softwareRuns));
	}

	/// <summary>The short render with the video decoded on the GPU and on the CPU, RenderRuns each, taking turns going first.</summary>
	private static async Task<(BenchmarkStat? Hardware, BenchmarkStat? Software)> MeasureRendersAsync(string[] source, VideoInfo video, VideoInfo output,
		string encoder, OverlaySettings settings, IReadOnlyList<OverlayElement> layout, IReadOnlyList<DerivedFrame> route, int num, int den,
		Action<BenchmarkStage> step, Live live, CancellationToken ct)
	{
		List<double?> hardwareRuns = [], softwareRuns = [];
		for (int run = 0; run < RenderRuns; run++)
		{
			foreach (bool hardware in Alternate(run))
			{
				step(BenchmarkStage.Render);
				(hardware ? hardwareRuns : softwareRuns).Add(
					await MeasureRenderAsync(source, video, output, encoder, settings, hardware, layout, route, num, den, live, ct));
			}
		}

		return (BenchmarkStat.Of(hardwareRuns), BenchmarkStat.Of(softwareRuns));
	}

	/// <summary>How busy the computer is right now, over about a second, on the card `encoder` runs on - the benchmark's check, also asked before a render.</summary>
	public static Task<BenchmarkLoad> SampleLoadAsync(string encoder, CancellationToken ct)
	{
		return BenchmarkSystemInfo.SampleLoadAsync(FfmpegPipeline.GpuVendor(encoder), ct);
	}

	/// <summary>
	///     The settings this computer would be faster with, given the ones in use. Nothing about decoding when the computer was
	///     busy before the test (a game on the GPU slows its decoder down, and the CPU would win only because of it), and only on
	///     the loaded recording (the test recording decodes on the CPU far more easily than a camera's).
	/// </summary>
	public static List<BenchmarkAdvice> Advise(BenchmarkResult result, OverlaySettings settings)
	{
		List<BenchmarkAdvice> advice = [];

		// A render says it best: decoding shares the CPU with drawing the overlay there. Decoding alone only counts when it's
		// far apart.
		SourceSpeeds? recording = result.Recording;
		(BenchmarkStat? hardware, BenchmarkStat? software, double minGain) = recording is { HardwareRender: not null, SoftwareRender: not null }
			? (recording.HardwareRender, recording.SoftwareRender, MinGain)
			: (recording?.HardwareDecode, recording?.SoftwareDecode, MinGain * 2);
		if (hardware is not null && software is not null && !result.Load.IsBusy)
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

	/// <summary>
	///     The stage that holds a render back with the settings in use - the overlay as a render draws it, with shadows. Decoding and
	///     the short render are the loaded recording's when it was tested (its bitstream is what a render decodes).
	/// </summary>
	public static BenchmarkBottleneck? Bottleneck(BenchmarkResult result, OverlaySettings settings)
	{
		List<(BenchmarkStage Stage, double Fps)> stages = [];
		if (DecodeInUse(result, settings) is { } decode) stages.Add((BenchmarkStage.Decode, decode.Median));
		if (result.Overlay.Fps > 0) stages.Add((BenchmarkStage.Overlay, result.Overlay.Fps));
		if (result.Pipe is { } pipe) stages.Add((BenchmarkStage.Pipe, pipe.Median));
		if (EncoderInUse(result, settings)?.Speed is { } encode) stages.Add((BenchmarkStage.Encode, encode.Median));
		if (result.Graph is { } graph) stages.Add((BenchmarkStage.Graph, graph.Median));
		if (stages.Count == 0) return null;

		(BenchmarkStage stage, double fps) = stages.MinBy(s => s.Fps);
		return new BenchmarkBottleneck(stage, fps, RenderInUse(result, settings)?.Median);
	}

	/// <summary>Decoding the way Settings decode - the loaded recording's when it was tested, else the test recording's.</summary>
	public static BenchmarkStat? DecodeInUse(BenchmarkResult result, OverlaySettings settings)
	{
		return result.Recording is { } recording
			? settings.HardwareDecoding ? recording.HardwareDecode : recording.SoftwareDecode
			: settings.HardwareDecoding
				? result.HardwareDecode
				: result.SoftwareDecode;
	}

	/// <summary>The short render the way Settings decode - the loaded recording's when it was tested, else the test recording's.</summary>
	public static BenchmarkStat? RenderInUse(BenchmarkResult result, OverlaySettings settings)
	{
		return result.Recording is { } recording
			? settings.HardwareDecoding ? recording.HardwareRender : recording.SoftwareRender
			: settings.HardwareDecoding
				? result.HardwareRender
				: result.SoftwareRender;
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
	///     The sample as an input (its -i, or the test recording's files joined - JoinedInput), looped so every step gets the
	///     frames it measures over, decoded as a render with `encoder` decodes it (FfmpegPipeline.HwDecodeArgs) - input options
	///     only: an output option here (a -map) would land before a second input, which ffmpeg refuses.
	/// </summary>
	private static string[] DecodeInput(string[] source, bool hardware, string encoder)
	{
		return [.. FfmpegPipeline.EncoderDeviceArgs(encoder), .. hardware ? FfmpegPipeline.HwDecodeArgs(encoder) : [], "-stream_loop", "-1", .. source];
	}

	/// <summary>The files as one input through the concat demuxer, the way a render reads a camera's split recording.</summary>
	private static string[] JoinedInput(IReadOnlyList<string> paths, List<string> tempFiles)
	{
		string list = Path.Combine(Path.GetTempPath(), $"osmooverlay-benchmark-{Guid.NewGuid():N}.txt");
		tempFiles.Add(list);
		File.WriteAllLines(list, paths.Select(path => $"file '{path.Replace("'", "'\\''")}'"));
		return ["-f", "concat", "-safe", "0", "-i", list];
	}

	private static async Task<double?> MeasureEncodeAsync(string[] source, VideoInfo video, VideoInfo output, string encoder, string? preset, int frames,
		bool decodeOnGpu, int num, int den, Live live, CancellationToken ct)
	{
		List<string> args = [.. DecodeInput(source, decodeOnGpu, encoder), "-map", "0:v:0", "-frames:v", Invariant(frames)];
		if (FfmpegPipeline.ResizeFilter(video, output) is { Length: > 0 } resize) args.AddRange(["-vf", resize.TrimEnd(',')]);
		FfmpegPipeline.AddVideoEncoderArgs(args, output, encoder, new RenderEncodeSettings(preset ?? "p7", decodeOnGpu, 1, false), num, den);
		args.AddRange(["-f", "null", "-"]);
		return await MeasureFfmpegAsync(args, live, ct);
	}

	/// <summary>
	///     The render's whole ffmpeg side with the settings in use - its decoding, its filter graph and its encoder at the
	///     chosen preset - with a still half-transparent overlay generated in ffmpeg instead of the pipe.
	/// </summary>
	private static async Task<double?> MeasureGraphAsync(string[] source, VideoInfo video, VideoInfo output, string encoder, OverlaySettings settings,
		int num, int den, Live live, CancellationToken ct)
	{
		bool gpu = FfmpegPipeline.IsGpuEncoder(encoder);
		List<string> args =
		[
			.. DecodeInput(source, settings.HardwareDecoding, encoder),
			"-f", "lavfi", "-i", $"color=c=red@0.3:s={output.Width}x{output.Height}:r={num}/{den},format=bgra",
			"-filter_complex", FfmpegPipeline.FilterGraph("[0:v]", 1, video, output, false, false, settings.OutputPadToAspect), "-map", "[v]",
			"-frames:v", Invariant(gpu ? NvencFrames : CpuEncodeFrames)
		];
		FfmpegPipeline.AddVideoEncoderArgs(args, output, encoder, new RenderEncodeSettings(settings.NvencPreset, settings.HardwareDecoding, 1, false), num, den);
		args.AddRange(["-f", "null", "-"]);
		return await MeasureFfmpegAsync(args, live, ct);
	}

	/// <summary>
	///     Frames handed to ffmpeg the way a render does - raw BGRA at the output's size through its stdin - and only read there:
	///     how fast the pipe alone carries them. Timed after the first PipeWarmUpFrames.
	/// </summary>
	private static async Task<double?> MeasurePipeAsync(VideoInfo output, int num, int den, Live live, CancellationToken ct)
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
						live.Count(i + 1);
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
	private static async Task<double?> MeasureRenderAsync(string[] source, VideoInfo video, VideoInfo output, string encoder, OverlaySettings settings,
		bool hardwareDecoding, IReadOnlyList<OverlayElement> layout, IReadOnlyList<DerivedFrame> route, int num, int den, Live live, CancellationToken ct)
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
			"-filter_complex", FfmpegPipeline.FilterGraph("[0:v]", 1, video, output, false, false, settings.OutputPadToAspect), "-map", "[v]", "-frames:v", Invariant(RenderFrames)
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
						live.Frame(pixels, output.Width, output.Height, false);
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
					live.Count(written);
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
		Action step, Live live, CancellationToken ct)
	{
		using var renderer = new OverlayRenderer(video.Width, video.Height, route[0].Raw.AltitudeMeters, layout, route,
			TelemetryProcessor.Summarize(route).MaxSpeedKmh, mapSources: MapSources.From(OverlaySettingsStore.Load()));
		step();
		await renderer.PrepareMapAsync(ct: ct);

		return await Task.Run(() =>
		{
			byte[] rendered = new byte[renderer.FrameBufferSize(output.Width, output.Height)];
			byte[] picture = new byte[previewWidth * previewHeight * 4];
			void ResetPicture() => Array.Fill(picture, (byte)96);

			// The preview draws onto a fresh video frame each time - a gray one here, filled again untimed.
			FrameTimes Time(bool shadows, Action<DerivedFrame> draw, Action? reset = null)
			{
				ct.ThrowIfCancellationRequested();
				renderer.DrawShadows = shadows;
				return TimeFrames(route, draw, reset, live);
			}

			void Render(DerivedFrame frame)
			{
				renderer.RenderInto(frame, rendered, output.Width, output.Height);
				live.Frame(rendered, output.Width, output.Height, false);
			}

			void Preview(DerivedFrame frame)
			{
				renderer.RenderOnto(frame, picture, previewWidth, previewHeight);
				live.Frame(picture, previewWidth, previewHeight, true);
			}

			FrameTimes render = Time(true, Render);
			FrameTimes renderPlain = Time(false, Render);
			step();
			FrameTimes preview = Time(true, Preview, ResetPicture);
			FrameTimes previewPlain = Time(false, Preview, ResetPicture);
			step();
			return (render, renderPlain, preview, previewPlain);
		}, ct);
	}

	private static FrameTimes TimeFrames(IReadOnlyList<DerivedFrame> frames, Action<DerivedFrame> draw, Action? reset, Live live)
	{
		List<double> milliseconds = [];
		int drawn = 0;
		foreach (int start in WindowStarts(frames.Count))
		{
			// Brings the route drawn (and every cache) up to here untimed - a render arrives at it one frame at a time.
			reset?.Invoke();
			draw(frames[start]);
			for (int i = start + 1; i <= start + WindowFrames && i < frames.Count; i++)
			{
				reset?.Invoke();
				long started = Stopwatch.GetTimestamp();
				draw(frames[i]);
				milliseconds.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
				live.Count(++drawn);
			}
		}

		return FrameTimes.Of(milliseconds);
	}

	/// <summary>The first SampleSeconds of the loaded recording's picture, copied as it is - instant, and the camera's own bitstream.</summary>
	private static async Task<string?> CutSampleAsync(string recording, List<string> tempFiles, CancellationToken ct)
	{
		string path = Path.Combine(Path.GetTempPath(), $"osmooverlay-benchmark-{Guid.NewGuid():N}.mp4");
		tempFiles.Add(path);
		bool ok = (await RunFfmpegAsync(["-i", recording, "-map", "0:v:0", "-t", Invariant(SampleSeconds), "-c", "copy", path], ct)).Ok;
		return ok ? path : null;
	}

	/// <summary>Frames a second ffmpeg ran `args` at - null when it failed.</summary>
	private static async Task<double?> MeasureFfmpegAsync(IReadOnlyList<string> args, Live live, CancellationToken ct)
	{
		return (await RunFfmpegAsync(args, ct, (_, fps) => live.Fps(fps))).Fps;
	}

	/// <summary>
	///     Runs ffmpeg reporting its progress to stdout, killed on cancellation. onProgress gets each periodic report: the frames
	///     done and the frames a second since the report before.
	/// </summary>
	internal static async Task<(bool Ok, double? Fps)> RunFfmpegAsync(IReadOnlyList<string> args, CancellationToken ct, Action<long, double>? onProgress = null)
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
				{
					double seconds = clock.Elapsed.TotalSeconds;
					if (onProgress is not null && samples.Count > 0 && seconds > samples[^1].Seconds)
						onProgress(frame, (frame - samples[^1].Frame) / (seconds - samples[^1].Seconds));
					samples.Add((seconds, frame));
				}
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

	/// <summary>
	///     The running step's live readings for the window: its frames a second (from ffmpeg's reports, or a count of frames
	///     done turned into a rate) and, a few times a second, a frame it drew scaled down. Called from the steps' threads.
	/// </summary>
	private sealed class Live(Action<BenchmarkLive>? sink)
	{
		private const double FrameEverySeconds = 0.25;
		private const double CountEverySeconds = 0.25;
		private const int ThumbnailWidth = 640;
		private long _lastFrame;
		private long _countStarted;
		private int _countFrom;
		private volatile BenchmarkStage _stage;

		public BenchmarkStage Stage
		{
			get => _stage;
			set
			{
				_stage = value;
				Interlocked.Exchange(ref _countStarted, 0);
			}
		}

		public void Fps(double fps)
		{
			sink?.Invoke(new BenchmarkLive(Stage, fps));
		}

		/// <summary>`done` frames into the step: every CountEverySeconds, the rate since the last time.</summary>
		public void Count(int done)
		{
			if (sink is null) return;

			long now = Stopwatch.GetTimestamp();
			long started = Interlocked.Read(ref _countStarted);
			if (started == 0 || done < _countFrom)
			{
				Interlocked.Exchange(ref _countStarted, now);
				_countFrom = done;
				return;
			}

			double seconds = Stopwatch.GetElapsedTime(started, now).TotalSeconds;
			if (seconds < CountEverySeconds) return;

			Fps((done - _countFrom) / seconds);
			Interlocked.Exchange(ref _countStarted, now);
			_countFrom = done;
		}

		public void Frame(byte[] pixels, int width, int height, bool opaque)
		{
			if (sink is null) return;

			long now = Stopwatch.GetTimestamp();
			long last = Interlocked.Read(ref _lastFrame);
			if (last != 0 && Stopwatch.GetElapsedTime(last, now).TotalSeconds < FrameEverySeconds) return;

			Interlocked.Exchange(ref _lastFrame, now);
			sink(new BenchmarkLive(Stage, null, Thumbnail(pixels, width, height, opaque)));
		}

		private static BenchmarkThumbnail Thumbnail(byte[] pixels, int width, int height, bool opaque)
		{
			int thumbWidth = Math.Min(ThumbnailWidth, width);
			int thumbHeight = Math.Max(1, (int)Math.Round(height * (double)thumbWidth / width));
			SKAlphaType alpha = opaque ? SKAlphaType.Opaque : SKAlphaType.Unpremul;
			var sourceInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, alpha);
			var thumbInfo = new SKImageInfo(thumbWidth, thumbHeight, SKColorType.Bgra8888, alpha);
			byte[] thumb = new byte[thumbInfo.BytesSize];
			GCHandle source = GCHandle.Alloc(pixels, GCHandleType.Pinned), target = GCHandle.Alloc(thumb, GCHandleType.Pinned);
			try
			{
				using var from = new SKPixmap(sourceInfo, source.AddrOfPinnedObject(), sourceInfo.RowBytes);
				using var to = new SKPixmap(thumbInfo, target.AddrOfPinnedObject(), thumbInfo.RowBytes);
				from.ScalePixels(to, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
			}
			finally
			{
				source.Free();
				target.Free();
			}

			return new BenchmarkThumbnail(thumb, thumbWidth, thumbHeight, opaque);
		}
	}
}
