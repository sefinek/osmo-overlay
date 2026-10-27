using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Overlay;

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
///     What the benchmark runs on: the test recording (SyntheticRecording, read like any recording - its telemetry is the
///     route every overlay step draws), the loaded recording when there is one (a few seconds of it are copied out as the
///     sample) and the layout a render would draw. No file is picked.
/// </summary>
public sealed record BenchmarkInput(FileSummary Synthetic, string? RecordingPath, IReadOnlyList<OverlayElement> Layout, int PreviewWidth);

/// <summary>
///     A reading while the test runs, for the window to show live: the step's frames a second right now, or now and then a
///     frame it drew, scaled down.
/// </summary>
public sealed record BenchmarkLive(BenchmarkStage Stage, double? Fps, BenchmarkThumbnail? Frame = null);

/// <summary>BGRA, Width * 4 bytes a row - straight alpha (an overlay frame as ffmpeg gets it) unless Opaque.</summary>
public sealed record BenchmarkThumbnail(byte[] Bgra, int Width, int Height, bool Opaque);

/// <summary>The loaded recording's decoding and short renders, measured beside the test recording's full run.</summary>
public sealed record SourceSpeeds(
	VideoInfo Video,
	BenchmarkStat? HardwareDecode,
	BenchmarkStat? SoftwareDecode,
	BenchmarkStat? HardwareRender,
	BenchmarkStat? SoftwareRender)
{
	public IEnumerable<BenchmarkStat?> All => [HardwareDecode, SoftwareDecode, HardwareRender, SoftwareRender];
}

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
///     The steps ran on the test recording (Source); Recording holds the loaded recording's decoding and short renders when
///     it was tested too. The test recording decodes on the CPU far more easily than a camera's own (measured 105-139 against
///     37 fps), so decoding is compared on Recording only.
/// </summary>
public sealed record BenchmarkResult(
	BenchmarkSystem System,
	BenchmarkLoad Load,
	VideoInfo Source,
	VideoInfo Output,
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
	TimeSpan Duration,
	SourceSpeeds? Recording = null)
{
	public double Fps => Source.Fps;

	/// <summary>Some measurement's runs disagreed - the computer wasn't left alone for the test.</summary>
	public bool IsUnstable =>
		new[] { HardwareDecode, SoftwareDecode, Pipe, Graph, HardwareRender, SoftwareRender }.Concat(Encoders.Select(e => e.Speed))
			.Concat(Recording?.All ?? []).Any(s => s?.IsUnstable == true);
}

public enum BenchmarkAdviceKind
{
	SoftwareDecode,
	HardwareDecode,
	PreviewShadowsOff,
	PreviewShadowsOn,
	// The app's preview decoded far more than it showed and didn't keep up - Auto quality (from the preview test, in the GUI).
	PreviewQualityAuto
}

/// <summary>A setting worth changing: Gain is how much faster it is (0.4 = 40%), or for the preview's shadows how many times.</summary>
public sealed record BenchmarkAdvice(BenchmarkAdviceKind Kind, double Gain);

/// <summary>A render's slowest stage with the settings in use, and how close the short render came to it (null without one).</summary>
public sealed record BenchmarkBottleneck(BenchmarkStage Stage, double Fps, double? RenderFps)
{
	/// <summary>The short render over its slowest stage - well below 1, the stages slowed each other down sharing the CPU and GPU.</summary>
	public double? Efficiency => RenderFps / Fps;
}
