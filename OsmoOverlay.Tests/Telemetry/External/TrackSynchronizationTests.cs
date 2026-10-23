using OsmoOverlay.Core.Telemetry;
using OsmoOverlay.Core.Telemetry.External;

namespace OsmoOverlay.Tests.Telemetry.External;

[TestClass]
public sealed class TrackSynchronizationTests
{
	private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

	[TestMethod]
	public void OneAnchorMapsVideoTimeWithoutACameraClock()
	{
		var sync = new TrackSynchronization(new TrackSyncAnchor(18.5, T0));
		sync.Validate();
		Assert.AreEqual(T0.AddSeconds(-18.5), sync.At(0));
		Assert.AreEqual(T0.AddSeconds(1), sync.At(19.5));
		Assert.AreEqual(1, sync.Rate);
	}

	[TestMethod]
	public void TwoAnchorsCorrectClockDriftAndPreserveTheVideoTimeline()
	{
		var sync = new TrackSynchronization(new TrackSyncAnchor(10, T0), new TrackSyncAnchor(110, T0.AddSeconds(101)));
		var track = new ExternalTrack(Enumerable.Range(0, 120).Select(s => new ExternalTrackPoint(T0.AddSeconds(s), 50 + s * 0.00001, 20, 100, 1)));
		var frames = Enumerable.Range(0, 120).Select(s => new TelemetryFrame(s, s, 0, 0, 0, null, 0, 0, 1, HasGpsFix: false)).ToList();

		List<TelemetryFrame> merged = ExternalTrackMerge.Apply(frames, track, sync);

		Assert.AreEqual(T0, merged[10].GpsTimestamp);
		Assert.AreEqual(T0.AddSeconds(50.5), merged[60].GpsTimestamp);
		Assert.AreEqual(T0.AddSeconds(101), merged[110].GpsTimestamp);
		Assert.AreEqual(50.000505, merged[60].Latitude, 1e-9);
		Assert.AreEqual(110, merged[110].SampleTimeSeconds);
		Assert.AreEqual(frames.Count, merged.Count);
	}

	[TestMethod]
	public void InvalidOrReversedAnchorsAreRejected()
	{
		TrackSynchronization[] invalid =
		[
			new(new(-1, T0)), new(new(double.NaN, T0)), new(new(0, DateTime.SpecifyKind(T0, DateTimeKind.Unspecified))),
			new(new(10, T0), new(10, T0.AddSeconds(1))), new(new(10, T0), new(20, T0)),
			new(new(10, T0), new(20, T0.AddSeconds(-1)))
		];
		foreach (TrackSynchronization sync in invalid) Assert.ThrowsExactly<InvalidDataException>(sync.Validate);
	}

	[TestMethod]
	public void AnchorArgumentsRoundTripAndConvertExplicitTimeZonesToUtc()
	{
		var anchor = new TrackSyncAnchor(18.5123456, T0.AddTicks(1234567));
		Assert.IsTrue(TrackSyncAnchor.TryParse(anchor.ToArgument(), out TrackSyncAnchor? parsed));
		Assert.AreEqual(anchor, parsed);
		Assert.IsTrue(TrackSyncAnchor.TryParse("00:18.5=2026-01-01T13:00:00+01:00", out parsed));
		Assert.AreEqual(new TrackSyncAnchor(18.5, T0), parsed);
		Assert.IsFalse(TrackSyncAnchor.TryParse("18=12:00", out _));
		Assert.IsFalse(TrackSyncAnchor.TryParse("-1=2026-01-01T12:00:00Z", out _));
	}
}
