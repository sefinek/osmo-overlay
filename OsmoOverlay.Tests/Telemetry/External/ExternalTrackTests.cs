using System.Text;
using OsmoOverlay.Core.Telemetry.External;

namespace OsmoOverlay.Tests.Telemetry.External;

[TestClass]
public sealed class ExternalTrackTests
{
	private static readonly DateTime T0 = new(2026, 10, 21, 14, 20, 49, DateTimeKind.Utc);

	/// <summary>A fix a second for `seconds`, moving north at `speedMs` (about 9e-6 degrees a metre).</summary>
	private static IEnumerable<ExternalTrackPoint> Walk(DateTime start, int seconds, double speedMs, double fromLatitude = 53.1, double? reportedSpeed = null)
	{
		for (int s = 0; s < seconds; s++)
			yield return new ExternalTrackPoint(start.AddSeconds(s), fromLatitude + s * speedMs * 9e-6, 18.9, 80, reportedSpeed);
	}

	private static MemoryStream Gpx(string body)
	{
		return new MemoryStream(Encoding.UTF8.GetBytes($"""<?xml version="1.0" encoding="UTF-8"?><gpx version="1.1" xmlns="http://www.topografix.com/GPX/1/1" xmlns:gpxtpx="http://www.garmin.com/xmlschemas/TrackPointExtension/v1">{body}</gpx>"""));
	}

	[TestMethod]
	public void Gpx_PointsOfEverySegment()
	{
		using MemoryStream gpx = Gpx("""
			<trk><trkseg>
			<trkpt lat="53.1546640" lon="18.9002020"><ele>87.4</ele><time>2026-10-21T14:20:49Z</time><extensions><gpxtpx:TrackPointExtension><gpxtpx:atemp>24</gpxtpx:atemp></gpxtpx:TrackPointExtension></extensions></trkpt>
			<trkpt lat="53.1546740" lon="18.9002220"><ele>87.6</ele><time>2026-10-21T14:20:50Z</time></trkpt>
			</trkseg><trkseg>
			<trkpt lat="53.1548760" lon="18.9008230"><time>2026-10-21T16:21:56+02:00</time></trkpt>
			</trkseg></trk>
			""");

		ExternalTrack track = GpxReader.Read(gpx);

		Assert.AreEqual(3, track.Points.Count);
		Assert.AreEqual(53.154664, track.Points[0].Latitude, 1e-9);
		Assert.AreEqual(87.4, track.Points[0].AltitudeMeters!.Value, 1e-9);
		Assert.IsNull(track.Points[0].SpeedMs);
		Assert.IsNull(track.Points[2].AltitudeMeters);
		Assert.AreEqual(new DateTime(2026, 10, 21, 14, 21, 56, DateTimeKind.Utc), track.Points[2].Utc, "a time with an offset is brought to UTC");
	}

	[TestMethod]
	public void Gpx_APointWithoutATimeOrWithABadPositionIsLeftOut()
	{
		using MemoryStream gpx = Gpx("""
			<trk><trkseg>
			<trkpt lat="53.1" lon="18.9"><time>2026-10-21T14:20:49Z</time></trkpt>
			<trkpt lat="53.2" lon="18.9"><ele>80</ele></trkpt>
			<trkpt lat="x" lon="18.9"><time>2026-10-21T14:20:50Z</time></trkpt>
			<trkpt lat="53.3" lon="18.9"/>
			<trkpt lat="53.4" lon="18.9"><time>2026-10-21T14:20:51Z</time></trkpt>
			</trkseg></trk>
			""");

		Assert.AreEqual(2, GpxReader.Read(gpx).Points.Count);
	}

	[TestMethod]
	public void Gpx_ADoctypeIsRefused()
	{
		var gpx = new MemoryStream("""<?xml version="1.0"?><!DOCTYPE gpx [<!ENTITY x SYSTEM "file:///c:/windows/win.ini">]><gpx/>"""u8.ToArray());

		Assert.ThrowsExactly<System.Xml.XmlException>(() => GpxReader.Read(gpx));
	}

	[TestMethod]
	public void Reader_TellsFitFromGpxByContent()
	{
		string directory = Directory.CreateTempSubdirectory().FullName;
		try
		{
			string fit = Path.Combine(directory, "ride.dat");
			File.WriteAllBytes(fit, new FitBuilder().DefineRecord().Record(T0, 53.1, 18.9, 80, 2).Record(T0.AddSeconds(1), 53.2, 18.9, 80, 2).ToArray());
			string gpx = Path.Combine(directory, "ride.txt");
			File.WriteAllText(gpx, """<gpx><trk><trkseg><trkpt lat="53.1" lon="18.9"><time>2026-10-21T14:20:49Z</time></trkpt><trkpt lat="53.2" lon="18.9"><time>2026-10-21T14:20:50Z</time></trkpt></trkseg></trk></gpx>""");
			string neither = Path.Combine(directory, "notes.fit");
			File.WriteAllText(neither, "just some text");

			Assert.AreEqual(2, ExternalTrackReader.Read(fit).Points.Count);
			Assert.AreEqual(2, ExternalTrackReader.Read(gpx).Points.Count);
			Assert.ThrowsExactly<InvalidDataException>(() => ExternalTrackReader.Read(neither));
			Assert.IsTrue(ExternalTrackReader.IsTrackFile("a.FIT"));
			Assert.IsFalse(ExternalTrackReader.IsTrackFile("a.mp4"));
		}
		finally
		{
			Directory.Delete(directory, true);
		}
	}

	[TestMethod]
	public void Track_NeedsTwoUsableFixesInTimeOrder()
	{
		Assert.ThrowsExactly<InvalidDataException>(() => new ExternalTrack(Walk(T0, 1, 1)));

		var shuffled = Walk(T0, 5, 1).Reverse().Append(new ExternalTrackPoint(T0, 0, 0));
		ExternalTrack track = new(shuffled);
		Assert.AreEqual(5, track.Points.Count, "(0, 0) is no fix");
		Assert.AreEqual(T0, track.StartUtc);
		Assert.AreEqual(T0.AddSeconds(4), track.EndUtc);
	}

	[TestMethod]
	public void At_InterpolatesBetweenFixesAndIsNullOutsideTheTrack()
	{
		var track = new ExternalTrack(Walk(T0, 10, 2, reportedSpeed: 2));
		int cursor = 0;

		ExternalTrackSample sample = track.At(T0.AddSeconds(2.5), ref cursor)!.Value;

		Assert.AreEqual(ExternalTrackState.Fix, sample.State);
		Assert.AreEqual(53.1 + 2.5 * 2 * 9e-6, sample.Latitude, 1e-9);
		Assert.AreEqual(2, sample.SpeedMs!.Value, 1e-9);
		Assert.IsNull(track.At(T0.AddSeconds(-1), ref cursor));
		Assert.IsNull(track.At(T0.AddSeconds(9.5), ref cursor));
		Assert.IsNotNull(track.At(T0.AddSeconds(9), ref cursor));
	}

	[TestMethod]
	public void At_WithoutTheDevicesSpeedItIsFromTheFixes()
	{
		var track = new ExternalTrack(Walk(T0, 10, 3));
		int cursor = 0;

		Assert.AreEqual(3, track.At(T0.AddSeconds(4.5), ref cursor)!.Value.SpeedMs!.Value, 0.01);
	}

	[TestMethod]
	public void At_AGoingBackwardsStartsOver()
	{
		var track = new ExternalTrack(Walk(T0, 10, 1));
		int cursor = 0;

		double later = track.At(T0.AddSeconds(8), ref cursor)!.Value.Latitude;
		double earlier = track.At(T0.AddSeconds(2), ref cursor)!.Value.Latitude;

		Assert.IsTrue(earlier < later);
		Assert.AreEqual(53.1 + 2 * 9e-6, earlier, 1e-9);
	}

	[TestMethod]
	public void At_AGapWhereTheTimerWasStoppedIsAStop()
	{
		var track = new ExternalTrack(Walk(T0, 5, 4).Concat(Walk(T0.AddSeconds(35), 5, 4, 53.1 + 5 * 4 * 9e-6)), [new ExternalTrackPause(T0.AddSeconds(5), T0.AddSeconds(35))]);
		int cursor = 0;

		ExternalTrackSample inGap = track.At(T0.AddSeconds(20), ref cursor)!.Value;

		Assert.AreEqual(ExternalTrackState.Paused, inGap.State);
		Assert.AreEqual(0, inGap.SpeedMs!.Value);
	}

	[TestMethod]
	public void At_AGapOverWhichTheDeviceHardlyMovedIsAStop_FarIsALostSignal()
	{
		// 31 s over 12 m: 0.4 m/s - standing. Over 310 m: 10 m/s - the signal was lost.
		var standing = new ExternalTrack(Walk(T0, 5, 1).Concat([new ExternalTrackPoint(T0.AddSeconds(35), 53.1 + 16 * 9e-6, 18.9)]));
		var lost = new ExternalTrack(Walk(T0, 5, 1).Concat([new ExternalTrackPoint(T0.AddSeconds(35), 53.1 + 350 * 9e-6, 18.9)]));
		int cursor = 0;

		Assert.AreEqual(ExternalTrackState.Paused, standing.At(T0.AddSeconds(20), ref cursor)!.Value.State);
		cursor = 0;
		Assert.AreEqual(ExternalTrackState.Lost, lost.At(T0.AddSeconds(20), ref cursor)!.Value.State);
	}

	[TestMethod]
	public void At_FixesThatAreAlwaysFarApartAreTheDevicesRhythmNotAGap()
	{
		// The fixes are 10 s apart all along - that is the device's rhythm, not a gap.
		var slow = new ExternalTrack(Enumerable.Range(0, 6).Select(i => new ExternalTrackPoint(T0.AddSeconds(i * 10), 53.1 + i * 90 * 9e-6, 18.9)));
		int cursor = 0;

		Assert.AreEqual(ExternalTrackState.Fix, slow.At(T0.AddSeconds(25), ref cursor)!.Value.State);
		Assert.AreEqual(30, slow.GapSeconds);
	}
}
