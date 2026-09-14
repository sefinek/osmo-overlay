namespace OsmoOverlay.Core.Overlay;

public sealed record OverlayPreset(string Id, string Name, List<OverlayElement> Elements)
{
	/// <summary>
	///     Computes the same anchors OverlayRenderer used to hardcode, so the default look doesn't
	///     shift when a fresh preset is created for a new video resolution.
	/// </summary>
	public static OverlayPreset CreateDefault(string id, string name, int width, int height)
	{
		const float m = OverlayElementBounds.Margin;

		var speedCx = width - m - OverlayElementBounds.SpeedRadius;
		var speedCy = height - m - OverlayElementBounds.SpeedRadius;
		var pitchCy = speedCy - OverlayElementBounds.SpeedRadius - OverlayElementBounds.PitchRadius - 40;

		List<OverlayElement> elements =
		[
			new(OverlayElementType.StatsBlock, m, m + 40),
			new(OverlayElementType.Compass, m + OverlayElementBounds.CompassRadius,
				height - m - OverlayElementBounds.CompassRadius),
			new(OverlayElementType.SunWidget, width - m - OverlayElementBounds.SunRadius,
				m + OverlayElementBounds.SunRadius),
			new(OverlayElementType.SpeedGauge, speedCx, speedCy),
			new(OverlayElementType.PitchGauge, speedCx, pitchCy)
		];

		return new OverlayPreset(id, name, elements);
	}
}
