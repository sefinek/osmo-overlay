using OsmoOverlay.Core;
using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Tests.Recordings;

[TestClass]
public sealed class PreparedTelemetryTests
{
	private static FileSummary Summary()
	{
		List<TelemetryFrame> raw = [.. Enumerable.Range(0, 180).Select(i =>
			new TelemetryFrame(i, i / 30.0, 50 + i * 0.000003, 20, 100,
				new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(i / 30.0), 0, 0, 1, 10 + i % 3))];
		List<DerivedFrame> derived = TelemetryProcessor.Process(raw, null);
		return new FileSummary(["synthetic.mp4"], [6], [180], null,
			new VideoInfo("hevc", "Main", 640, 360, "30", "yuv420p", null, null, null, null, 1_000_000),
			null, 6, 0, true, raw, derived, TelemetryProcessor.Summarize(derived), 180);
	}

	[TestMethod]
	public void MeasuredFrames_ReuseCachedFramesWhenSmoothingIsOff()
	{
		FileSummary summary = Summary();
		Assert.AreSame(summary.DerivedFrames, TelemetryProcessor.MeasuredFrames(summary, false));
	}

	[TestMethod]
	public void MeasuredFrames_SmoothingUsesRawFramesWithoutChangingTheCachedSummary()
	{
		FileSummary summary = Summary();
		DerivedFrame[] original = summary.DerivedFrames!.ToArray();

		IReadOnlyList<DerivedFrame> smoothed = TelemetryProcessor.MeasuredFrames(summary, true);

		CollectionAssert.AreEqual(TelemetryProcessor.Process(summary.TelemetryFrames!, null, true).ToArray(), smoothed.ToArray());
		Assert.AreNotSame(summary.DerivedFrames, smoothed);
		CollectionAssert.AreEqual(original, summary.DerivedFrames!.ToArray());
		Assert.AreSame(summary.DerivedFrames, TelemetryProcessor.MeasuredFrames(summary, false));
	}

	[TestMethod]
	[DataRow(false)]
	[DataRow(true)]
	public void ReusedFrames_WithSpeedCorrectionMatchFullProcessing(bool smoothGps)
	{
		FileSummary summary = Summary();
		IReadOnlyList<DerivedFrame> measured = TelemetryProcessor.MeasuredFrames(summary, smoothGps);
		IReadOnlyList<DerivedFrame> corrected = SpeedCalibration.Apply(measured, 15);

		CollectionAssert.AreEqual(TelemetryProcessor.Process(summary.TelemetryFrames!, null, smoothGps, 15).ToArray(), corrected.ToArray());
		Assert.AreEqual(36, summary.DerivedFrames![0].SpeedKmh, 1e-9);
	}

	[TestMethod]
	public void MeasuredFrames_WithoutTelemetryAreEmpty()
	{
		FileSummary summary = Summary() with { TelemetryFrames = null, DerivedFrames = null };
		Assert.AreEqual(0, TelemetryProcessor.MeasuredFrames(summary, true).Count);
		Assert.AreEqual(0, TelemetryProcessor.MeasuredFrames(summary, false).Count);
	}
}
