using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Tests.Telemetry;

[TestClass]
public sealed class PlainRecordingFramesTests
{
	[TestMethod]
	public void CoversTheWholeRecording_OneSampleASecond()
	{
		List<TelemetryFrame> frames = PlainRecordingFrames.Create(2.5);

		CollectionAssert.AreEqual(new[] { 0.0, 1.0, 2.0, 2.5 }, frames.Select(f => f.SampleTimeSeconds).ToArray());
	}

	[TestMethod]
	public void HasNoDataAnyWidgetCouldShow()
	{
		List<TelemetryFrame> frames = PlainRecordingFrames.Create(10);

		var availability = OverlayAvailability.Of(frames, false, null);

		Assert.IsFalse(availability.GpsFix);
		Assert.IsFalse(availability.GpsTimestamp);
		Assert.IsFalse(availability.CameraAxes);
	}

	[TestMethod]
	public void ProcessesLikeAnyTelemetry()
	{
		List<DerivedFrame> derived = TelemetryProcessor.Process(PlainRecordingFrames.Create(5), null);

		Assert.AreEqual(6, derived.Count);
	}
}
