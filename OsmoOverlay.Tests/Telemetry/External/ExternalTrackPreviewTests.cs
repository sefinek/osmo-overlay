using OsmoOverlay.Core.Telemetry;
using OsmoOverlay.Core.Telemetry.External;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Tests.Telemetry.External;

[TestClass]
public sealed class ExternalTrackPreviewTests
{
	private static readonly DateTime Start = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
	private static ExternalTrack Track() => new(Enumerable.Range(0, 11).Select(i => new ExternalTrackPoint(Start.AddSeconds(i), 50, 20 + i * 0.00001)));

	[TestMethod]
	public void ExtraTelemetryIsDetectedBeforeAndAfterTheRecordingUsingItsSynchronization()
	{
		ExternalTrack track = Track();
		Assert.IsFalse(ExternalTrackPreview.ExtendsRecording(track, new(new(0, Start)), 10));
		Assert.IsTrue(ExternalTrackPreview.ExtendsRecording(track, new(new(0, Start)), 5));
		Assert.IsTrue(ExternalTrackPreview.ExtendsRecording(track, new(new(0, Start.AddSeconds(5))), 5));
		Assert.IsFalse(ExternalTrackPreview.ExtendsRecording(track, new(new(0, Start.AddSeconds(-5))), 20));
		Assert.IsFalse(ExternalTrackPreview.ExtendsRecording(track, new(new(0, Start)), 0));
	}

	[TestMethod]
	public void RecordingRangeIncludesClockDriftAndIsClippedToTheTrack()
	{
		ExternalTrack track = Track();
		var sync = new TrackSynchronization(new(0, Start.AddSeconds(2)), new(2, Start.AddSeconds(6)));
		var range = ExternalTrackPreview.RecordingRange(track, sync, 3)!;
		Assert.AreEqual(2, range.StartSeconds);
		Assert.AreEqual(8, range.EndSeconds);
		Assert.IsNull(ExternalTrackPreview.RecordingRange(track, new(new(0, Start.AddHours(1))), 5));
	}

	[TestMethod]
	public void VideoPositionUsesUnclippedSynchronizationAndExclusiveRecordingEnd()
	{
		ExternalTrack track = Track();
		var sync = new TrackSynchronization(new(0, Start.AddSeconds(2)), new(2, Start.AddSeconds(6)));
		Assert.IsNull(ExternalTrackPreview.VideoPosition(track, sync, 1.9, 3));
		Assert.AreEqual(0.0, ExternalTrackPreview.VideoPosition(track, sync, 2, 3));
		Assert.AreEqual(1.5, ExternalTrackPreview.VideoPosition(track, sync, 5, 3));
		Assert.IsNull(ExternalTrackPreview.VideoPosition(track, sync, 8, 3));
		Assert.AreEqual(2.5, ExternalTrackPreview.VideoPosition(track, new(new(0, Start.AddSeconds(-2))), 0.5, 5));
	}

	[TestMethod]
	public void PreparedTrackFeedsTheExistingPlayerAndKeepsTheVideoMapping()
	{
		ExternalTrack track = Track();
		var sync = new TrackSynchronization(new(0, Start.AddSeconds(2)), new(2, Start.AddSeconds(6)));
		ExternalTrackPlayback playback = ExternalTrackPreview.Prepare(track, sync, 3, new OverlaySettings());
		Assert.AreEqual(10.0, playback.DurationSeconds);
		Assert.IsNull(playback.VideoPosition(0));
		Assert.AreEqual(0.0, playback.VideoPosition(2));
		Assert.AreEqual(1.5, playback.VideoPosition(5));
		Assert.IsNull(playback.VideoPosition(8));
		Assert.HasCount(playback.RawFrames.Count, playback.DerivedFrames);
		Assert.IsTrue(playback.RawFrames.All(frame => frame.Orientation is null && frame.AccelX == 0 && frame.AccelY == 0 && frame.AccelZ == 0));
	}

	[TestMethod]
	public void RecordingPositionRemainsUnclippedOutsideTheVideo()
	{
		ExternalTrack track = Track();
		var sync = new TrackSynchronization(new(0, Start.AddSeconds(2)), new(2, Start.AddSeconds(6)));
		Assert.AreEqual(-1.0, ExternalTrackPreview.RecordingPosition(track, sync, 0));
		Assert.AreEqual(4.0, ExternalTrackPreview.RecordingPosition(track, sync, 10));
		Assert.IsNull(ExternalTrackPreview.VideoPosition(track, sync, double.NaN, 3));
		Assert.IsNull(ExternalTrackPreview.VideoPosition(track, sync, 2, double.PositiveInfinity));
	}

	[TestMethod]
	public void LongTracksHaveBoundedSamplingAndKeepBothEndpoints()
	{
		DateTime end = Start.AddHours(12);
		var track = new ExternalTrack([new(Start, 50, 20), new(end, 50, 21)]);
		List<TelemetryFrame> frames = ExternalTrackPreview.Frames(track);
		Assert.HasCount(100_001, frames);
		Assert.AreEqual(Start, frames[0].GpsTimestamp);
		Assert.AreEqual(end, frames[^1].GpsTimestamp);
		Assert.IsTrue(frames.Zip(frames.Skip(1), (a, b) => a.SampleTimeSeconds < b.SampleTimeSeconds).All(increasing => increasing));
	}

	[TestMethod]
	public void FullTimelineKeepsEventsOutsideTheClipAndUsesTrackReadingsWithoutCameraSensors()
	{
		var track = new ExternalTrack(Track().Points,
			droneBatteries: [new(Start, new DroneBattery(70))],
			events: [new(Start.AddSeconds(1), new(FlightEventKind.LowVoltage, Value: 3.5)), new(Start.AddSeconds(9), new(FlightEventKind.MotorsOff))]);
		List<TelemetryFrame> frames = ExternalTrackPreview.Frames(track);
		Assert.AreEqual(0, frames[0].SampleTimeSeconds);
		Assert.AreEqual(10, frames[^1].SampleTimeSeconds);
		Assert.AreEqual(Start, frames[0].GpsTimestamp);
		Assert.AreEqual(track.EndUtc, frames[^1].GpsTimestamp);
		Assert.HasCount(2, frames.SelectMany(f => f.FlightEvents ?? []).ToArray());
		Assert.IsTrue(frames.All(f => f.DroneBattery?.Percent == 70 && f.AccelX == 0 && f.AccelY == 0 && f.AccelZ == 0));
	}

	[TestMethod]
	public void GapsRemainWithoutFixAndTheRouteRestartsAfterThem()
	{
		var track = new ExternalTrack(new[] { 0, 1, 2, 8, 9, 10 }.Select(i => new ExternalTrackPoint(Start.AddSeconds(i), 50, 20)));
		List<TelemetryFrame> frames = ExternalTrackPreview.Frames(track);
		Assert.IsFalse(frames[50].HasGpsFix);
		Assert.IsTrue(frames[80].HasGpsFix);
		Assert.IsTrue(frames[80].StartsAfterGap);
		using var cts = new CancellationTokenSource();
		cts.Cancel();
		Assert.ThrowsExactly<OperationCanceledException>(() => ExternalTrackPreview.Frames(track, cts.Token));
	}

	[TestMethod]
	public void FramesOfATrackWithAGapMarkTheGapAsLostGps()
	{
		ExternalTrackPoint[] points = [.. Enumerable.Range(0, 11).Where(i => i is <= 2 or >= 8).Select(i => new ExternalTrackPoint(Start.AddSeconds(i), 50, 20 + i * 0.00001))];
		var track = new ExternalTrack(points);

		List<(double Start, double End)> lost = TelemetryProcessor.FindGpsLossRanges(ExternalTrackPreview.Frames(track));

		Assert.AreEqual(1, lost.Count);
		Assert.IsTrue(lost[0].Start is > 2 and < 2.5, $"{lost[0].Start}");
		Assert.IsTrue(lost[0].End is > 7.5 and < 8, $"{lost[0].End}");
	}
}
