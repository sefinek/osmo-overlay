using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Telemetry;
using SkiaSharp;

namespace OsmoOverlay.Tests.Overlay;

[TestClass]
public sealed class GpsLossRouteTests
{
	private static DerivedFrame Frame(int index, double seconds, double east, bool fix)
	{
		var raw = new TelemetryFrame(index, seconds, 50, 20, 0, null, 0, 0, 0, HasGpsFix: fix);
		return new DerivedFrame(raw, 20, 0, 0, 0, 0, 0, default, east, 0, 1, 0, 0);
	}

	/// <summary>One frame a second, 5 m apart; the frames in `lost` have no fix and hold the position the fix was lost at.</summary>
	private static List<DerivedFrame> Drive(int count, Func<int, bool> hasFix, double step = 1)
	{
		List<DerivedFrame> frames = [];
		double east = 0;
		for (int i = 0; i < count; i++)
		{
			bool fix = hasFix(i);
			if (fix) east = i * 5;
			frames.Add(Frame(i, i * step, east, fix));
		}

		return frames;
	}

	private static RouteTrail TrailOf(List<DerivedFrame> frames)
	{
		var trail = new RouteTrail();
		trail.MoveTo(frames, frames.Count - 1);
		return trail;
	}

	[TestMethod]
	public void Trail_MarksTheFirstFixAfterALossOfASecondOrMore()
	{
		RouteTrail trail = TrailOf(Drive(10, i => i is < 3 or > 5));

		List<TrailPoint> resumed = [.. trail.Points.ToArray().Where(p => p.AfterGpsLoss)];
		Assert.AreEqual(1, resumed.Count);
		Assert.AreEqual(30, resumed[0].East, 1e-9, "frame 6, where the signal came back");
		Assert.IsFalse(resumed[0].AfterCut);
	}

	[TestMethod]
	public void Trail_AShorterLossIsNotMarked()
	{
		RouteTrail trail = TrailOf(Drive(20, i => i is < 4 or > 5, 0.25));

		Assert.IsFalse(trail.Points.ToArray().Any(p => p.AfterGpsLoss));
	}

	[TestMethod]
	public void Trail_NoFixBeforeTheFirstOneIsNotALoss()
	{
		RouteTrail trail = TrailOf(Drive(10, i => i > 3));

		Assert.IsFalse(trail.Points.ToArray().Any(p => p.AfterGpsLoss));
	}

	[TestMethod]
	public void Trail_BuiltAgainAfterASeekBackIsTheSame()
	{
		List<DerivedFrame> frames = Drive(10, i => i is < 3 or > 5);
		RouteTrail trail = TrailOf(frames);
		trail.MoveTo(frames, 2);
		trail.MoveTo(frames, 9);

		Assert.AreEqual(1, trail.Points.ToArray().Count(p => p.AfterGpsLoss));
	}

	private static SKBitmap Draw(RouteJoin join)
	{
		var bitmap = new SKBitmap(100, 20);
		using var canvas = new SKCanvas(bitmap);
		using var route = new RouteGeometry(false, 50);
		route.Add(new SKPoint(5, 10), 20);
		route.Add(new SKPoint(15, 10), 20);
		route.Add(new SKPoint(85, 10), 20, join);
		route.Add(new SKPoint(95, 10), 20);

		using var stroke = new SKPaint { Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Butt, IsAntialias = false };
		using var dashes = new SKPaint { Style = SKPaintStyle.Stroke, PathEffect = SKPathEffect.CreateDash([4, 4], 0), IsAntialias = false };
		route.Draw(canvas, SKMatrix.Identity, SKColors.Red, 2, stroke, dashes);
		return bitmap;
	}

	private static int Painted(SKBitmap bitmap, int fromX, int toX)
	{
		int count = 0;
		for (int x = fromX; x < toX; x++)
		{
			if (bitmap.GetPixel(x, 10).Alpha > 0) count++;
		}

		return count;
	}

	[TestMethod]
	public void Geometry_GpsLossJoinedByTheSettingOfTheWidget()
	{
		using SKBitmap straight = Draw(RouteJoin.Straight), gap = Draw(RouteJoin.Gap), dashed = Draw(RouteJoin.Dashed);

		Assert.AreEqual(60, Painted(straight, 20, 80), "a solid line over the loss");
		Assert.AreEqual(0, Painted(gap, 20, 80), "nothing travelled, nothing drawn");
		int dashes = Painted(dashed, 20, 80);
		Assert.IsTrue(dashes is > 10 and < 50, $"dashes, not all of it: {dashes}");
		Assert.IsTrue(Painted(gap, 0, 15) > 0 && Painted(gap, 86, 95) > 0, "the travelled parts stay");
	}
}
