namespace OsmoOverlay.Core.Overlay;

/// <summary>Layers: top to bottom, null in a preset no layer was ever changed in - see OverlayLayers.Normalize.</summary>
public sealed record OverlayPreset(string Id, string Name, List<OverlayElement> Elements)
{
	public List<OverlayLayer>? Layers { get; init; }

	/// <summary>Set by OverlayPresetStore.Save when the preset is first saved and each time its content changes; null for the built-in Default.</summary>
	public DateTime? CreatedUtc { get; init; }

	public DateTime? UpdatedUtc { get; init; }

	/// <summary>
	///     The built-in default layout for a video resolution. All spacing is expressed
	///     at OverlayElementBounds' 4K reference and scaled down for smaller frames, so elements don't
	///     end up overlapping on e.g. 1080p footage.
	/// </summary>
	public static OverlayPreset CreateDefault(string id, string name, int width, int height)
	{
		float scale = OverlayElementBounds.GetScale(width, height);
		float m = OverlayElementBounds.Margin * scale;

		// Widgets sit with their visible edge on the margin, not their nominal radius: a gauge's band, a panel's ring and
		// a text's cap height reach a little past (or stop short of) the anchor-based box.
		float speedCx = width - m - (OverlayElementBounds.SpeedRadius + 10) * scale;
		float speedCy = height - m - (OverlayElementBounds.SpeedRadius - 2) * scale;
		float rollCy = speedCy - (OverlayElementBounds.SpeedRadius + OverlayElementBounds.TiltRadius + 56) * scale;
		// Beside the roll gauge, toward the middle.
		float pitchCx = speedCx - (OverlayElementBounds.TiltRadius * 2 + 40) * scale;

		float statsX = m;
		float dateY = m + 34 * scale;
		float elevationY = dateY + 100 * scale;
		float gradientY = elevationY + 220 * scale;
		float distanceY = gradientY + 220 * scale;
		// Appended after the always-visible stats column instead of spliced between DateTimeText and
		// Elevation - it's off by default, so it must not shift anything else's default position just
		// to make room for it.
		float utcY = distanceY + 240 * scale;
		// Same reasoning as UtcTimeText above - off by default, appended after it instead of shifting it.
		float cameraInfoY = utcY + 240 * scale;
		float elapsedY = cameraInfoY + 240 * scale;
		float cameraModelY = elapsedY + 110 * scale;
		float tripStatY = cameraModelY + 150 * scale;

		float mapCx = m + (OverlayElementBounds.MapRadius + 2) * scale;
		float mapCy = height - m - (OverlayElementBounds.MapRadius + 2) * scale;
		float compassCx = mapCx + (OverlayElementBounds.MapRadius + 40 + OverlayElementBounds.CompassRadius) * scale;
		float gMeterCx = width - m - (OverlayElementBounds.GMeterRadius + 3) * scale;
		float gMeterCy = m + (OverlayElementBounds.GMeterRadius + 3) * scale;
		float sunCx = width - m - OverlayElementBounds.SunRadius * scale;
		float sunCy = m + OverlayElementBounds.GMeterRadius * 2 * scale + OverlayElementBounds.SunRadius * scale + 40 * scale;
		// Centered along the bottom edge, clear of Compass (bottom-left) and SpeedGauge (bottom-right)
		// at their default positions.
		float progressBarCx = width / 2f;
		float progressBarCy = height - m - 16 * scale;
		// Centered above the progress bar, which is as wide.
		float chartX = progressBarCx - OverlayElementBounds.ChartWidth / 2 * scale;
		float chartY = progressBarCy - (OverlayElementBounds.ProgressBarHeight / 2 + 60 + OverlayElementBounds.ChartTop +
		                                OverlayElementBounds.ChartHeight) * scale;

		List<OverlayElement> elements =
		[
			new DateTimeTextElement { X = statsX, Y = dateY },
			new ElevationElement { X = statsX, Y = elevationY },
			new GradientElement { X = statsX, Y = gradientY },
			new DistanceElement { X = statsX, Y = distanceY },
			// The ones below are off by default; each sits in its own slot so enabling it moves nothing.
			new UtcTimeTextElement { X = statsX, Y = utcY, Visible = false },
			new CameraInfoElement { X = statsX, Y = cameraInfoY, Visible = false },
			new ElapsedTimeTextElement { X = statsX, Y = elapsedY, Visible = false },
			new CameraModelTextElement { X = statsX, Y = cameraModelY, Visible = false },
			// MapWidget takes the bottom-left spot, so this sits one slot over.
			new CompassElement { X = compassCx, Y = height - m - OverlayElementBounds.CompassRadius * scale, Visible = false },
			// GMeter takes the top-right spot, so this sits one slot down.
			new SunWidgetElement { X = sunCx, Y = sunCy, Visible = false },
			new SpeedGaugeElement { X = speedCx, Y = speedCy },
			new RollGaugeElement { X = speedCx, Y = rollCy },
			// Tile source (satellite by default) is a global setting - see OverlaySettings - not a per-element
			// field, so turning this on doesn't need any per-widget provider setup to already look right.
			new MapWidgetElement { X = mapCx, Y = mapCy },
			new GMeterElement { X = gMeterCx, Y = gMeterCy },
			// Fades in at 16s rather than being on screen the whole time - by then the ride's actually
			// under way, so "X% / Y km left" reads as a status update instead of a number sitting there
			// before there's anywhere meaningful left to go.
			new TripProgressBarElement
			{
				X = progressBarCx, Y = progressBarCy,
				AppearAtSeconds = 16.0, AnimationType = OverlayAnimationType.Fade
			},
			// The rest are off by default - added after the layout above, so they mustn't move or cover any of it.
			new PitchGaugeElement { X = pitchCx, Y = rollCy, Visible = false },
			new ProfileChartElement { X = chartX, Y = chartY, Visible = false },
			new TripStatElement { X = statsX, Y = tripStatY, Visible = false },
			new TextElement { X = width / 2f, Y = m + 40 * scale, Visible = false },
			new ImageElement { X = width / 2f, Y = m, Visible = false }
		];

		// Each type's built-in instance gets a stable id (its own type name) rather than a random Guid, so a
		// freshly created preset's JSON is deterministic. Any additional instance of a type the GUI lets
		// someone drag on later gets a real Guid.
		return new OverlayPreset(id, name, [.. elements.Select(e => e with { Id = e.Type.ToString() })]);
	}
}
