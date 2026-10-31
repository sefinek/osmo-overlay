using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Telemetry;
using OsmoOverlay.Core.Telemetry.External;
using OsmoOverlay.Drones.Dji;
using SkiaSharp;
using static OsmoOverlay.Tests.Drones.Dji.FlightLogBuilder;

namespace OsmoOverlay.Tests.Drones.Dji;

[TestClass]
public sealed class DroneControlsTests
{
	private static readonly DateTime Started = new(2026, 10, 29, 13, 0, 0, DateTimeKind.Utc);

	private static byte[] RcRecord(params ushort[] channels)
	{
		byte[] c = new byte[25];
		for (int i = 0; i < channels.Length; i++) BitConverter.GetBytes(channels[i]).CopyTo(c, i * 2);
		return c;
	}

	private static byte[] OsdWithAttitude(double pitch, double roll, double yaw)
	{
		byte[] c = OsdRecord(50, 20, 0, 0, 0, 10, true);
		BitConverter.GetBytes((short)Math.Round(pitch * 10)).CopyTo(c, 24);
		BitConverter.GetBytes((short)Math.Round(roll * 10)).CopyTo(c, 26);
		BitConverter.GetBytes((short)Math.Round(yaw * 10)).CopyTo(c, 28);
		return c;
	}

	[TestMethod]
	public void Decode_TheSticksAreTheRemotesChannelsAroundTheirCenter()
	{
		var builder = new FlightLogBuilder(12, Started);
		builder.Add(Osd, OsdRecord(50, 20, 0, 0, 0, 10, true));
		builder.Add(4, RcRecord(364, 1024, 1684, 1354, 1024));
		builder.Add(4, RcRecord(0, 0, 0, 0, 0));

		IReadOnlyList<FlightSticksSample> sticks = FlightLog.Parse(builder.Build()).Decode(null).Sticks!;

		Assert.AreEqual(1, sticks.Count, "a record outside the channels' range is not a reading");
		Assert.AreEqual(-1, sticks[0].Sticks.Aileron, 1e-9);
		Assert.AreEqual(0, sticks[0].Sticks.Elevator, 1e-9);
		Assert.AreEqual(1, sticks[0].Sticks.Throttle, 1e-9);
		Assert.AreEqual(0.5, sticks[0].Sticks.Rudder, 1e-9);
	}

	[TestMethod]
	public void Decode_TheAttitudeIsPitchRollAndYawInTenthsOfADegree()
	{
		var builder = new FlightLogBuilder(12, Started);
		builder.Add(Osd, OsdWithAttitude(-12.5, 8.4, 39.6));
		builder.Add(Osd, OsdWithAttitude(3, -4, 359));

		FlightSample[] samples = [.. FlightLog.Parse(builder.Build()).Decode(null).Samples];

		Assert.AreEqual(new DroneAttitude(-12.5, 8.4, 39.6), samples[0].Attitude);
		Assert.AreEqual(new DroneAttitude(3, -4, 359), samples[1].Attitude);
	}

	[TestMethod]
	public void TheTrackHoldsTheSticksAndTheAttitudeAndTheWidgetsNeedThem()
	{
		var builder = new FlightLogBuilder(12, Started);
		builder.Add(Custom, CustomRecord(Started));
		builder.Add(Osd, OsdWithAttitude(1, 2, 3));
		builder.Add(4, RcRecord(1684, 1024, 1024, 1024, 1024));
		builder.Add(Osd, OsdWithAttitude(4, 5, 6));
		builder.Add(Osd, OsdWithAttitude(7, 8, 9));
		FlightLog log = FlightLog.Parse(builder.Build());
		ExternalTrack track = DjiFlightLogFormat.ToTrack(log, log.Decode(null));

		Assert.IsNull(track.DroneSticksAt(Started.AddSeconds(-1)));
		Assert.AreEqual(1, track.DroneSticksAt(Started.AddSeconds(0.15))!.Aileron, 1e-9);

		TelemetryFrame frame = new(0, 0, 50, 20, 100, null, 0, 0, 1);
		var none = OverlayAvailability.Of([frame], false, null);
		Assert.IsFalse(none.DroneSticks);
		Assert.IsFalse(none.DroneAttitude);
		var shown = OverlayAvailability.Of([frame with { DroneSticks = new DroneSticks(0, 0, 0, 0), DroneAttitude = new DroneAttitude(0, 0, 0) }], false, null);
		Assert.IsTrue(OverlayDataRequirements.IsSupported(OverlayElementType.RcSticks, shown));
		Assert.IsTrue(OverlayDataRequirements.IsSupported(OverlayElementType.AttitudeIndicator, shown));
		Assert.IsTrue(OverlayDataRequirements.IsSupported(OverlayElementType.Orientation3D, shown));
		Assert.IsFalse(OverlayDataRequirements.IsSupported(OverlayElementType.RcSticks, none));
	}

	[TestMethod]
	[DataRow(0.0, 0.0, 0)]
	[DataRow(0.1, -0.15, 0)]
	[DataRow(0.5, 0.0, 1)]
	[DataRow(-0.8, 0.9, 1)]
	[DataRow(1.0, -0.2, 2)]
	[DataRow(0.0, -1.0, 2)]
	public void ADotIsYellowNearTheCenterOrangeInBetweenAndRedAtTheEnd(double x, double y, int level)
	{
		SKColor[] colors = [SKColor.Parse("#F2D31B"), SKColor.Parse("#FF9A2E"), SKColor.Parse("#FF1F1F")];
		Assert.AreEqual(colors[level], OverlayRenderer.StickColor(x, y));
	}

	[TestMethod]
	public void BothDronePresetsShowTheSticksAndTheDials()
	{
		foreach (OverlayPreset preset in new[] { OverlayPreset.CreateDrone("x", "Drones"), OverlayPreset.CreateFpv("y", "FPV") })
		{
			foreach (OverlayElementType type in new[] { OverlayElementType.RcSticks, OverlayElementType.AttitudeIndicator, OverlayElementType.Orientation3D })
				Assert.IsTrue(preset.Elements.Single(e => e.Type == type).Visible, type.ToString());
		}
	}

	[TestMethod]
	public void AFlightAlertIsRoomierThanItsTextAndNeverNarrowerThanTheMinimum()
	{
		Assert.AreEqual(OverlayElementBounds.FlightAlertMinWidth, OverlayElementBounds.FlightAlertWidth(100));
		Assert.IsTrue(OverlayElementBounds.FlightAlertWidth(1000) > 1000 + OverlayElementBounds.FlightAlertExtraPadding);
		Assert.IsTrue(OverlayElementBounds.FlightAlertsBox(null, 1).Width >= OverlayElementBounds.FlightAlertMinWidth);
	}

	[TestMethod]
	public void TheAttitudeIndicatorMovesTheHorizonWithThePitchAndTheRoll()
	{
		const int width = 1920, height = 1080;

		byte[] Render(DroneAttitude attitude)
		{
			TelemetryFrame raw = new(0, 0, 50, 20, 100, null, 0, 0, 1, DroneAttitude: attitude);
			List<DerivedFrame> frames = TelemetryProcessor.Process([raw], null);
			using var renderer = new OverlayRenderer(width, height, 0, [new AttitudeIndicatorElement { X = OverlayElementBounds.ReferenceWidth / 2, Y = OverlayElementBounds.ReferenceHeight / 2, Id = "attitude" }], frames, showWatermark: false);
			byte[] buffer = new byte[width * height * 4];
			renderer.RenderInto(frames[0], buffer, premultiplied: true);
			return buffer;
		}

		static bool IsGround(byte[] buffer, int x, int y)
		{
			int at = (y * width + x) * 4;
			return buffer[at] > 120 && buffer[at + 2] < 90 && buffer[at + 3] > 200;
		}

		static int GroundTop(byte[] buffer)
		{
			return Enumerable.Range(0, height).First(y => IsGround(buffer, width / 2 + 20, y));
		}

		byte[] level = Render(new DroneAttitude(0, 0, 0)), noseUp = Render(new DroneAttitude(20, 0, 0)), banked = Render(new DroneAttitude(0, 30, 0));
		Assert.IsTrue(GroundTop(noseUp) > GroundTop(level), "with the nose up the horizon is below the center line");
		Assert.IsFalse(level.AsSpan().SequenceEqual(banked));
		Assert.IsTrue(IsGround(level, width / 2 - 30, height / 2 + 20));
		Assert.IsFalse(IsGround(level, width / 2 - 30, height / 2 - 20));
	}

	[TestMethod]
	public void FindFor_TakesTheLogBesideTheRecordingWhoseFlightCoversItsStart()
	{
		string folder = Path.Combine(Path.GetTempPath(), "ovl-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(folder);
		try
		{
			File.WriteAllBytes(Path.Combine(folder, "FlightRecord_early.txt"), new FlightLogBuilder(12, Started.AddHours(-3)) { DurationSeconds = 400 }.Build());
			File.WriteAllBytes(Path.Combine(folder, "FlightRecord_match.txt"), new FlightLogBuilder(12, Started) { DurationSeconds = 400 }.Build());
			File.WriteAllText(Path.Combine(folder, "FlightRecord_broken.txt"), "not a log");
			string video = Path.Combine(folder, "DJI_0001.MP4");
			var format = new DjiFlightLogFormat();

			Assert.AreEqual("FlightRecord_match.txt", Path.GetFileName(format.FindFor(video, Started.AddSeconds(200))));
			Assert.AreEqual("FlightRecord_match.txt", Path.GetFileName(format.FindFor(video, Started.AddSeconds(430))), "within the slack after the flight");
			Assert.IsNull(format.FindFor(video, Started.AddSeconds(600)));
			Assert.IsNull(format.FindFor(Path.Combine(folder, "missing", "DJI_0001.MP4"), Started));
		}
		finally
		{
			Directory.Delete(folder, true);
		}
	}

	[TestMethod]
	public void ContainingSpanAnchor_PlacesAClipThatIsOnlyPartOfTheLogsRecordingSpan()
	{
		var builder = new FlightLogBuilder(12, Started);
		builder.Add(Custom, CustomRecord(Started));
		for (int i = 0; i < 600; i++) builder.Add(Osd, OsdRecord(50 + i * 1e-5, 20, 5, 0, 0, 10, true));
		builder.Add(Camera, CameraRecord(true));
		for (int i = 0; i < 600; i++) builder.Add(Osd, OsdRecord(50.01 + i * 1e-5, 20, 5, 0, 0, 10, true));
		builder.Add(Camera, CameraRecord(false));
		FlightLog log = FlightLog.Parse(builder.Build());
		ExternalTrack track = DjiFlightLogFormat.ToTrack(log, log.Decode(null));
		ExternalTrackRecording span = track.Recordings.Single();
		List<TelemetryFrame> frames = [.. Enumerable.Range(0, 300).Select(i => new TelemetryFrame(i, i / 10.0, 0, 0, 0, null, 0, 0, 0, HasGpsFix: false))];

		Assert.IsNull(ExternalTrackSource.RecordingAnchor(frames, 10, track), "the clip is shorter than the span");
		Assert.AreEqual(span.StartUtc, ExternalTrackSource.ContainingSpanAnchor(frames, 10, track, span.StartUtc.AddSeconds(-2.7)));
		Assert.AreEqual(span.StartUtc.AddSeconds(31), ExternalTrackSource.ContainingSpanAnchor(frames, 10, track, span.StartUtc.AddSeconds(31)));
		Assert.IsNull(ExternalTrackSource.ContainingSpanAnchor(frames, 10, track, span.EndUtc.AddMinutes(5)));
		Assert.IsNull(ExternalTrackSource.ContainingSpanAnchor(frames, 10, track, null));

		TrackAlignment? proposed = ExternalTrackSource.Propose(frames, 10, span.StartUtc.AddSeconds(31), track);
		Assert.AreEqual(TrackAlignmentMethod.ContainerClock, proposed?.Method);
		Assert.IsTrue(proposed!.NeedsVerification, "a clock is off by the difference of two clocks");
	}

	[TestMethod]
	public void Coverage_IsTheShareOfTheRecordingInsideTheTracksRange()
	{
		var builder = new FlightLogBuilder(12, Started);
		builder.Add(Custom, CustomRecord(Started));
		for (int i = 0; i < 600; i++) builder.Add(Osd, OsdRecord(50 + i * 1e-5, 20, 5, 0, 0, 10, true));
		FlightLog log = FlightLog.Parse(builder.Build());
		ExternalTrack track = DjiFlightLogFormat.ToTrack(log, log.Decode(null));
		double length = (track.EndUtc - track.StartUtc).TotalSeconds;

		Assert.AreEqual(1, ExternalTrackSource.Coverage(track, new TrackSynchronization(new TrackSyncAnchor(0, track.StartUtc)), length), 1e-6);
		Assert.AreEqual(0.5, ExternalTrackSource.Coverage(track, new TrackSynchronization(new TrackSyncAnchor(0, track.StartUtc.AddSeconds(length / 2))), length), 1e-6);
		Assert.AreEqual(0, ExternalTrackSource.Coverage(track, new TrackSynchronization(new TrackSyncAnchor(0, track.EndUtc.AddSeconds(5))), length));
	}
}
