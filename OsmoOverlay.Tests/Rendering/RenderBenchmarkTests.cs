using OsmoOverlay.Core;
using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Tests.Rendering;

[TestClass]
public sealed class RenderBenchmarkTests
{
	private static readonly VideoInfo Source = new("hevc", "Main 10", 3840, 2160, "60", "yuv420p10le", "bt709", "bt709", "bt709", "tv", 73_000_000);

	private static BenchmarkStat Runs(params double[] fps)
	{
		return BenchmarkStat.Of(fps.Select(f => (double?)f))!;
	}

	private static BenchmarkResult Result(BenchmarkStat? hardwareDecode = null, BenchmarkStat? softwareDecode = null, double previewMs = 4,
		double previewPlainMs = 2, BenchmarkStat? hardwareRender = null, BenchmarkStat? softwareRender = null, double overlayMs = 16,
		BenchmarkStat? pipe = null, IReadOnlyList<EncoderSpeed>? encoders = null)
	{
		return new BenchmarkResult(new BenchmarkSystem("test", null, 8, 1, [], null, "1.0.0"), new BenchmarkLoad(0, 0), Source, Source, false,
			hardwareDecode ?? Runs(150), softwareDecode ?? Runs(150), new FrameTimes(overlayMs, overlayMs * 1.2), new FrameTimes(4, 5), 1920, 1080,
			new FrameTimes(previewMs, previewMs * 1.2), new FrameTimes(previewPlainMs, previewPlainMs), pipe, encoders ?? [], hardwareRender,
			softwareRender, TimeSpan.FromMinutes(2));
	}

	[TestMethod]
	public void Stat_IsTheMedianOfTheRuns_LeavingOutFailedOnes()
	{
		BenchmarkStat stat = BenchmarkStat.Of([30, null, 10, 20, double.NaN])!;

		Assert.AreEqual((20, 10, 30, 3), (stat.Median, stat.Min, stat.Max, stat.Runs));
		Assert.AreEqual(15, Runs(10, 20).Median);
		Assert.IsNull(BenchmarkStat.Of([null, null]));
	}

	[TestMethod]
	public void Stat_RunsFarApart_AreUnstable()
	{
		Assert.IsFalse(Runs(100, 105, 103).IsUnstable);
		Assert.IsTrue(Runs(100, 120, 110).IsUnstable);
		Assert.IsFalse(Runs(100).IsUnstable, "one run has nothing to disagree with");
	}

	[TestMethod]
	public void FrameTimes_TakeTheMedianAndThe95thPercentile()
	{
		FrameTimes times = FrameTimes.Of([.. Enumerable.Range(1, 100).Select(i => (double)i)]);

		Assert.AreEqual(50.5, times.MedianMs);
		Assert.AreEqual(95, times.P95Ms);
		Assert.AreEqual(1000 / 50.5, times.Fps, 1e-9);
	}

	[TestMethod]
	public void SteadyFps_LeavesOutTheStartUp()
	{
		// 0.8 s to start, then 100 frames a second.
		List<(double, long)> samples = [(1.0, 20), (1.5, 70), (2.0, 120)];

		Assert.AreEqual(100, RenderBenchmark.SteadyFps(samples)!.Value, 1e-9);
		Assert.IsNull(RenderBenchmark.SteadyFps([(1.0, 20)]));
	}

	[TestMethod]
	[DataRow(3840, 2160, 1920, 1920, 1080)]
	[DataRow(3840, 2160, 0, 3840, 2160)]
	[DataRow(1920, 1080, 2560, 1920, 1080)]
	[DataRow(2704, 1520, 1280, 1280, 720)]
	public void PreviewSize_CapsTheWidthKeepingTheAspect(int width, int height, int previewWidth, int expectedWidth, int expectedHeight)
	{
		Assert.AreEqual((expectedWidth, expectedHeight), RenderBenchmark.PreviewSize(width, height, previewWidth));
	}

	[TestMethod]
	public void WindowStarts_SpreadOverTheRecording_InsideIt()
	{
		CollectionAssert.AreEqual(new[] { 1000, 5000, 9000 }, RenderBenchmark.WindowStarts(10_000));
		CollectionAssert.AreEqual(new[] { 0 }, RenderBenchmark.WindowStarts(50));
		Assert.IsTrue(RenderBenchmark.WindowStarts(100).All(s => s + 31 <= 100));
	}

	[TestMethod]
	public void ClearlyFaster_NeedsTheGain_AndRunsThatDontOverlap()
	{
		Assert.IsTrue(RenderBenchmark.ClearlyFaster(Runs(130, 135, 140), Runs(100, 105, 110), 0.15));
		Assert.IsFalse(RenderBenchmark.ClearlyFaster(Runs(100, 135, 140), Runs(100, 105, 110), 0.15), "the slowest run of one is among the other's");
		Assert.IsFalse(RenderBenchmark.ClearlyFaster(Runs(110, 112, 114), Runs(100, 101, 102), 0.15), "10% is within the threshold");
	}

	[TestMethod]
	public void Advise_PrefersTheRealRender_OverDecodingAlone()
	{
		// Decoding alone says the CPU, the render (CPU shared with the overlay) says the GPU - the render wins.
		BenchmarkResult result = Result(Runs(100, 101, 99), Runs(250, 251, 249), hardwareRender: Runs(30, 31), softwareRender: Runs(24, 25));

		Assert.IsFalse(RenderBenchmark.Advise(result, new OverlaySettings()).Any(a => a.Kind == BenchmarkAdviceKind.SoftwareDecode));
		Assert.AreEqual(BenchmarkAdviceKind.HardwareDecode,
			RenderBenchmark.Advise(result, new OverlaySettings { HardwareDecoding = false }).Single().Kind);
	}

	[TestMethod]
	public void Advise_DecodingAlone_OnlyWhenFarApart()
	{
		Assert.AreEqual(0, RenderBenchmark.Advise(Result(Runs(100), Runs(120)), new OverlaySettings()).Count, "20% is within what decoding alone can tell");

		BenchmarkAdvice advice = RenderBenchmark.Advise(Result(Runs(100), Runs(250)), new OverlaySettings()).Single();
		Assert.AreEqual(BenchmarkAdviceKind.SoftwareDecode, advice.Kind);
		Assert.AreEqual(1.5, advice.Gain, 1e-9);
	}

	[TestMethod]
	public void Advise_PreviewShadows_ByTheShareOfAFrame()
	{
		// At 60 fps a frame is 16.7 ms: shadows may take up to half of it.
		BenchmarkAdvice off = RenderBenchmark.Advise(Result(previewMs: 12, previewPlainMs: 3), new OverlaySettings()).Single();
		Assert.AreEqual(BenchmarkAdviceKind.PreviewShadowsOff, off.Kind);
		Assert.AreEqual(4, off.Gain, 1e-9);

		Assert.AreEqual(0, RenderBenchmark.Advise(Result(previewMs: 6), new OverlaySettings()).Count);
		Assert.AreEqual(BenchmarkAdviceKind.PreviewShadowsOn,
			RenderBenchmark.Advise(Result(previewMs: 3), new OverlaySettings { PreviewShadows = false }).Single().Kind);
		Assert.AreEqual(0, RenderBenchmark.Advise(Result(previewMs: 6), new OverlaySettings { PreviewShadows = false }).Count,
			"turning them back on only when it's clearly cheap, its slowest frames included");
	}

	[TestMethod]
	public void Bottleneck_IsTheSlowestStageInUse_AndTheRenderMeasuredAgainstIt()
	{
		EncoderSpeed[] encoders =
		[
			new("hevc_nvenc", "p1", Runs(120), false),
			new("hevc_nvenc", "p7", Runs(46), false),
			new("libx265", null, Runs(5), false)
		];
		BenchmarkResult result = Result(Runs(131), Runs(41), overlayMs: 16, pipe: Runs(59), encoders: encoders, hardwareRender: Runs(23));

		BenchmarkBottleneck bottleneck = RenderBenchmark.Bottleneck(result, new OverlaySettings())!;

		Assert.AreEqual(BenchmarkStage.Encode, bottleneck.Stage, "p7 is the preset in use - not the CPU encoder nor p1");
		Assert.AreEqual(46, bottleneck.Fps);
		Assert.AreEqual(0.5, bottleneck.Efficiency!.Value, 1e-9);
		Assert.AreEqual(BenchmarkStage.Decode,
			RenderBenchmark.Bottleneck(result, new OverlaySettings { HardwareDecoding = false })!.Stage, "decoding on the CPU is then the slowest");
	}

	[TestMethod]
	public void Load_BeforeARender_CountsWhatTheEncoderUses()
	{
		var game = new BenchmarkLoad(0.3, 0.97, 0, 0);
		Assert.IsTrue(game.SlowsRender(true), "a game on the GPU slows NVENC's render");
		Assert.IsFalse(game.SlowsRender(false), "an x265 render runs on the CPU, 30% busy");

		var recording = new BenchmarkLoad(0.1, 0.2, 0.3, 1);
		Assert.IsTrue(recording.SlowsRender(true), "another program encoding on the GPU");

		var browsing = new BenchmarkLoad(0.15, 0.25, 0.05, 1);
		Assert.IsFalse(browsing.SlowsRender(true), "a browser and GeForce's background recording don't ask every time");
		Assert.IsTrue(browsing.IsBusy, "but they still skew a benchmark");
		Assert.IsFalse(new BenchmarkLoad(null, null).IsBusy);
	}

	[TestMethod]
	public void Advise_SaysNothingAboutDecoding_WhenTheComputerWasBusy()
	{
		BenchmarkResult result = Result(Runs(30, 31, 29), Runs(190, 191, 189)) with { Load = new BenchmarkLoad(0.2, 0.97) };

		Assert.IsFalse(RenderBenchmark.Advise(result, new OverlaySettings()).Any(a => a.Kind == BenchmarkAdviceKind.SoftwareDecode));
	}

	[TestMethod]
	public void EncoderInUse_IsTheCpuOneWithoutNvenc()
	{
		BenchmarkResult result = Result(encoders: [new EncoderSpeed("libx265", null, Runs(5), false)]);

		Assert.AreEqual("libx265", RenderBenchmark.EncoderInUse(result, new OverlaySettings())!.Encoder);
	}
}
