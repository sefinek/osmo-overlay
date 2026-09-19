using SkiaSharp;

namespace OsmoOverlay.Core.Overlay;

/// <summary>
///     Layout constants shared between OverlayRenderer (drawing) and the GUI editor (hit-testing for
///     drag-to-reposition), so the two can't drift apart. Sizes are fixed per element type in V1 -
///     only position is editable. All sizes are expressed at ReferenceWidth x ReferenceHeight (4K);
///     GetScale gives the factor both sides must apply so the layout looks the same (proportionally)
///     at any actual video resolution instead of overlapping on smaller frames.
/// </summary>
public static class OverlayElementBounds
{
	public const float ReferenceWidth = 3840f;
	public const float ReferenceHeight = 2160f;

	public const float Margin = 70f;
	public const float CompassRadius = 260f;
	public const float SunRadius = 90f;
	public const float SpeedRadius = 260f;
	public const float PitchRadius = 95f;
	public const float MapRadius = 260f;
	public const float GMeterRadius = 110f;

	public const float DateTimeWidth = 420f;
	public const float DateTimeHeight = 70f;
	public const float StatWidth = 380f;
	public const float StatHeight = 170f;
	public const float CameraInfoWidth = 380f;
	public const float CameraInfoHeight = 220f;
	public const float ProgressBarWidth = 760f;
	public const float ProgressBarHeight = 60f;

	public static float GetScale(int width, int height)
	{
		return Math.Min(width / ReferenceWidth, height / ReferenceHeight);
	}

	public static SKRect GetBounds(OverlayElementType type, float x, float y, float scale)
	{
		return type switch
		{
			// DrawTimeText renders the text with its baseline at (x, y), so the glyphs sit mostly
			// above that anchor - the hit box must follow, or clicking the visible text misses it.
			OverlayElementType.DateTimeText or OverlayElementType.UtcTimeText or OverlayElementType.ElapsedTimeText
				or OverlayElementType.CameraModelText => new SKRect(x, y - (DateTimeHeight - 10) * scale,
					x + DateTimeWidth * scale, y + 10 * scale),
			OverlayElementType.Elevation or OverlayElementType.Gradient or OverlayElementType.Distance =>
				new SKRect(x - 10 * scale, y - 50 * scale, x + (StatWidth - 10) * scale, y + (StatHeight - 50) * scale),
			OverlayElementType.CameraInfo =>
				new SKRect(x - 10 * scale, y - 50 * scale, x + (CameraInfoWidth - 10) * scale, y + (CameraInfoHeight - 50) * scale),
			// TripProgressBar's anchor is the bar's vertical/horizontal center (DrawTripProgressBar
			// translates to (x, y) and draws the track symmetrically around it), unlike the text panels
			// above whose anchor is a corner - so the hit box is centered on (x, y) too.
			OverlayElementType.TripProgressBar =>
				new SKRect(x - ProgressBarWidth / 2 * scale, y - ProgressBarHeight / 2 * scale,
					x + ProgressBarWidth / 2 * scale, y + ProgressBarHeight / 2 * scale),
			OverlayElementType.Compass => Circle(x, y, CompassRadius * scale),
			OverlayElementType.SunWidget => Circle(x, y, (SunRadius + 70) * scale),
			OverlayElementType.PitchGauge => Circle(x, y, PitchRadius * scale),
			OverlayElementType.SpeedGauge => Circle(x, y, SpeedRadius * scale),
			OverlayElementType.MapWidget => Circle(x, y, MapRadius * scale),
			OverlayElementType.GMeter => Circle(x, y, GMeterRadius * scale),
			_ => SKRect.Empty
		};
	}

	private static SKRect Circle(float cx, float cy, float radius)
	{
		return new SKRect(cx - radius, cy - radius, cx + radius, cy + radius);
	}
}
