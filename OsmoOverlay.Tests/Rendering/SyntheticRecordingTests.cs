using System.Globalization;
using System.Text.Json.Nodes;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Cameras;
using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Reframe;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Tests.Rendering;

/// <summary>The performance test's recording: what ffmpeg is asked to make, and its telemetry's anomalies.</summary>
[TestClass]
public sealed class SyntheticRecordingTests
{
	private const double Fps = SyntheticRecording.Fps;

	private static IReadOnlyList<TelemetryFrame> File(int index)
	{
		return SyntheticTelemetry.ForFile(index, SyntheticRecording.Plans[index].Frames, Fps);
	}

	private static double Meters(TelemetryFrame a, TelemetryFrame b)
	{
		double north = (b.Latitude - a.Latitude) * 111_320;
		double east = (b.Longitude - a.Longitude) * 111_320 * Math.Cos(a.Latitude * Math.PI / 180);
		return Math.Sqrt(north * north + east * east);
	}

	[TestMethod]
	public void Telemetry_IsTheSameEveryTime()
	{
		CollectionAssert.AreEqual(File(1).ToList(), File(1).ToList());
	}

	[TestMethod]
	public void Telemetry_OneSamplePerFrame_FromZeroInEachFile()
	{
		for (int i = 0; i < SyntheticRecording.Plans.Count; i++)
		{
			IReadOnlyList<TelemetryFrame> frames = File(i);
			Assert.AreEqual(SyntheticRecording.Plans[i].Frames, frames.Count);
			Assert.AreEqual(0, frames[0].SampleTimeSeconds);
			Assert.AreEqual(10 / Fps, frames[10].SampleTimeSeconds, 1e-12);
		}
	}

	[TestMethod]
	public void FirstFile_StartsWithoutAFixOrGpsTime_AtNullIslandLikeADji()
	{
		IReadOnlyList<TelemetryFrame> frames = File(0);

		Assert.IsTrue(frames.TakeWhile(f => f.SampleTimeSeconds < 1.5).All(f => f is { HasGpsFix: false, Latitude: 0, Longitude: 0, GpsTimestamp: null }));
		Assert.IsTrue(frames.SkipWhile(f => f.SampleTimeSeconds < 1.5).All(f => f is { HasGpsFix: true, GpsTimestamp: not null }));
	}

	[TestMethod]
	public void FirstFile_FixWandersWhileStandingStill()
	{
		var standing = File(0).Where(f => f.SampleTimeSeconds is >= 1.6 and < 2.9).ToList();

		Assert.IsTrue(standing.Max(f => Meters(standing[0], f)) > 1, "the position moves");
		Assert.IsTrue(standing.All(f => f.GpsSpeedMs < 1), "while the receiver says it's standing");
	}

	[TestMethod]
	public void SecondFile_LosesTheFix_HoldingTheLastPosition()
	{
		double start = SyntheticTelemetry.FileStartSeconds(1);
		IReadOnlyList<TelemetryFrame> frames = File(1);
		var lost = frames.Where(f => start + f.SampleTimeSeconds is >= 15 and < 17).ToList();
		TelemetryFrame lastFix = frames.Last(f => start + f.SampleTimeSeconds < 15);

		Assert.IsTrue(lost.Count > 100);
		Assert.IsTrue(lost.All(f => !f.HasGpsFix && f.GpsSpeedMs is null && f.Latitude == lastFix.Latitude && f.Longitude == lastFix.Longitude));
		Assert.AreEqual(1, TelemetryProcessor.FindGpsLossRanges([.. frames]).Count);
	}

	[TestMethod]
	public void SecondFile_MultipathJump_WithASpeedSpike()
	{
		double start = SyntheticTelemetry.FileStartSeconds(1);
		IReadOnlyList<TelemetryFrame> frames = File(1);
		TelemetryFrame before = frames.Last(f => start + f.SampleTimeSeconds < 17.2);
		TelemetryFrame during = frames.First(f => start + f.SampleTimeSeconds >= 17.2);

		Assert.IsTrue(Meters(before, during) > 40);
		Assert.AreEqual(75, during.GpsSpeedMs);
	}

	[TestMethod]
	public void ThirdFile_ByTheGpsClock_StartsAfterAStop_SomewhereElse()
	{
		List<IReadOnlyList<TelemetryFrame>> files = [File(0), File(1), File(2)];
		var video = new VideoInfo("hevc", "Main 10", 3840, 2160, "60000/1001", "yuv420p10le", null, null, null, null, 90_000_000);
		List<SourceInfo> sources = [.. files.Select(f => new SourceInfo(video with { FrameCount = f.Count }, null, f.Count / Fps, null))];
		IReadOnlyList<VideoSegment> segments = VideoSegments.Join([.. files.Select((_, i) => $"{i}.mp4")], sources);

		List<TelemetryFrame> combined = TelemetryExtraction.Combine(segments,
			segment => new TelemetryExtractionResult([.. files[int.Parse(Path.GetFileNameWithoutExtension(segment.InputPath))]], null)).Frames;

		int third = files[0].Count + files[1].Count;
		Assert.AreEqual(third, combined.FindIndex(f => f.StartsAfterGap), "only the third file starts after a gap");
		Assert.IsTrue(Meters(combined[third - 1], combined[third]) > 200);
		Assert.AreEqual(40, (combined[third].GpsTimestamp!.Value - combined[third - 1].GpsTimestamp!.Value).TotalSeconds, 0.2);
	}

	[TestMethod]
	public void Ride_StartsAtTheFirstWaypoint_HeadingWest_UpTo150()
	{
		TelemetryFrame first = File(0).First(f => f.HasGpsFix);

		Assert.AreEqual(53.2505, first.Latitude, 0.0002);
		Assert.AreEqual(16.4396, first.Longitude, 0.0002);
		Assert.IsTrue(File(1).Where(f => f.GpsSpeedMs < 50).Max(f => f.GpsSpeedMs!.Value) * 3.6 is > 145 and <= 150.5, "150 km/h at most, the spike aside");
		Assert.IsTrue(File(1)[^1].Longitude < first.Longitude, "westwards");
	}

	[TestMethod]
	public void ThirdFile_TakesTheCornerAtTheSecondWaypoint()
	{
		IReadOnlyList<TelemetryFrame> frames = File(2);
		double nearest = frames.Min(f => Meters(f, new TelemetryFrame(0, 0, 53.248489686279584, 16.411663402469195, 0, null, 0, 1, 0)));

		// A 137-degree turn rounded to 25 m runs 25 * (1 / sin(21.5 deg) - 1) = 43 m inside the waypoint.
		Assert.IsTrue(nearest is > 35 and < 50, $"passes {nearest:0} m from the waypoint");
		Assert.IsTrue(frames[^1].Latitude < frames[0].Latitude, "out of it south-east, towards the third");
	}

	[TestMethod]
	public void Gravity_StandingStill_IsLevelAndOneG()
	{
		TelemetryFrame standing = File(0).First(f => f.SampleTimeSeconds >= 2);

		Assert.AreEqual(1, standing.GForce, 0.06);
		Assert.AreEqual(1, standing.AccelY, 0.06, "gravity pulls down - y");
		Assert.AreEqual(0, standing.AccelX, 0.06);
	}

	[TestMethod]
	public void Telemetry_HasPotholeJolts()
	{
		Assert.IsTrue(File(0).Max(f => f.GForce) > 3);
		Assert.IsTrue(File(1).Max(f => Math.Abs(f.AccelX)) > 1, "a sideways knock");
	}

	[TestMethod]
	public void Telemetry_NothingDifferencedAcrossTheStop()
	{
		TelemetryFrame first = File(2)[0];

		Assert.AreEqual(1, first.GForce, 0.1, "no deceleration from before the stop leaks into the third file's first frame");
	}

	[TestMethod]
	public void Sidecar_RoundTrips()
	{
		string video = Path.Combine(Path.GetTempPath(), $"osmooverlay-synthetic-{Guid.NewGuid():N}.mp4");
		try
		{
			SyntheticTelemetry.Write(video, File(2));

			CollectionAssert.AreEqual(File(2).ToList(), SyntheticTelemetry.Read(video));
		}
		finally
		{
			System.IO.File.Delete(SyntheticTelemetry.SidecarPath(video));
		}
	}

	[TestMethod]
	public void Camera_IsFoundByIdAndBySidecar_ButNotListedAmongTheCameras()
	{
		ICameraFormat? camera = CameraFormats.Find("synthetic");

		Assert.IsNotNull(camera);
		Assert.IsFalse(CameraFormats.All.Contains(camera));

		string video = Path.Combine(Path.GetTempPath(), $"osmooverlay-synthetic-{Guid.NewGuid():N}.mp4");
		var streams = new JsonArray();
		Assert.IsNull(CameraFormats.Detect(video, streams), "no sidecar, no telemetry");
		try
		{
			SyntheticTelemetry.Write(video, File(0));

			Assert.AreSame(camera, CameraFormats.Detect(video, streams)?.Format);
		}
		finally
		{
			System.IO.File.Delete(SyntheticTelemetry.SidecarPath(video));
		}
	}

	[TestMethod]
	public void Camera_GravityIsTheFrameItself()
	{
		ICameraFormat camera = CameraFormats.Find("synthetic")!;

		Assert.AreEqual(new Direction(0.1, 0.9, -0.2), camera.Gravity(new TelemetryFrame(0, 0, 0, 0, 0, null, 0.1, 0.9, -0.2)));
	}

	[TestMethod]
	public void Plans_ScenesFillEveryFrame()
	{
		foreach (SyntheticRecording.FilePlan plan in SyntheticRecording.Plans)
		{
			int frames = plan.Scenes.Sum(s => (int)Math.Ceiling(s.Seconds * Fps - 1e-9));
			Assert.IsTrue(frames >= plan.Frames, $"{frames} frames of scenes for {plan.Frames}");
		}
	}

	[TestMethod]
	public void Arguments_TheDroppedFrameLeavesAGapInTheTimestamps()
	{
		List<string> dropped = SyntheticRecording.Arguments(SyntheticRecording.Plans[1], 1, "out.mp4", "hevc_nvenc");
		List<string> plain = SyntheticRecording.Arguments(SyntheticRecording.Plans[2], 2, "out.mp4", "libx265");

		StringAssert.Contains(dropped[dropped.IndexOf("-filter_complex") + 1], "select='not(eq(n\\,300))'");
		Assert.AreEqual("passthrough", dropped[dropped.IndexOf("-fps_mode") + 1], "or ffmpeg fills the gap back in");
		Assert.IsFalse(plain.Contains("-fps_mode"));
		StringAssert.Contains(plain[plain.IndexOf("-filter_complex") + 1], $"trim=end_frame={SyntheticRecording.Plans[2].Frames}");
		Assert.AreEqual("libx265", plain[plain.IndexOf("-c:v") + 1]);
		Assert.AreEqual("out.mp4", plain[^1]);
	}

	[TestMethod]
	public void Arguments_SoundRunsOnPastThePicture()
	{
		SyntheticRecording.FilePlan plan = SyntheticRecording.Plans[0];
		List<string> args = SyntheticRecording.Arguments(plan, 0, "out.mp4", "hevc_nvenc");

		string sound = args.Single(a => a.StartsWith("aevalsrc", StringComparison.Ordinal));
		StringAssert.Contains(sound, $":d={(plan.Frames / Fps + plan.SoundOverrunSeconds).ToString("0.######", CultureInfo.InvariantCulture)}");
	}
}
