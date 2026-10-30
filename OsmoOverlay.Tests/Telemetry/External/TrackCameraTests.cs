using OsmoOverlay.Core.Mapping;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Telemetry;
using OsmoOverlay.Core.Telemetry.External;

namespace OsmoOverlay.Tests.Telemetry.External;

[TestClass]
public sealed class TrackCameraTests
{
	// 10 Hz for a minute, moving east by 0.0001 degrees a frame.
	private static List<DerivedFrame> Frames(Func<int, double> speedKmh, Func<int, bool>? fix = null)
	{
		List<DerivedFrame> frames = [];
		for (int i = 0; i <= 600; i++)
		{
			var raw = new TelemetryFrame(i, i / 10.0, 50, 20 + i * 0.0001, 0, null, 0, 0, 1, HasGpsFix: fix?.Invoke(i) ?? true);
			frames.Add(new DerivedFrame(raw, speedKmh(i), 0, 0, 0, 0, 0, default, 0, 0, 1, 0, 0));
		}

		return frames;
	}

	[TestMethod]
	public void Spread_IsClosestWhileSlowAndWidestAtTheTracksPace()
	{
		List<DerivedFrame> frames = Frames(i => i < 300 ? 0 : 40);
		double reference = TrackCamera.ReferenceKmh(frames);

		Assert.AreEqual(40, reference, 1e-9);
		Assert.AreEqual(0, TrackCamera.At(frames, 10, reference)!.Value.Spread, 1e-9);
		Assert.AreEqual(1, TrackCamera.At(frames, 55, reference)!.Value.Spread, 1e-9);
	}

	[TestMethod]
	public void Spread_ChangesGraduallyAcrossASpeedChange()
	{
		List<DerivedFrame> frames = Frames(i => i < 300 ? 0 : 40);
		double reference = TrackCamera.ReferenceKmh(frames);

		double before = TrackCamera.At(frames, 24, reference)!.Value.Spread;
		double at = TrackCamera.At(frames, 30, reference)!.Value.Spread;
		double after = TrackCamera.At(frames, 36, reference)!.Value.Spread;

		Assert.IsTrue(before < at && at < after, $"{before} {at} {after}");
		Assert.AreEqual(0.5, at, 0.05);
	}

	[TestMethod]
	public void Position_IsTheTrackAtThatTime()
	{
		List<DerivedFrame> frames = Frames(_ => 10);

		TrackCamera.Shot shot = TrackCamera.At(frames, 30, 10)!.Value;

		Assert.AreEqual(50, shot.Latitude, 1e-9);
		Assert.AreEqual(20 + 300 * 0.0001, shot.Longitude, 1e-6);
	}

	[TestMethod]
	public void TimeOutsideTheTrack_StaysOnItsEnds()
	{
		List<DerivedFrame> frames = Frames(_ => 10);

		Assert.AreEqual(20, TrackCamera.At(frames, -5, 10)!.Value.Longitude, 1e-3);
		Assert.AreEqual(20 + 600 * 0.0001, TrackCamera.At(frames, 500, 10)!.Value.Longitude, 1e-3);
	}

	[TestMethod]
	public void WithoutAFixAtThatTime_TheNearestOneIsUsed()
	{
		List<DerivedFrame> frames = Frames(_ => 10, i => i < 100 || i > 400);

		TrackCamera.Shot shot = TrackCamera.At(frames, 25, 10)!.Value;

		Assert.AreEqual(20 + 99 * 0.0001, shot.Longitude, 2e-4);
	}

	[TestMethod]
	public void WithNoFixAtAll_ThereIsNoShot()
	{
		Assert.IsNull(TrackCamera.At(Frames(_ => 10, _ => false), 10, 10));
		Assert.IsNull(TrackCamera.At([], 10, 10));
	}

	[TestMethod]
	public void ReferenceSpeed_NeverFallsBelowAHover()
	{
		Assert.AreEqual(5, TrackCamera.ReferenceKmh(Frames(_ => 0.2)), 1e-9);
		Assert.AreEqual(5, TrackCamera.ReferenceKmh([]), 1e-9);
	}

	[TestMethod]
	public void TheSameTimeGivesTheSameShot_WhateverWasAskedBefore()
	{
		List<DerivedFrame> frames = Frames(i => i % 100);

		TrackCamera.Shot first = TrackCamera.At(frames, 33.3, 40)!.Value;
		TrackCamera.At(frames, 5, 40);
		TrackCamera.At(frames, 58, 40);

		Assert.AreEqual(first, TrackCamera.At(frames, 33.3, 40)!.Value);
	}

	[TestMethod]
	public void BetweenTwoFrames_ThePositionMovesSmoothly()
	{
		List<DerivedFrame> frames = Frames(_ => 10);

		double before = TrackCamera.At(frames, 30.0, 10)!.Value.Longitude;
		double quarter = TrackCamera.At(frames, 30.025, 10)!.Value.Longitude;
		double half = TrackCamera.At(frames, 30.05, 10)!.Value.Longitude;

		Assert.AreEqual(0.00005, half - before, 1e-9);
		Assert.AreEqual(0.000025, quarter - before, 1e-9);
		Assert.AreEqual(30.025 / 0.1 * 0.0001 + 20, TrackCamera.FixAt(frames, 30.025)!.Longitude, 1e-9);
	}

	[TestMethod]
	public void TheAircraftsYaw_TurnsTheShorterWayRound()
	{
		List<DerivedFrame> frames = Frames(_ => 10);
		frames[300] = frames[300] with { Raw = frames[300].Raw with { DroneAttitude = new DroneAttitude(0, 10, 350) } };
		frames[301] = frames[301] with { Raw = frames[301].Raw with { DroneAttitude = new DroneAttitude(4, 20, 10) } };

		DroneAttitude half = TrackCamera.FixAt(frames, 30.05)!.DroneAttitude!;

		double yaw = AngleMath.NormalizeDegrees(half.YawDegrees);
		Assert.IsLessThan(1e-9, Math.Min(yaw, 360 - yaw));
		Assert.AreEqual(2, half.PitchDegrees, 1e-9);
		Assert.AreEqual(15, half.RollDegrees, 1e-9);
	}

	[TestMethod]
	public void TheMarker_IsNotCarriedAcrossAGapOrWhereThereIsNoFix()
	{
		List<DerivedFrame> frames = Frames(_ => 10, i => i != 300);
		frames[301] = frames[301] with { Raw = frames[301].Raw with { StartsAfterGap = true } };

		Assert.IsNull(TrackCamera.FixAt(frames, 30.0));
		Assert.AreEqual(frames[301].Raw.Longitude, TrackCamera.FixAt(frames, 30.06)!.Longitude, 1e-9);
		Assert.AreEqual(frames[299].Raw.Longitude, TrackCamera.FixAt(frames, 29.94)!.Longitude, 1e-9);
		Assert.IsNull(TrackCamera.FixAt([], 1));
	}

	[TestMethod]
	public void TrackMapSettings_AreBroughtBackIntoRange()
	{
		Assert.AreEqual(RouteMapMosaic.MaxZoom, (new TrackMapSettings(MaxZoom: 99)).Sanitized().MaxZoom);
		Assert.AreEqual(RouteMapMosaic.MinZoom, (new TrackMapSettings(MaxZoom: -4)).Sanitized().MaxZoom);
		Assert.AreEqual(TrackMapSettings.Default, TrackMapSettings.From(new OverlaySettings()));
		Assert.AreEqual(12, TrackMapSettings.From(new OverlaySettings(TrackMap: new TrackMapSettings(MaxZoom: 12))).MaxZoom);
	}
}
