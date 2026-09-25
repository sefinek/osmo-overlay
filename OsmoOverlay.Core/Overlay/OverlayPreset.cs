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
		var gMeterCy = m + OverlayElementBounds.GMeterRadius * scale;
		var sunCx = width - m - OverlayElementBounds.SunRadius * scale;
		var sunCy = m + OverlayElementBounds.GMeterRadius * 2 * scale + OverlayElementBounds.SunRadius * scale + 40 * scale;
		// Centered along the bottom edge, clear of Compass (bottom-left) and SpeedGauge (bottom-right)
		// at their default positions.
		var progressBarCx = width / 2f;
		var progressBarCy = height - m - OverlayElementBounds.ProgressBarHeight / 2 * scale;

		List<OverlayElement> elements =
		[
			new DateTimeTextElement { X = statsX, Y = dateY },
			new ElevationElement { X = statsX, Y = elevationY, Reference = ElevationReference.SeaLevel },
			new GradientElement { X = statsX, Y = gradientY },
			new DistanceElement { X = statsX, Y = distanceY },
			// Off by default - most users only need one clock; UTC is an opt-in extra for syncing
			// footage against UTC-timestamped external data (flight logs, other sensors, etc.).
			new UtcTimeTextElement { X = statsX, Y = utcY, Visible = false },
			// Off by default - niche/photographer-oriented metadata most riders/pilots don't need burned in.
			new CameraInfoElement { X = statsX, Y = cameraInfoY, Visible = false },
			// Off by default - a stopwatch duplicates what most editors already show in their timeline.
			new ElapsedTimeTextElement { X = statsX, Y = elapsedY, Visible = false },
			// Off by default - purely cosmetic branding of which camera shot the clip.
			new CameraModelTextElement { X = statsX, Y = cameraModelY, Visible = false },
			// Off by default (swapped with MapWidget below, which now takes the primary bottom-left
			// spot) - still positioned one slot over so it has somewhere sensible to land if re-enabled.
			new CompassElement { X = mapCx, Y = height - m - OverlayElementBounds.CompassRadius * scale, Visible = false },
			// Off by default (swapped with GMeter below, which now takes the primary top-right spot) -
			// still positioned one slot down so it has somewhere sensible to land if re-enabled.
			new SunWidgetElement { X = sunCx, Y = sunCy, Visible = false },
			new SpeedGaugeElement { X = speedCx, Y = speedCy },
			new PitchGaugeElement { X = speedCx, Y = pitchCy },
			// On by default (unlike Compass above) - takes Compass's old bottom-left spot. Tile source
			// (satellite by default) is a global setting - see OverlaySettings - not a per-element
			// field, so turning this on doesn't need any per-widget provider setup to already look right.
			new MapWidgetElement { X = compassCx, Y = height - m - OverlayElementBounds.MapRadius * scale },
			// On by default (swapped with SunWidget above) - takes its old top-right corner spot. Still
			// experimental: unlike PitchGauge's AccelY mapping, the X/Z axes plotted here were never
			// empirically verified against a controlled recording (see the comment above
			// OverlayRenderer.DrawGMeter) - kept as the default anyway per an explicit request to
			// prefer it over Sun/G-force.
			new GMeterElement { X = gMeterCx, Y = gMeterCy },
			// Fades in at 16s rather than being on screen the whole time - by then the ride's actually
			// under way, so "X% / Y km left" reads as a status update instead of a number sitting there
			// before there's anywhere meaningful left to go.
			new TripProgressBarElement
			{
				X = progressBarCx, Y = progressBarCy,
				AppearAtSeconds = 16.0, AnimationType = OverlayAnimationType.Fade
			}
		];

		// Each type's built-in instance gets a stable id (its own type name) rather than a random Guid -
		// keeps a freshly created preset's JSON deterministic, and matches WithElementIdsBackfilled's
		// migration for presets saved before Id existed (which likewise never had more than one instance
		// per type). Any additional instance of a type the GUI lets someone drag on later gets a real Guid.
		return new OverlayPreset(id, name, [.. elements.Select(e => e with { Id = e.Type.ToString() })]);
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

	/// <summary>
	///     Assigns a stable id (its type name) to any element left over from before OverlayElement.Id
	///     existed - a preset saved back then always had exactly one instance per type, so Type.ToString()
	///     can't collide with anything else already in Elements. No-op once every element already has one.
	/// </summary>
	public OverlayPreset WithElementIdsBackfilled()
	{
		return Elements.All(e => !string.IsNullOrEmpty(e.Id))
			? this
			: this with { Elements = [.. Elements.Select(e => string.IsNullOrEmpty(e.Id) ? e with { Id = e.Type.ToString() } : e)] };
	}
}
