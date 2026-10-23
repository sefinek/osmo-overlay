using OsmoOverlay.Core.Telemetry;
using OsmoOverlay.Core.Telemetry.External;

namespace OsmoOverlay.Tests.Telemetry.External;

[TestClass]
public sealed class ExternalTrackSourceTests
{
	private static readonly DateTime TrackStart = new(2026, 10, 21, 14, 20, 49, DateTimeKind.Utc);
	private const double Fps = 25;

	private static string WriteTrack(string directory)
	{
		FitBuilder builder = new FitBuilder().DefineRecord();
		for (int s = 0; s < 60; s++) builder.Record(TrackStart.AddSeconds(s), 53.1 + s * 5 * 9e-6, 18.9, 80, 5);

		string path = Path.Combine(directory, "ride.fit");
		File.WriteAllBytes(path, builder.ToArray());
		return path;
	}

	private static List<TelemetryFrame> Frames()
	{
		return [.. Enumerable.Range(0, (int)(20 * Fps)).Select(f => new TelemetryFrame(f, f / Fps, 0, 0, 0, null, 0, 0, 1, HasGpsFix: false))];
	}

	[TestMethod]
	public void Apply_AGivenOffsetIsHowFarTheTracksClockIsAheadOfTheCameras()
	{
		string directory = Directory.CreateTempSubdirectory().FullName;
		try
		{
			// The camera said 14:27:45 at the first frame, the track's clock 14:20:59 then: -406 s.
			DateTime cameraStart = new(2026, 10, 21, 14, 27, 45, DateTimeKind.Utc);
			var source = new ExternalTrackSource(WriteTrack(directory), (TrackStart.AddSeconds(10) - cameraStart).TotalSeconds);

			IReadOnlyList<TelemetryFrame> frames = source.Apply(Frames(), Fps, cameraStart);

			Assert.IsTrue(frames[0].HasGpsFix);
			Assert.AreEqual(53.1 + 10 * 5 * 9e-6, frames[0].Latitude, 1e-6);
			Assert.AreEqual(TrackStart.AddSeconds(10), frames[0].GpsTimestamp);
		}
		finally
		{
			Directory.Delete(directory, true);
		}
	}

	[TestMethod]
	public void Apply_AnAnchorWorksWithoutTheRecordingsStartTime()
	{
		string directory = Directory.CreateTempSubdirectory().FullName;
		try
		{
			var source = new ExternalTrackSource(WriteTrack(directory), Synchronization: new(new(5, TrackStart.AddSeconds(15))));
			IReadOnlyList<TelemetryFrame> frames = source.Apply(Frames(), Fps, null);
			Assert.AreEqual(TrackStart.AddSeconds(10), frames[0].GpsTimestamp);
			Assert.AreEqual(TrackStart.AddSeconds(15), frames[125].GpsTimestamp);
			Assert.IsTrue(frames[0].HasGpsFix);
		}
		finally { Directory.Delete(directory, true); }
	}

	[TestMethod]
	public void Apply_RejectsAnAlignmentOutsideTheTrack()
	{
		string directory = Directory.CreateTempSubdirectory().FullName;
		try
		{
			var source = new ExternalTrackSource(WriteTrack(directory), Synchronization: new(new(0, TrackStart.AddMinutes(5))));
			Assert.ThrowsExactly<InvalidOperationException>(() => source.Apply(Frames(), Fps, null));
		}
		finally { Directory.Delete(directory, true); }
	}

	[TestMethod]
	public void Apply_AnOffsetNeedsTheRecordingsStartTime()
	{
		string directory = Directory.CreateTempSubdirectory().FullName;
		try
		{
			var source = new ExternalTrackSource(WriteTrack(directory), -406);

			Assert.ThrowsExactly<InvalidOperationException>(() => source.Apply(Frames(), Fps, null));
		}
		finally
		{
			Directory.Delete(directory, true);
		}
	}

	[TestMethod]
	public void Apply_OverlappingCameraClockWithoutMatchingMotionDoesNotProveAlignment()
	{
		string directory = Directory.CreateTempSubdirectory().FullName;
		try
		{
			Assert.ThrowsExactly<InvalidOperationException>(() => new ExternalTrackSource(WriteTrack(directory)).Apply(Frames(), Fps, TrackStart.AddSeconds(5)));
		}
		finally
		{
			Directory.Delete(directory, true);
		}
	}

	[TestMethod]
	public void Apply_WithNoWayToMatchItIsAnErrorThatNamesTheFile()
	{
		string directory = Directory.CreateTempSubdirectory().FullName;
		try
		{
			var source = new ExternalTrackSource(WriteTrack(directory));

			var error = Assert.ThrowsExactly<InvalidOperationException>(() => source.Apply(Frames(), Fps, TrackStart.AddMinutes(7)));

			Assert.IsTrue(error.Message.Contains("ride.fit"));
		}
		finally
		{
			Directory.Delete(directory, true);
		}
	}
}
