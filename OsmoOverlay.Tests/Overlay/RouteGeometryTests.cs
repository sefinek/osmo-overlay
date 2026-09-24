using OsmoOverlay.Core.Overlay;
using SkiaSharp;

namespace OsmoOverlay.Tests.Overlay;

[TestClass]
public sealed class RouteGeometryTests
{
	private const int Size = 100;
	private const float Width = 4;

	/// <summary>Draws the route in white on a transparent canvas; the result's alpha is what the tests read.</summary>
	private static SKBitmap Draw(RouteGeometry route, SKMatrix toCanvas)
	{
		var bitmap = new SKBitmap(new SKImageInfo(Size, Size, SKColorType.Bgra8888, SKAlphaType.Premul));
		using var canvas = new SKCanvas(bitmap);
		canvas.Clear(SKColors.Transparent);
		using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };
		using var dashed = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke };
		route.Draw(canvas, toCanvas, SKColors.White, Width, stroke, dashed);
		return bitmap;
	}

	private static bool Drawn(SKBitmap bitmap, int x, int y)
	{
		return bitmap.GetPixel(x, y).Alpha > 128;
	}

	/// <summary>10-40 along y = 50, then - after a cut - 60-90.</summary>
	private static RouteGeometry TwoPieces(RouteJoin join, float step = 2, float minStep = 0)
	{
		var route = new RouteGeometry(join, false, 50, minStep);
		for (float x = 10; x <= 40; x += step) route.Add(new SKPoint(x, 50), 20, false);
		for (float x = 60; x <= 90; x += step) route.Add(new SKPoint(x, 50), 20, x == 60);
		return route;
	}

	[TestMethod]
	public void Gap_LeavesTheCutOut_StraightDrawsAcrossIt()
	{
		using (RouteGeometry gap = TwoPieces(RouteJoin.Gap))
		using (SKBitmap bitmap = Draw(gap, SKMatrix.Identity))
		{
			Assert.IsTrue(Drawn(bitmap, 25, 50));
			Assert.IsTrue(Drawn(bitmap, 75, 50));
			Assert.IsFalse(Drawn(bitmap, 50, 50));
		}

		using RouteGeometry straight = TwoPieces(RouteJoin.Straight);
		using SKBitmap straightBitmap = Draw(straight, SKMatrix.Identity);
		Assert.IsTrue(Drawn(straightBitmap, 50, 50));
	}

	[TestMethod]
	public void MinStep_LeavesPointsOut_ButStillReachesTheLastOneBeforeACut()
	{
		using RouteGeometry route = TwoPieces(RouteJoin.Gap, 2, 7);
		using SKBitmap bitmap = Draw(route, SKMatrix.Identity);

		Assert.AreEqual(32, route.Count, "every point counts, the left-out ones too");
		Assert.IsTrue(Drawn(bitmap, 40, 50), "40 is 6 past the last kept point (34), less than MinStep");
		Assert.IsFalse(Drawn(bitmap, 50, 50));
	}

	[TestMethod]
	public void Matrix_ScalesThePoints_NotTheWidth()
	{
		using var route = new RouteGeometry(RouteJoin.Gap, false, 50);
		route.Add(new SKPoint(5, 25), 20, false);
		route.Add(new SKPoint(40, 25), 20, false);
		using SKBitmap bitmap = Draw(route, SKMatrix.CreateScale(2, 2));

		Assert.IsTrue(Drawn(bitmap, 70, 50));
		Assert.IsTrue(Drawn(bitmap, 70, 51));
		Assert.IsFalse(Drawn(bitmap, 70, 54), "4 px wide stays 4 px, not 8");
	}

	[TestMethod]
	public void LongRoute_SpanningManyChunks_IsDrawnWhole()
	{
		using var route = new RouteGeometry(RouteJoin.Gap, true, 50);
		for (var i = 0; i < 1000; i++) route.Add(new SKPoint(5 + i * 0.09f, 50), i % 60, false);
		using SKBitmap bitmap = Draw(route, SKMatrix.Identity);

		for (var x = 6; x < 94; x += 4) Assert.IsTrue(Drawn(bitmap, x, 50), $"x = {x}");
	}
}
