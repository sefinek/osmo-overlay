using OsmoOverlay.Core.Mapping;

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
		// Appended after the always-visible stats column instead of spliced between DateTimeText and
		// Elevation - it's off by default, so it must not shift anything else's default position just
		// to make room for it.
		var utcY = distanceY + 240 * scale;
		// Same reasoning as UtcTimeText above - off by default, appended after it instead of shifting it.
		var cameraInfoY = utcY + 240 * scale;
		var elapsedY = cameraInfoY + 240 * scale;
		var cameraModelY = elapsedY + 110 * scale;

		var compassCx = m + OverlayElementBounds.CompassRadius * scale;
		var mapCx = compassCx + (OverlayElementBounds.CompassRadius + 40 + OverlayElementBounds.MapRadius) * scale;
		var gMeterCx = width - m - OverlayElementBounds.GMeterRadius * scale;
		var gMeterCy = m + OverlayElementBounds.SunRadius * 2 * scale + OverlayElementBounds.GMeterRadius * scale + 40 * scale;

		List<OverlayElement> elements =
		[
			new(OverlayElementType.DateTimeText, statsX, dateY),
			new(OverlayElementType.Elevation, statsX, elevationY),
			new(OverlayElementType.Gradient, statsX, gradientY),
			new(OverlayElementType.Distance, statsX, distanceY),
			// Off by default - most users only need one clock; UTC is an opt-in extra for syncing
			// footage against UTC-timestamped external data (flight logs, other sensors, etc.).
			new(OverlayElementType.UtcTimeText, statsX, utcY, false),
			// Off by default - niche/photographer-oriented metadata most riders/pilots don't need burned in.
			new(OverlayElementType.CameraInfo, statsX, cameraInfoY, false),
			// Off by default - a stopwatch duplicates what most editors already show in their timeline.
			new(OverlayElementType.ElapsedTimeText, statsX, elapsedY, false),
			// Off by default - purely cosmetic branding of which camera shot the clip.
			new(OverlayElementType.CameraModelText, statsX, cameraModelY, false),
			new(OverlayElementType.Compass, compassCx, height - m - OverlayElementBounds.CompassRadius * scale),
			new(OverlayElementType.SunWidget, width - m - OverlayElementBounds.SunRadius * scale,
				m + OverlayElementBounds.SunRadius * scale),
			new(OverlayElementType.SpeedGauge, speedCx, speedCy),
			new(OverlayElementType.PitchGauge, speedCx, pitchCy),
			// Off by default - unlike every other widget, this one needs network access to fetch map
			// tiles, so it shouldn't silently start making HTTP requests for someone who never asked
			// for a map. Defaults to satellite imagery (rather than OpenStreetMapUrlTemplate's street
			// map) since it reads better at a glance alongside the rest of the HUD.
			new(OverlayElementType.MapWidget, mapCx, height - m - OverlayElementBounds.MapRadius * scale, false,
				MapTileUrlTemplate: MapTileFetcher.SatelliteUrlTemplate, MapAttribution: MapTileFetcher.SatelliteAttribution),
			// Off by default - experimental: unlike PitchGauge's AccelY mapping, the X/Z axes plotted
			// here were never empirically verified against a controlled recording (see the comment
			// above OverlayRenderer.DrawGMeter), so it shouldn't suddenly appear as "real" data.
			new(OverlayElementType.GMeter, gMeterCx, gMeterCy, false)
		];

		return new OverlayPreset(id, name, elements);
	}

	/// <summary>
	///     Backfills any widget type missing from Elements - a preset saved before that type existed
	///     (e.g. before MapWidget) has no entry for it, and toggling its checkbox would be a silent
	///     no-op. Appends only the missing types from a fresh CreateDefault, as Visible=false so they
	///     don't suddenly appear on an already-arranged layout.
	/// </summary>
	public OverlayPreset WithMissingDefaultsFilled(int width, int height)
	{
		HashSet<OverlayElementType> existingTypes = [.. Elements.Select(e => e.Type)];
		OverlayPreset fresh = CreateDefault(Id, Name, width, height);
		List<OverlayElement> missing =
		[
			.. fresh.Elements
				.Where(e => !existingTypes.Contains(e.Type))
				.Select(e => e with { Visible = false })
		];

		return missing.Count == 0 ? this : this with { Elements = [.. Elements, .. missing] };
	}
}
