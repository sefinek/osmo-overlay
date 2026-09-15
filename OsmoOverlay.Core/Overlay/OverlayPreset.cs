namespace OsmoOverlay.Core.Overlay;

public sealed record OverlayPreset(string Id, string Name, List<OverlayElement> Elements)
{
	/// <summary>
	///     Computes the same anchors OverlayRenderer used to hardcode, so the default look doesn't
	///     shift when a fresh preset is created for a new video resolution. All spacing is expressed
	///     at OverlayElementBounds' 4K reference and scaled down for smaller frames, so elements don't
	///     end up overlapping on e.g. 1080p footage.
	/// </summary>
	public static OverlayPreset CreateDefault(string id, string name, int width, int height)
	{
		var scale = OverlayElementBounds.GetScale(width, height);
		var m = OverlayElementBounds.Margin * scale;

		var speedCx = width - m - OverlayElementBounds.SpeedRadius * scale;
		var speedCy = height - m - OverlayElementBounds.SpeedRadius * scale;
		var pitchCy = speedCy - (OverlayElementBounds.SpeedRadius + OverlayElementBounds.PitchRadius + 40) * scale;

		var statsX = m;
		var dateY = m + 40 * scale;
		var elevationY = dateY + 110 * scale;
		var gradientY = elevationY + 240 * scale;
		var distanceY = gradientY + 240 * scale;

		List<OverlayElement> elements =
		[
			new(OverlayElementType.DateTimeText, statsX, dateY),
			new(OverlayElementType.Elevation, statsX, elevationY),
			new(OverlayElementType.Gradient, statsX, gradientY),
			new(OverlayElementType.Distance, statsX, distanceY),
			new(OverlayElementType.Compass, m + OverlayElementBounds.CompassRadius * scale,
				height - m - OverlayElementBounds.CompassRadius * scale),
			new(OverlayElementType.SunWidget, width - m - OverlayElementBounds.SunRadius * scale,
				m + OverlayElementBounds.SunRadius * scale),
			new(OverlayElementType.SpeedGauge, speedCx, speedCy),
			new(OverlayElementType.PitchGauge, speedCx, pitchCy)
		];

		return new OverlayPreset(id, name, elements);
	}
}
