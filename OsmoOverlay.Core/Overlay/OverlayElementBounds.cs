using SkiaSharp;

namespace OsmoOverlay.Core.Overlay;

/// <summary>
///     Layout constants shared between OverlayRenderer (drawing) and the GUI editor (hit-testing for
///     drag-to-reposition), so the two can't drift apart. Sizes are fixed per element type in V1 -
///     only position is editable.
/// </summary>
public static class OverlayElementBounds
{
	public const float Margin = 70f;
	public const float CompassRadius = 260f;
	public const float SunRadius = 90f;
	public const float SpeedRadius = 260f;
	public const float PitchRadius = 95f;

	private const float StatsBlockWidth = 480f;
	private const float StatsBlockHeight = 620f;

	public static SKRect GetBounds(OverlayElementType type, float x, float y)
	{
		return type switch
		{
			OverlayElementType.StatsBlock => new SKRect(x - 10, y - 50, x + StatsBlockWidth - 10,
				y + StatsBlockHeight - 50),
			OverlayElementType.Compass => Circle(x, y, CompassRadius),
			OverlayElementType.SunWidget => Circle(x, y, SunRadius + 70),
			OverlayElementType.PitchGauge => Circle(x, y, PitchRadius),
			OverlayElementType.SpeedGauge => Circle(x, y, SpeedRadius),
			_ => SKRect.Empty
		};
	}

	private static SKRect Circle(float cx, float cy, float radius)
	{
		return new SKRect(cx - radius, cy - radius, cx + radius, cy + radius);
	}
}
