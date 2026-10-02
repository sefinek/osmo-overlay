using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Tests.Overlay;

[TestClass]
public sealed class OverlayPresetTests
{
	private static OverlayElement Get(OverlayPreset preset, OverlayElementType type)
	{
		return preset.Elements.Single(e => e.Type == type);
	}

	[TestMethod]
	public void CreateDefault_At4K_PlacesWidgetsOnTheMargin()
	{
		var preset = OverlayPreset.CreateDefault("d", "Default");

		(OverlayElementType Type, float X, float Y)[] expected =
		[
			(OverlayElementType.DateTimeText, 70, 104),
			(OverlayElementType.Elevation, 70, 204),
			(OverlayElementType.Gradient, 70, 424),
			(OverlayElementType.Distance, 70, 644),
			(OverlayElementType.SpeedGauge, 3500, 1832),
			(OverlayElementType.RollGauge, 3500, 1421),
			(OverlayElementType.MapWidget, 332, 1828),
			(OverlayElementType.GMeter, 3657, 183),
			(OverlayElementType.TripProgressBar, 1920, 2074)
		];

		foreach ((OverlayElementType type, float x, float y) in expected)
		{
			OverlayElement element = Get(preset, type);
			Assert.AreEqual(x, element.X, 0.01, $"{type} X");
			Assert.AreEqual(y, element.Y, 0.01, $"{type} Y");
		}
	}

	[TestMethod]
	public void CreateDefault_StaysInsideTheReferenceFrame()
	{
		foreach (OverlayElement element in OverlayPreset.CreateDefault("d", "Default").Elements)
		{
			Assert.IsTrue(element.X is >= 0 and <= OverlayElementBounds.ReferenceWidth, $"{element.Type} X {element.X}");
			Assert.IsTrue(element.Y is >= 0 and <= OverlayElementBounds.ReferenceHeight, $"{element.Type} Y {element.Y}");
		}
	}

	[TestMethod]
	public void ToPixels_ScalesEachAxisOnItsOwn()
	{
		(float x, float y) = OverlayElementBounds.ToPixels(3840, 2160, 2560, 1440);
		Assert.AreEqual(2560f, x, 0.001);
		Assert.AreEqual(1440f, y, 0.001);

		(x, y) = OverlayElementBounds.ToPixels(1920, 1080, 1440, 1080);
		Assert.AreEqual(720f, x, 0.001, "a 4:3 frame's width is its own axis");
		Assert.AreEqual(540f, y, 0.001);
	}

	[TestMethod]
	public void ToReference_UndoesToPixels()
	{
		(float px, float py) = OverlayElementBounds.ToPixels(1234.5f, 678.9f, 1920, 1080);
		(float x, float y) = OverlayElementBounds.ToReference(px, py, 1920, 1080);

		Assert.AreEqual(1234.5f, x, 0.01);
		Assert.AreEqual(678.9f, y, 0.01);
	}
}
