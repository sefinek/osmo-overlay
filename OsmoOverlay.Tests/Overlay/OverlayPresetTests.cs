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
		OverlayPreset preset = OverlayPreset.CreateDefault("d", "Default", 3840, 2160);

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
	public void CreateDefault_At1080p_IsTheSameLayoutAtHalfSize()
	{
		OverlayPreset full = OverlayPreset.CreateDefault("d", "Default", 3840, 2160);
		OverlayPreset half = OverlayPreset.CreateDefault("d", "Default", 1920, 1080);

		foreach (OverlayElement element in full.Elements)
		{
			OverlayElement scaled = Get(half, element.Type);
			Assert.AreEqual(element.X / 2, scaled.X, 0.01, $"{element.Type} X");
			Assert.AreEqual(element.Y / 2, scaled.Y, 0.01, $"{element.Type} Y");
		}
	}
}
