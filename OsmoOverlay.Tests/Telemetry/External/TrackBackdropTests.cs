using OsmoOverlay.Core.Mapping;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Telemetry;
using OsmoOverlay.Core.Telemetry.External;
using SkiaSharp;

namespace OsmoOverlay.Tests.Telemetry.External;

[TestClass]
public sealed class TrackBackdropTests
{
	private const int Width = 320, Height = 180, Zoom = 10;

	private static TrackBackdrop Backdrop(TrackMapSettings options, IReadOnlyList<DerivedFrame> route)
	{
		var bitmap = new SKBitmap(2048, 2048);
		using (var canvas = new SKCanvas(bitmap))
		{
			canvas.Clear(new SKColor(70, 100, 60));
			using var stripe = new SKPaint { Color = new SKColor(90, 120, 80) };
			for (int x = 0; x < 2048; x += 128) canvas.DrawRect(x, 0, 64, 2048, stripe);
		}

		var mosaic = new RouteMapMosaic(bitmap, 0, 0, Zoom, new GeoBounds(0, 1, 0, 1), new SKRect(0, 0, 2048, 2048), 1);
		return new TrackBackdrop(mosaic, options, 1920, route);
	}

	// 10 Hz for a minute, moving east across the map; the aircraft's attitude only where asked for.
	private static List<DerivedFrame> Frames(bool aircraft)
	{
		List<DerivedFrame> frames = [];
		for (int i = 0; i <= 600; i++)
		{
			double n = 256 * Math.Pow(2, Zoom);
			double lon = (800 + i * 0.6) / n * 360 - 180;
			double lat = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * 1024 / n))) * 180 / Math.PI;
			var raw = new TelemetryFrame(i, i / 10.0, lat, lon, 0, null, 0, 0, 1, HasGpsFix: true,
				DroneAttitude: aircraft ? new DroneAttitude(5, -10, 90) : null);
			frames.Add(new DerivedFrame(raw, 20, 0, 0, 0, 0, 0, default, 0, 0, 1, 0, 0));
		}

		return frames;
	}

	private static byte[] Draw(TrackBackdrop backdrop, IReadOnlyList<DerivedFrame> frames, double seconds, int width = Width, int height = Height)
	{
		byte[] bgra = new byte[width * height * 4];
		backdrop.DrawInto(bgra, width, height, seconds, frames);
		return bgra;
	}

	[TestMethod]
	[DataRow(false, false)]
	[DataRow(true, false)]
	[DataRow(false, true)]
	[DataRow(true, true)]
	public void TheSameTimeGivesTheSamePicture_WhateverWasDrawnBefore(bool follow, bool aircraft)
	{
		List<DerivedFrame> frames = Frames(aircraft);
		using TrackBackdrop backdrop = Backdrop(new TrackMapSettings(Follow: follow), frames);

		byte[] first = Draw(backdrop, frames, 20);
		Draw(backdrop, frames, 5);
		Draw(backdrop, frames, 55);
		Draw(backdrop, frames, 20, 160, 90);

		CollectionAssert.AreEqual(first, Draw(backdrop, frames, 20));
	}

	[TestMethod]
	public void ThePictureFollowsTheTime()
	{
		List<DerivedFrame> frames = Frames(true);
		using TrackBackdrop backdrop = Backdrop(new TrackMapSettings(Follow: false), frames);

		CollectionAssert.AreNotEqual(Draw(backdrop, frames, 10), Draw(backdrop, frames, 40));
	}

	[TestMethod]
	public void AfterItIsDisposed_NothingIsDrawnAndDisposingAgainIsHarmless()
	{
		List<DerivedFrame> frames = Frames(true);
		TrackBackdrop backdrop = Backdrop(new TrackMapSettings(), frames);
		backdrop.Dispose();
		backdrop.Dispose();

		Assert.IsTrue(Draw(backdrop, frames, 10).All(b => b == 0));
	}

	[TestMethod]
	public void TheLineIsColoredBySpeedOrOneColor_AsAsked()
	{
		List<DerivedFrame> frames = [.. Frames(false).Select((f, i) => f with { SpeedKmh = i / 6.0 })];
		using TrackBackdrop bySpeed = Backdrop(new TrackMapSettings(Follow: false, ColorBySpeed: true), frames);
		using TrackBackdrop flat = Backdrop(new TrackMapSettings(Follow: false, ColorBySpeed: false), frames);

		CollectionAssert.AreNotEqual(Draw(bySpeed, frames, 59), Draw(flat, frames, 59));
	}

	[TestMethod]
	public void TheColorOfTheLineIsTheOneAsked()
	{
		List<DerivedFrame> frames = Frames(false);
		using TrackBackdrop green = Backdrop(new TrackMapSettings(Follow: false, ColorBySpeed: false, TrailColor: "#00FF00"), frames);
		using TrackBackdrop red = Backdrop(new TrackMapSettings(Follow: false, ColorBySpeed: false, TrailColor: "#FF0000"), frames);

		CollectionAssert.AreNotEqual(Draw(green, frames, 30), Draw(red, frames, 30));
	}

	[TestMethod]
	public void WithoutTheRestOfTheRoute_OnlyWhatWasFlownIsDrawn()
	{
		List<DerivedFrame> frames = Frames(false);
		using TrackBackdrop with = Backdrop(new TrackMapSettings(Follow: false, ShowAhead: true), frames);
		using TrackBackdrop without = Backdrop(new TrackMapSettings(Follow: false, ShowAhead: false), frames);

		CollectionAssert.AreNotEqual(Draw(with, frames, 5), Draw(without, frames, 5));
	}

	[TestMethod]
	public void TheMarkerSizeIsTheOneAsked()
	{
		List<DerivedFrame> frames = Frames(true);
		using TrackBackdrop small = Backdrop(new TrackMapSettings(Follow: true, MarkerScale: 0.5f), frames);
		using TrackBackdrop large = Backdrop(new TrackMapSettings(Follow: true, MarkerScale: 2), frames);

		CollectionAssert.AreNotEqual(Draw(small, frames, 20), Draw(large, frames, 20));
	}
}
