namespace OsmoOverlay.Core.Overlay;

/// <summary>Layers: top to bottom, null in a preset no layer was ever changed in - see OverlayLayers.Normalize.</summary>
public sealed record OverlayPreset(string Id, string Name, List<OverlayElement> Elements)
{
	public List<OverlayLayer>? Layers { get; init; }

	/// <summary>Set by OverlayPresetStore.Save when the preset is first saved and each time its content changes; null for the built-in Default.</summary>
	public DateTime? CreatedUtc { get; init; }

	public DateTime? UpdatedUtc { get; init; }

	/// <summary>
	///     The built-in default layout, in OverlayElementBounds' 4K reference space - the same space every widget's
	///     X/Y is in, so it fits any resolution and aspect ratio without being rebuilt for one.
	/// </summary>
	public static OverlayPreset CreateDefault(string id, string name)
	{
		const float width = OverlayElementBounds.ReferenceWidth;
		const float height = OverlayElementBounds.ReferenceHeight;
		const float m = OverlayElementBounds.Margin;

		// Widgets sit with their visible edge (OverlayRenderer.MeasureElement at 4K) on a guide - the margin or the first
		// third line - not their nominal radius: a gauge's band, a panel's ring and a text's cap height or side bearing
		// reach a little past (or stop short of) the anchor-based box. The small offsets below are those differences.
		float firstThirdY = m + (height - m * 2) / 3;

		float speedCx = width - m - (OverlayElementBounds.SpeedRadius - 2);
		float speedCy = height - m - (OverlayElementBounds.SpeedRadius - 2);
		float rollCy = speedCy - (OverlayElementBounds.SpeedRadius + OverlayElementBounds.TiltRadius + 56);
		// Beside the roll gauge, toward the middle.
		float pitchCx = speedCx - (OverlayElementBounds.TiltRadius * 2 + 40);

		float dateY = m + 34;
		float distanceY = firstThirdY - 99;
		float gradientY = distanceY - 220;
		float elevationY = gradientY - 220;
		// Appended after the always-visible stats column instead of spliced between DateTimeText and
		// Elevation - it's off by default, so it must not shift anything else's default position just
		// to make room for it.
		float utcY = distanceY + 240;
		// Same reasoning as UtcTimeText above - off by default, appended after it instead of shifting it.
		float cameraInfoY = utcY + 240;
		float elapsedY = cameraInfoY + 240;
		float cameraModelY = elapsedY + 110;
		float tripStatY = cameraModelY + 150;

		const float ring = OverlayElementBounds.RingHalfStroke;
		float mapCx = m + (OverlayElementBounds.MapRadius + ring);
		float mapCy = height - m - (OverlayElementBounds.MapRadius + ring);
		float compassCx = mapCx + (OverlayElementBounds.MapRadius + 40 + OverlayElementBounds.CompassRadius);
		float compassCy = height - m - (OverlayElementBounds.CompassRadius + ring);
		float gMeterCx = width - m - (OverlayElementBounds.GMeterRadius + 3);
		float gMeterCy = m + (OverlayElementBounds.GMeterRadius + 3);
		float sunCx = width - m - (OverlayElementBounds.SunRadius + 2);
		float sunCy = m + OverlayElementBounds.GMeterRadius * 2 + OverlayElementBounds.SunRadius + 40;
		// Centered along the bottom edge, clear of Compass (bottom-left) and SpeedGauge (bottom-right)
		// at their default positions.
		float progressBarCx = width / 2f;
		float progressBarCy = height - m - 16;
		// Centered above the progress bar, which is as wide.
		float chartX = progressBarCx - OverlayElementBounds.ChartWidth / 2;
		float chartY = progressBarCy - (OverlayElementBounds.ProgressBarHeight / 2 + 60 + OverlayElementBounds.ChartTop + OverlayElementBounds.ChartHeight);

		List<OverlayElement> elements =
		[
			new DateTimeTextElement { X = m, Y = dateY },
			// A stat's label starts a pixel in ("ELEVATION") or out ("DISTANCE") of its anchor.
			new ElevationElement { X = m - 1, Y = elevationY },
			new GradientElement { X = m, Y = gradientY },
			new DistanceElement { X = m + 1, Y = distanceY },
			// The ones below are off by default; each sits in its own slot so enabling it moves nothing.
			new UtcTimeTextElement { X = m, Y = utcY, Visible = false },
			new CameraInfoElement { X = m, Y = cameraInfoY, Visible = false },
			new ElapsedTimeTextElement { X = m, Y = elapsedY, Visible = false },
			new CameraModelTextElement { X = m, Y = cameraModelY, Visible = false },
			// MapWidget takes the bottom-left spot, so this sits one slot over.
			new CompassElement { X = compassCx, Y = compassCy, Visible = false },
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
			new TripStatElement { X = m - 1, Y = tripStatY, Visible = false },
			new TextElement { X = width / 2f, Y = dateY, Visible = false },
			new ImageElement { X = width / 2f, Y = m, Visible = false }
		];

		// Each type's built-in instance gets a stable id (its own type name) rather than a random Guid, so a
		// freshly created preset's JSON is deterministic. Any additional instance of a type the GUI lets
		// someone drag on later gets a real Guid.
		return new OverlayPreset(id, name, [.. elements.Select(e => e with { Id = e.Type.ToString() })]);
	}
}
