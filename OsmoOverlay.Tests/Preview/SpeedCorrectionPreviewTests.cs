using OsmoOverlay.Core;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Preview;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Tests.Preview;

[TestClass]
public sealed class SpeedCorrectionPreviewTests
{
	[TestMethod]
	public async Task ChangingCorrection_ReplacesThePreviousPercentage_AndCanRestoreUncorrectedSpeed()
	{
		DerivedFrame[] frames =
		[
			new(new TelemetryFrame(0, 0, 50, 20, 200, null, 0, 0, 1), 44, 0, 0, 0, 0, 0, default, 0, 0, 0, 0, 0),
			new(new TelemetryFrame(1, 1, 50, 20, 200, null, 0, 0, 1), 44, 0, 0, 0, 0, 0, default, 0, 0, 0, 0, 0)
		];
		using var compositor = new OverlayCompositor(
			values => new OverlayRenderer(640, 360, 200, [], values, 44, false),
			frames, 2, new OverlayAvailability(true, false, false), null, true, new FrameBufferPool(2), 10);

		await compositor.SetSpeedCorrectionAsync(50);
		Assert.AreEqual(60.0, compositor.AutoGaugeMaxSpeed(UnitSystem.Metric));
		await compositor.SetSpeedCorrectionAsync(25);
		Assert.AreEqual(50.0, compositor.AutoGaugeMaxSpeed(UnitSystem.Metric));
		await compositor.SetSpeedCorrectionAsync(0);
		Assert.AreEqual(40.0, compositor.AutoGaugeMaxSpeed(UnitSystem.Metric));
		await compositor.SetSpeedCorrectionAsync(50);
		Assert.AreEqual(60.0, compositor.AutoGaugeMaxSpeed(UnitSystem.Metric));
	}

	[TestMethod]
	public async Task CancelledCorrection_DoesNotChangeSpeed()
	{
		DerivedFrame[] frames = [Frame(0, 40), Frame(1, 40)];
		using var compositor = CreateCompositor(frames);
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();

		await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => compositor.SetSpeedCorrectionAsync(50, cancellation.Token));
		Assert.AreEqual(40.0, compositor.AutoGaugeMaxSpeed(UnitSystem.Metric));
		await compositor.SetSpeedCorrectionAsync(25);
		Assert.AreEqual(50.0, compositor.AutoGaugeMaxSpeed(UnitSystem.Metric));
	}

	[TestMethod]
	public async Task ConcurrentCorrections_KeepTheLatestPercentage()
	{
		DerivedFrame[] frames = [.. Enumerable.Range(0, 10000).Select(i => Frame(i, 40))];
		using var compositor = CreateCompositor(frames);

		Task first = compositor.SetSpeedCorrectionAsync(50);
		Task latest = compositor.SetSpeedCorrectionAsync(25);
		await Task.WhenAll(first, latest);

		Assert.AreEqual(50.0, compositor.AutoGaugeMaxSpeed(UnitSystem.Metric));
	}

	[TestMethod]
	public async Task Correction_PreservesTrailPercentileBelowTheFloor_AndOriginalFrames()
	{
		DerivedFrame[] frames = [Frame(0, 4), Frame(1, 4)];
		using var compositor = CreateCompositor(frames);

		await compositor.SetSpeedCorrectionAsync(50);
		Assert.AreEqual(6.0, compositor.Renderer.TrailSpeedPercentileKmh, 1e-9);
		await compositor.SetSpeedCorrectionAsync(0);
		Assert.AreEqual(4.0, compositor.Renderer.TrailSpeedPercentileKmh, 1e-9);
		Assert.AreEqual(4.0, frames[0].SpeedKmh);
	}

	[TestMethod]
	public async Task RestoringCurrentPercentage_SupersedesAPendingCorrection()
	{
		DerivedFrame[] frames = [.. Enumerable.Range(0, 10000).Select(i => Frame(i, 40))];
		using var compositor = CreateCompositor(frames);

		Task first = compositor.SetSpeedCorrectionAsync(50);
		Task latest = compositor.SetSpeedCorrectionAsync(0);
		await Task.WhenAll(first, latest);

		Assert.AreEqual(40.0, compositor.AutoGaugeMaxSpeed(UnitSystem.Metric));
	}

	[TestMethod]
	public async Task ChangingTimeline_DuringCorrection_PreservesCuts()
	{
		DerivedFrame[] frames = [.. Enumerable.Range(0, 10000).Select(i => Frame(i, i < 5000 ? 40 : 80))];
		using var compositor = CreateCompositor(frames);
		var timeline = new OutputTimeline(RenderPlan.Resolve(0, 10000, [new TimeRange(5000, 10000)], null, 1, 10000), 1);

		Task correction = compositor.SetSpeedCorrectionAsync(25);
		compositor.SetTimeline(timeline);
		await correction;

		Assert.AreEqual(50.0, compositor.AutoGaugeMaxSpeed(UnitSystem.Metric));
		Assert.AreEqual(50.0, compositor.Renderer.TrailSpeedPercentileKmh, 1e-9);
		await compositor.SetSpeedCorrectionAsync(0);
		Assert.AreEqual(40.0, compositor.AutoGaugeMaxSpeed(UnitSystem.Metric));
	}

	private static DerivedFrame Frame(int second, double speed) =>
		new(new TelemetryFrame(second, second, 50, 20, 200, null, 0, 0, 1), speed, 0, 0, second * 10, 0, 0, default, 0, 0, 0, 0, 0);

	private static OverlayCompositor CreateCompositor(IReadOnlyList<DerivedFrame> frames) =>
		new(values => new OverlayRenderer(640, 360, 200, [], values, frames.Max(frame => frame.SpeedKmh), false),
			frames, frames.Count, new OverlayAvailability(true, false, false), null, true, new FrameBufferPool(2));
}
