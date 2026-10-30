using OsmoOverlay.Core.Telemetry;
using OsmoOverlay.Core.Telemetry.External;

namespace OsmoOverlay.Tests.Telemetry.External;

[TestClass]
public sealed class ExternalTrackSyncTests
{
	private const double Fps = 25;
	private static readonly DateTime TrackStart = new(2026, 10, 21, 14, 20, 49, DateTimeKind.Utc);

	/// <summary>A rider: stands, rides at varying speed, stands again, rides - the speed in m/s a second from the start.</summary>
	private static double Speed(double second)
	{
		return second switch
		{
			< 15 => 0,
			< 45 => 4 + 2 * Math.Sin(second / 4),
			< 70 => 0,
			< 110 => 7 + 2 * Math.Cos(second / 5),
			< 130 => 0,
			_ => 3
		};
	}

	private static ExternalTrack Track(double seconds = 150)
	{
		List<ExternalTrackPoint> points = [];
		double latitude = 53.1;
		for (int s = 0; s <= seconds; s++)
		{
			points.Add(new ExternalTrackPoint(TrackStart.AddSeconds(s), latitude, 18.9, 80, Speed(s)));
			latitude += Speed(s) * 9e-6;
		}

		return new ExternalTrack(points);
	}

	/// <summary>The camera of that rider: its accelerometer shakes in proportion to the speed - `startSecond` into the track.</summary>
	private static List<TelemetryFrame> Camera(double startSecond, double seconds, int seed = 3)
	{
		var random = new Random(seed);
		List<TelemetryFrame> frames = [];
		for (int f = 0; f < seconds * Fps; f++)
		{
			double shake = 0.02 + 0.03 * Speed(startSecond + f / Fps);
			double z = 1 + shake * (random.NextDouble() * 2 - 1) * 1.7;
			frames.Add(new TelemetryFrame(f, f / Fps, 0, 0, 0, null, 0, 0, z, HasGpsFix: false));
		}

		return frames;
	}

	[TestMethod]
	public void Estimate_OverlappingCameraClockDoesNotOverrideMotion()
	{
		TrackSyncEstimate? estimate = ExternalTrackSync.Estimate(Camera(30, 70), Fps, Track());

		Assert.IsNotNull(estimate);
		Assert.AreEqual(TrackSyncMethod.Motion, estimate.Method);
		Assert.AreEqual(30, (estimate.TrackTimeAtVideoStartUtc - TrackStart).TotalSeconds, 1.0);
	}

	[TestMethod]
	public void Estimate_ACameraWhoseClockIsMinutesOffIsMatchedByHowItMoved()
	{
		// The camera's clock says 14:27:45, seven minutes after the track ended, and the recording is really 30 s in.
		List<TelemetryFrame> frames = Camera(30, 70);

		TrackSyncEstimate? estimate = ExternalTrackSync.Estimate(frames, Fps, Track());

		Assert.IsNotNull(estimate);
		Assert.AreEqual(TrackSyncMethod.Motion, estimate.Method);
		Assert.AreEqual(30, (estimate.TrackTimeAtVideoStartUtc - TrackStart).TotalSeconds, 1.0);
		Assert.IsTrue(estimate.Correlation > 0.7, $"{estimate.Correlation}");
	}

	[TestMethod]
	public void Estimate_ARecordingStartingBeforeTheTrackIsMatchedToo()
	{
		// The track starts 20 s into the recording.
		List<TelemetryFrame> frames = Camera(-20, 90);

		TrackSyncEstimate? estimate = ExternalTrackSync.Estimate(frames, Fps, Track());

		Assert.IsNotNull(estimate);
		Assert.AreEqual(-20, (estimate.TrackTimeAtVideoStartUtc - TrackStart).TotalSeconds, 1.0);
	}

	[TestMethod]
	public void Estimate_ACameraThatShakesNothingLikeTheTrackGetsNoEstimate()
	{
		var random = new Random(7);
		List<TelemetryFrame> frames = [.. Enumerable.Range(0, (int)(80 * Fps)).Select(f => new TelemetryFrame(f, f / Fps, 0, 0, 0, null, 0, 0, 1 + 0.05 * random.NextDouble()))];

		Assert.IsNull(ExternalTrackSync.Estimate(frames, Fps, Track()));
	}

	[TestMethod]
	public void Estimate_ACameraThatDoesntMoveAtAllGetsNoEstimate()
	{
		List<TelemetryFrame> still = [.. Enumerable.Range(0, (int)(80 * Fps)).Select(f => new TelemetryFrame(f, f / Fps, 0, 0, 0, null, 0, 0, 1))];

		Assert.IsNull(ExternalTrackSync.Estimate(still, Fps, Track()));
		Assert.IsNull(ExternalTrackSync.Estimate([], Fps, Track()));
	}

	[TestMethod]
	public void Estimate_ARecordingTooShortOnTheTrackGetsNoEstimate()
	{
		Assert.IsNull(ExternalTrackSync.Estimate(Camera(0, 10), Fps, Track()));
	}

	[TestMethod]
	public void CameraShake_IsTheVariationOfTheAccelerometersLengthPerHalfSecond()
	{
		List<TelemetryFrame> frames = [.. Enumerable.Range(0, 50).Select(f => new TelemetryFrame(f, f / Fps, 0, 0, 0, null, 0, 0, f < 25 ? 1 : (f % 2 == 0 ? 1.2 : 0.8)))];

		double[] shake = ExternalTrackSync.CameraShake(frames, Fps, 2);

		Assert.AreEqual(4, shake.Length);
		Assert.AreEqual(0, shake[0], 1e-9);
		Assert.AreEqual(0.2, shake[3], 0.02);
	}

	[TestMethod]
	public void TrackSpeed_KeepsRecordedStopsAndBoundaryFixesButLeavesGapsUnknown()
	{
		var track = new ExternalTrack(
		[
			new ExternalTrackPoint(TrackStart, 53.1, 18.9, null, 3), new ExternalTrackPoint(TrackStart.AddSeconds(1), 53.1001, 18.9, null, 3),
			new ExternalTrackPoint(TrackStart.AddSeconds(31), 53.10015, 18.9, null, 0), new ExternalTrackPoint(TrackStart.AddSeconds(32), 53.1002, 18.9, null, 3),
			new ExternalTrackPoint(TrackStart.AddSeconds(62), 53.3, 18.9, null, 3), new ExternalTrackPoint(TrackStart.AddSeconds(63), 53.3001, 18.9, null, 3)
		]);

		double[] speed = ExternalTrackSync.TrackSpeed(track, 63);

		Assert.AreEqual(0, speed[2 * 31], "recorded stop");
		Assert.IsTrue(double.IsNaN(speed[2 * 15]), "a nearby position after a gap does not prove a standstill");
		Assert.AreEqual(3, speed[2 * 32], 0.5);
		Assert.IsTrue(double.IsNaN(speed[2 * 45]), "lost");
	}

	// The shake (per half second) of a 78 s walk with a camera and the speed of a recorder that dropped its fixes while the walker stood (a GoPro HERO12 sample;
	// the recording really began 4.9 s before the track). A match on the track's first third alone must not outrank one that covers the whole of it.
	private static readonly double[] WalkShake = [0.17, 0.24, 0.22, 0.15, 0.28, 0.13, 0.27, 0.32, 0.23, 0.11, 0.19, 0.24, 0.19, 0.18, 0.16, 0.23, 0.25, 0.21, 0.21, 0.17, 0.19, 0.19, 0.13, 0.16, 0.15, 0.16, 0.15, 0.13, 0.13, 0.13, 0.13, 0.09, 0.05, 0.01, 0.03, 0.03, 0.03, 0.01, 0.02, 0.02, 0.01, 0.01, 0.02, 0.02, 0.02, 0.02, 0.02, 0.02, 0.01, 0.01, 0.01, 0.02, 0.05, 0.02, 0.03, 0.02, 0.04, 0.03, 0.02, 0.02, 0.03, 0.05, 0.05, 0.04, 0.03, 0.03, 0.02, 0.02, 0.02, 0.03, 0.04, 0.04, 0.07, 0.02, 0.03, 0.04, 0.05, 0.08, 0.13, 0.13, 0.13, 0.18, 0.16, 0.21, 0.26, 0.22, 0.30, 0.29, 0.24, 0.25, 0.24, 0.26, 0.23, 0.19, 0.19, 0.17, 0.23, 0.18, 0.16, 0.12, 0.16, 0.13, 0.14, 0.17, 0.15, 0.11, 0.10, 0.12, 0.10, 0.16, 0.14, 0.18, 0.12, 0.14, 0.17, 0.09, 0.07, 0.08, 0.11, 0.08, 0.02, 0.03, 0.03, 0.02, 0.02, 0.01, 0.01, 0.01, 0.03, 0.03, 0.02, 0.02, 0.01, 0.04, 0.04, 0.03, 0.03, 0.02, 0.05, 0.03, 0.04, 0.04, 0.03, 0.03, 0.03, 0.03, 0.02, 0.02, 0.02, 0.02, 0.05, 0.02, 0.06, 0.01, 0.02, 0.07];

	private static readonly double[] WalkSpeedBeforeGap = [1.7, 1.6, 1.3, 1.3, 1.0, 1.4, 1.4, 1.5, 1.4, 1.0, 1.2, 1.0, 1.4, 1.1, 1.3, 0.8, 1.0, 0.9, 1.1, 1.0, 1.0, 0.3, 0.1];
	private static readonly double[] WalkSpeedAfterGap = [1.3, 1.1, 0.9, 1.1, 1.1, 1.1, 1.5, 0.9, 1.1, 1.0, 0.4, 0.5, 0.1];

	[TestMethod]
	public void Estimate_AMatchOverTheWholeTrackBeatsOneOverAThirdOfIt()
	{
		List<ExternalTrackPoint> points = [];
		double latitude = 53.1;
		for (int s = 0; s < WalkSpeedBeforeGap.Length; s++)
		{
			points.Add(new ExternalTrackPoint(TrackStart.AddSeconds(s), latitude, 18.9, 80, WalkSpeedBeforeGap[s]));
			latitude += WalkSpeedBeforeGap[s] * 9e-6;
		}

		for (int s = 0; s < WalkSpeedAfterGap.Length; s++)
		{
			points.Add(new ExternalTrackPoint(TrackStart.AddSeconds(55 + s), latitude, 18.9, 80, WalkSpeedAfterGap[s]));
			latitude += WalkSpeedAfterGap[s] * 9e-6;
		}

		var frames = new List<TelemetryFrame>();
		for (int frame = 0; frame < WalkShake.Length * Fps / 2; frame++)
		{
			double shake = WalkShake[(int)(frame / Fps / 0.5)];
			frames.Add(new TelemetryFrame(frame, frame / Fps, 0, 0, 0, null, 0, 0, 1 + (frame % 2 == 0 ? shake : -shake), HasGpsFix: false));
		}

		TrackSyncEstimate? estimate = ExternalTrackSync.Estimate(frames, Fps, new ExternalTrack(points));

		Assert.IsNotNull(estimate);
		Assert.AreEqual(4.9, (estimate.TrackTimeAtVideoStartUtc - TrackStart).TotalSeconds, 1);
	}
}
