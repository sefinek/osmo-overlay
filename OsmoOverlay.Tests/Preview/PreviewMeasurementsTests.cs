using OsmoOverlay.Core.Preview;

namespace OsmoOverlay.Tests.Preview;

[TestClass]
public sealed class PreviewMeasurementsTests
{
	[TestMethod]
	public void CountsPresentedFramesAndIncludesStallAtEnd()
	{
		var clock = new TestClock();
		var measurements = new PreviewMeasurements(50, clock);
		clock.Advance(20);
		measurements.RecordPresented();
		clock.Advance(20);
		measurements.RecordPresented();
		clock.Advance(200);
		PreviewMeasurementResult result = measurements.Finish();
		Assert.AreEqual(2, result.PresentedFrames);
		Assert.AreEqual(0.24, result.Seconds, 0.00001);
		Assert.AreEqual(2 / 0.24, result.PresentedFps, 0.00001);
		Assert.AreEqual(20, result.Intervals.P95Ms);
		Assert.AreEqual(200, result.LongestGapMs);
		Assert.AreEqual(1, result.Stalls);
	}

	[TestMethod]
	public void EmptyPlaybackDoesNotReportSuccessOrInventFrames()
	{
		var clock = new TestClock();
		var measurements = new PreviewMeasurements(60, clock);
		clock.Advance(20000);
		PreviewMeasurementResult result = measurements.Finish();
		Assert.AreEqual(0, result.PresentedFrames);
		Assert.AreEqual(0, result.PresentedFps);
		Assert.AreEqual(20000, result.LongestGapMs);
		Assert.AreEqual(1, result.Stalls);
	}

	[TestMethod]
	public void TracksStagePercentilesAndDistinctDropReasons()
	{
		var clock = new TestClock();
		var measurements = new PreviewMeasurements(60, clock);
		for (int i = 1; i <= 20; i++)
		{
			long started = clock.GetTimestamp();
			clock.Advance(i);
			measurements.RecordStage(PreviewStage.Decode, started);
		}
		measurements.RecordDrop(PreviewDrop.Decode, 7);
		measurements.RecordDrop(PreviewDrop.Overlay, 2);
		measurements.RecordDrop(PreviewDrop.Display);
		PreviewMeasurementResult result = measurements.Finish();
		Assert.AreEqual(10.5, result.Decode.MedianMs);
		Assert.AreEqual(19, result.Decode.P95Ms);
		Assert.AreEqual(7, result.DecodeSkipped);
		Assert.AreEqual(2, result.OverlayDropped);
		Assert.AreEqual(1, result.DisplayDropped);
		Assert.AreEqual(0, result.Draw.Count);
	}

	[TestMethod]
	public void RefreshMeasurementIgnoresSuspendedWindow()
	{
		var measurements = new PreviewMeasurements(60, new TestClock());
		measurements.RecordRefresh(1 / 144.0);
		measurements.RecordRefresh(1 / 144.0);
		measurements.RecordRefresh(2);
		Assert.AreEqual(144, measurements.Finish().DisplayRefreshHz!.Value, 0.001);
	}

	[TestMethod]
	public void FinishedMeasurementIgnoresLateCallbacks()
	{
		var clock = new TestClock();
		var measurements = new PreviewMeasurements(60, clock);
		clock.Advance(20);
		measurements.RecordPresented();
		PreviewMeasurementResult first = measurements.Finish();
		clock.Advance(1000);
		measurements.RecordPresented();
		measurements.RecordDrop(PreviewDrop.Display);
		measurements.RecordStage(PreviewStage.Draw, 0);
		Assert.AreSame(first, measurements.Finish());
		Assert.AreEqual(1, first.PresentedFrames);
		Assert.AreEqual(0, first.DisplayDropped);
		Assert.AreEqual(0, first.Draw.Count);
	}

	[TestMethod]
	public void ConcurrentStagesDoNotLoseSamples()
	{
		var clock = new TestClock();
		var measurements = new PreviewMeasurements(60, clock);
		Parallel.For(0, 1000, i =>
		{
			measurements.RecordPresented();
			measurements.RecordDrop(PreviewDrop.Display);
			measurements.RecordStage(PreviewStage.Draw, 0);
		});
		PreviewMeasurementResult result = measurements.Finish();
		Assert.AreEqual(1000, result.PresentedFrames);
		Assert.AreEqual(1000, result.DisplayDropped);
		Assert.AreEqual(1000, result.Draw.Count);
	}

	private sealed class TestClock : TimeProvider
	{
		private long _timestamp;
		public override long TimestampFrequency => 1000;
		public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
		public void Advance(long milliseconds) => Interlocked.Add(ref _timestamp, milliseconds);
	}
}
