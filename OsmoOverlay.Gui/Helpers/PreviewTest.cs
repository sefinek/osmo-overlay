using System.Diagnostics;
using System.Text;
using Avalonia;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Preview;

namespace OsmoOverlay.Gui;

/// <summary>What the preview test's report says about the playback around its measurements.</summary>
public sealed record PreviewReportContext(string Source, string Mode, string Display, PixelSize? Decoded, double ShownWidth, double ShownHeight,
	double Scaling, double Rate, string? Decoder, bool Overlay, bool Shadows);

/// <summary>One measured playback: the measurements, where it played, and the computer's load meanwhile.</summary>
public sealed record PreviewRun(PreviewMeasurementResult Result, PreviewReportContext Context, IReadOnlyList<BenchmarkLoad> Loads, long PeakMemory);

/// <summary>A live reading of a measured playback: seconds into it and the frames a second shown and dropped since the last one.</summary>
public sealed record PreviewLive(double Seconds, double ShownFps, double DroppedFps);

/// <summary>
///     The preview's part of the performance test: a running playback measured for MeasureSeconds after a warm-up
///     (PreviewMeasurements, attached to the player's frames), the same in the test's own window and in the app's player.
/// </summary>
internal static class PreviewTest
{
	public const int MeasureSeconds = 20;
	private const double WarmUpSeconds = 3;
	private const double LiveEverySeconds = 0.25;

	/// <summary>Where a playback has to start for warm-up and measurement to fit before the recording ends (a looping one anywhere).</summary>
	public static TimeSpan StartFor(TimeSpan position, TimeSpan duration, double rate, bool loop)
	{
		double needed = rate * (MeasureSeconds + WarmUpSecondsFor(duration, rate));
		return loop ? position : TimeSpan.FromSeconds(Math.Min(position.TotalSeconds, Math.Max(0, duration.TotalSeconds - needed)));
	}

	/// <summary>
	///     Measures `player`'s playback, already started: the warm-up, then MeasureSeconds or until it stops - null if it
	///     stopped during the warm-up. Readings go to `live` every LiveEverySeconds. Runs on the UI thread.
	/// </summary>
	public static async Task<PreviewMeasurementResult?> MeasureAsync(PreviewPlayer player, Action<string> status, Action<PreviewLive> live,
		CancellationToken ct)
	{
		status(Strings.PreviewBenchmark_WarmingUp);
		double warmUp = WarmUpSecondsFor(player.Duration, player.PlaybackRate);
		var clock = Stopwatch.StartNew();
		while (clock.Elapsed.TotalSeconds < warmUp && player.IsPlaying)
			await Task.Delay(100, ct);
		if (!player.IsPlaying) return null;

		var measurements = new PreviewMeasurements(player.PlaybackFrameRate);
		player.Frames.Measurements = measurements;
		void OnStopped() => measurements.Finish();
		player.PlaybackStopped += OnStopped;
		try
		{
			clock.Restart();
			(int presented, int dropped) last = (0, 0);
			double lastSeconds = 0;
			while (clock.Elapsed.TotalSeconds < MeasureSeconds && player.IsPlaying)
			{
				await Task.Delay(TimeSpan.FromSeconds(LiveEverySeconds), ct);
				double seconds = clock.Elapsed.TotalSeconds;
				(int presented, int dropped) counts = measurements.Counts;
				double interval = Math.Max(seconds - lastSeconds, 0.001);
				live(new PreviewLive(seconds, (counts.presented - last.presented) / interval, (counts.dropped - last.dropped) / interval));
				status(string.Format(Strings.PreviewBenchmark_Progress, seconds.ToString("0"), MeasureSeconds));
				(last, lastSeconds) = (counts, seconds);
			}

			return measurements.Finish();
		}
		finally
		{
			measurements.Finish();
			player.Frames.Measurements = null;
			player.PlaybackStopped -= OnStopped;
		}
	}

	/// <summary>The report as text - what the test window copies, one block per playback.</summary>
	public static string Report(PreviewRun run)
	{
		(PreviewMeasurementResult result, PreviewReportContext context) = (run.Result, run.Context);
		var text = new StringBuilder();
		text.AppendLine(string.Format(Strings.PreviewBenchmark_Source, context.Source));
		text.AppendLine(string.Format(Strings.PreviewBenchmark_Display, context.Mode, context.Display));
		text.AppendLine(string.Format(Strings.PreviewBenchmark_Resolution, context.Decoded?.Width, context.Decoded?.Height,
			context.ShownWidth.ToString("0"), context.ShownHeight.ToString("0"), context.Scaling.ToString("0.##")));
		text.AppendLine(string.Format(Strings.PreviewBenchmark_Rate, context.Rate.ToString("0.##"), result.ExpectedFps.ToString("0.##")));
		text.AppendLine(string.Format(Strings.PreviewBenchmark_Refresh, result.DisplayRefreshHz?.ToString("0.0") ?? Strings.Benchmark_Unknown));
		text.AppendLine(string.Format(Strings.PreviewBenchmark_Settings, context.Decoder ?? Strings.Benchmark_Unknown,
			context.Overlay ? Strings.PreviewBenchmark_On : Strings.PreviewBenchmark_Off,
			context.Shadows ? Strings.PreviewBenchmark_On : Strings.PreviewBenchmark_Off));
		text.AppendLine(string.Format(Strings.PreviewBenchmark_Result, result.PresentedFrames, result.Seconds.ToString("0.00"), result.PresentedFps.ToString("0.00")));
		if (result.Seconds < MeasureSeconds - 0.5) text.AppendLine(Strings.PreviewBenchmark_ShortSample);
		if (result.PresentedFrames == 0) text.AppendLine(Strings.PreviewBenchmark_NoFrames);
		text.AppendLine(Gaps(result));
		text.AppendLine(Dropped(result));
		text.AppendLine(Timing(Strings.Benchmark_NameDecode, result.Decode));
		text.AppendLine(Timing(Strings.Benchmark_NameOverlay, result.Overlay));
		text.AppendLine(Timing(Strings.PreviewBenchmark_Draw, result.Draw));
		text.AppendLine(string.Format(Strings.PreviewBenchmark_Load, Average(run.Loads, l => l.Cpu), Average(run.Loads, l => l.Gpu),
			Average(run.Loads, l => l.Decoder), run.PeakMemory > 0 ? FormatHelper.FormatBytes(run.PeakMemory) : Strings.Benchmark_Unknown));
		return text.ToString();
	}

	/// <summary>
	///     Whether a playback decoded well over what it showed (a quality picked above the preview's size on screen) and
	///     didn't keep up for it - frames left out or fewer shown than expected. Auto quality would decode what's shown.
	/// </summary>
	public static bool DecodedTooMuch(PreviewRun run)
	{
		const double oversize = 1.5;
		PreviewMeasurementResult result = run.Result;
		bool behind = result.DecodeSkipped + result.OverlayDropped + result.DisplayDropped > 0 || result.PresentedFps < result.ExpectedFps * 0.98;
		return run.Context.Decoded is { } decoded && decoded.Width > run.Context.ShownWidth * oversize && behind;
	}

	public static string Gaps(PreviewMeasurementResult result)
	{
		return string.Format(Strings.PreviewBenchmark_Gaps, result.Intervals.MedianMs.ToString("0.00"), result.Intervals.P95Ms.ToString("0.00"),
			result.LongestGapMs.ToString("0.00"), result.Stalls);
	}

	public static string Dropped(PreviewMeasurementResult result)
	{
		return string.Format(Strings.PreviewBenchmark_Dropped, result.DecodeSkipped, result.OverlayDropped, result.DisplayDropped);
	}

	public static string Timing(string stage, PreviewTiming timing)
	{
		return string.Format(Strings.PreviewBenchmark_Timing, stage,
			timing.Count > 0 ? timing.MedianMs.ToString("0.00") : Strings.Benchmark_Unknown,
			timing.Count > 0 ? timing.P95Ms.ToString("0.00") : Strings.Benchmark_Unknown);
	}

	private static string Average(IReadOnlyList<BenchmarkLoad> loads, Func<BenchmarkLoad, double?> select)
	{
		double[] values = loads.Select(select).OfType<double>().ToArray();
		return values.Length > 0 ? values.Average().ToString("P0") : Strings.Benchmark_Unknown;
	}

	private static double WarmUpSecondsFor(TimeSpan duration, double rate)
	{
		return Math.Min(WarmUpSeconds, duration.TotalSeconds / Math.Max(rate, 0.01) / 5);
	}
}
