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

		var compassCx = m + OverlayElementBounds.CompassRadius * scale;
		var mapCx = compassCx + (OverlayElementBounds.CompassRadius + 40 + OverlayElementBounds.MapRadius) * scale;

		List<OverlayElement> elements =
		[
			new(OverlayElementType.DateTimeText, statsX, dateY),
			new(OverlayElementType.Elevation, statsX, elevationY),
			new(OverlayElementType.Gradient, statsX, gradientY),
			new(OverlayElementType.Distance, statsX, distanceY),
			new(OverlayElementType.Compass, compassCx, height - m - OverlayElementBounds.CompassRadius * scale),
			new(OverlayElementType.SunWidget, width - m - OverlayElementBounds.SunRadius * scale,
				m + OverlayElementBounds.SunRadius * scale),
			new(OverlayElementType.SpeedGauge, speedCx, speedCy),
			new(OverlayElementType.PitchGauge, speedCx, pitchCy),
			// Off by default - unlike every other widget, this one needs network access to fetch map
			// tiles, so it shouldn't silently start making HTTP requests for someone who never asked
			// for a map.
			new(OverlayElementType.MapWidget, mapCx, height - m - OverlayElementBounds.MapRadius * scale, false)
		];

		return new OverlayPreset(id, name, elements);
	}

	/// <summary>
	///     Backfills any widget type missing from Elements - a preset saved before that widget type
	///     existed (e.g. every preset saved before MapWidget was added) simply has no entry for it, and
	///     toggling a checkbox for a type that isn't in the list is a silent no-op (OverlayPresetStore
	///     can't invent a position out of nowhere). Recomputes a fresh CreateDefault set and appends
	///     only the types this preset doesn't already have, as Visible=false so a newly-added widget
	///     doesn't suddenly appear on an already-arranged layout - same as any other widget, it's opt-in.
	/// </summary>
	public OverlayPreset WithMissingDefaultsFilled(int width, int height)
	{
		HashSet<OverlayElementType> existingTypes = Elements.Select(e => e.Type).ToHashSet();
		OverlayPreset fresh = CreateDefault(Id, Name, width, height);
		List<OverlayElement> missing = fresh.Elements
			.Where(e => !existingTypes.Contains(e.Type))
			.Select(e => e with { Visible = false })
			.ToList();

		return missing.Count == 0 ? this : this with { Elements = [.. Elements, .. missing] };
	}
}
