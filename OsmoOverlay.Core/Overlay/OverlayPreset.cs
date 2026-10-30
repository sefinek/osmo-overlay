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
	// The default watermark's seconds on screen, and its baseline under the progress bar's center - where the bar is drawn.
	private const double WatermarkSeconds = 6;
	private const float WatermarkBelowBar = 14;

	/// <summary>
	///     The layout for a DJI drone's footage: the default one without what a drone has no data for (the camera's roll and G-force, a
	///     gradient, a heading, the sun, the date) or doesn't need (the GPS alert - the flight alerts are the drone's - the climb rate,
	///     the flight mode, the progress bar). The battery is in the top left corner, the radio link and the GPS are a row in the top right, the flight alerts sit at the top center,
	///     the distance and the trip's figures are a column down the left under the battery (the height is the profile's, below), the height profile, the speed
	///     gauge and the map are where the default has them. The height profile is on the bottom guide, where the progress bar would be, and comes in after the watermark.
	/// </summary>
	public static OverlayPreset CreateDrone(string id, string name)
	{
		OverlayPreset basis = CreateDefault(id, name);
		OverlayElement date = basis.Elements.First(e => e is DateTimeTextElement);
		const float width = OverlayElementBounds.ReferenceWidth;
		const float m = OverlayElementBounds.Margin;
		float top = date.Y;
		// On the bottom guide, where the default keeps the progress bar (this layout leaves the bar out); the profile only comes in once the watermark has gone.
		float chartY = OverlayElementBounds.ReferenceHeight - m - (OverlayElementBounds.ChartTop + OverlayElementBounds.ChartHeight + 1);
		float gpsX = width - m - DroneGpsWidth, signalX = gpsX - DroneTopGap - DroneSignalWidth;
		float sticksX = width - m - (OverlayElementBounds.StickPadSize * 2 + OverlayElementBounds.StickPadGap), dialX = width - m - OverlayElementBounds.DroneDialRadius;

		return basis with
		{
			Elements =
			[
				.. basis.Elements.Select(e => e switch
				{
					DateTimeTextElement or ElevationElement or GradientElement or RollGaugeElement or GMeterElement or CompassElement or SunWidgetElement or GpsAlertElement
						or TripProgressBarElement => e with { Visible = false },
					ProfileChartElement chart => chart with { Y = chartY, Visible = true, Series = ProfileSeries.Height, Axis = ProfileAxis.Time, AutoHeight = true },
					MapWidgetElement map => map with { MapZoom = 17, MapDynamicZoom = true, MapDynamicZoomMaxFactor = 3, ShowEsriLabels = false },
					DroneBatteryElement => e with { X = m, Y = top, Visible = true },
					SignalElement => e with { X = signalX, Y = top, Visible = true },
					SatellitesElement => e with { X = gpsX, Y = top, Visible = true },
					FlightAlertsElement alerts => alerts with { Visible = true, ShowSeconds = 5, MaxLines = 2 },
					RcSticksElement => e with { X = sticksX, Y = top + DroneControlsTop, Visible = true },
					AttitudeIndicatorElement => e with { X = dialX, Y = top + DroneControlsTop + OverlayElementBounds.StickPadSize + DroneControlsGap + OverlayElementBounds.DroneDialRadius, Visible = true },
					Orientation3DElement => e with
					{
						X = dialX, Y = top + DroneControlsTop + OverlayElementBounds.StickPadSize + 2 * DroneControlsGap + OverlayElementBounds.DroneDialRadius * 3, Visible = true
					},
					HeightElement or HomeDistanceElement => e with { Visible = false },
					DistanceElement => e with { X = m, Y = top + DroneColumnStep },
					TripStatElement => e with { X = m, Y = top + 2 * DroneColumnStep, Visible = true },
					_ => e
				})
			]
		};
	}

	/// <summary>
	///     An FPV goggles' OSD, not the DJI layout: everything that is shown is white, text on a see-through ground. The aircraft's own state
	///     is along the edges - battery, radio link and GPS in a row top left, the flight timer and the height, distance and climb rate in a
	///     column top right, the flight mode top center over the alerts (three, 3 s, fading in 0.12 s and sliding in) - and the instruments
	///     are along the bottom: the remote's sticks left, the attitude indicator and the 3D orientation in the middle, the speed gauge right.
	///     The map is bottom left (with its credit), the sticks next to it. The date and time sit under the flight timer. No profile, sun or camera gauges.
	/// </summary>
	public static OverlayPreset CreateFpv(string id, string name)
	{
		OverlayPreset basis = CreateDefault(id, name);
		const float width = OverlayElementBounds.ReferenceWidth, height = OverlayElementBounds.ReferenceHeight, m = OverlayElementBounds.Margin;
		float top = basis.Elements.First(e => e is DateTimeTextElement).Y;
		float signalX = m + FpvBatteryWidth + DroneTopGap, gpsX = signalX + FpvSignalWidth + DroneTopGap, leftY = top + FpvLeftColumnTop;
		float bottom = height - m;
		// A widget whose text ends on the right margin, lined up to the right: the box is the widget's own, so its left edge follows its width.
		OverlayElement OnRightEdge(StyledOverlayElement e, float y)
		{
			StyledOverlayElement aligned = e with { Y = y, Visible = true, TextAlign = TextAlign.Right };
			return aligned with { X = width - m - OverlayElementBounds.TextBoxRight(aligned) };
		}

		OverlayElement AtTopCenter(StyledOverlayElement e)
		{
			StyledOverlayElement centered = e with { Y = top, Visible = true, TextAlign = TextAlign.Center };
			return centered with { X = width / 2f - OverlayElementBounds.TextBoxRight(centered) / 2 };
		}

		const float dial = OverlayElementBounds.DroneDialRadius;
		float sticksX = basis.Elements.First(e => e is MapWidgetElement).X + OverlayElementBounds.MapRadius + FpvMapToSticks;
		const string white = "#FFFFFF";

		return basis with
		{
			Elements =
			[
				.. basis.Elements.Select(e =>
				{
					OverlayElement placed = e switch
					{
						DroneBatteryElement battery => battery with { X = m, Y = top, Visible = true, ShowTemperature = true, ShowCurrent = true, ShowCells = true },
						SignalElement => e with { X = signalX, Y = top, Visible = true },
						SatellitesElement => e with { X = gpsX, Y = top, Visible = true },
						FlightModeElement f => AtTopCenter(f),
						FlightAlertsElement alerts => alerts with { X = width / 2f, Y = top + FpvAlertsBelowTop, Visible = true, ShowSeconds = 3, MaxLines = 3, FadeSeconds = 0.12, SlideIn = true },
						ElapsedTimeTextElement t => OnRightEdge(t, top),
						DateTimeTextElement d => OnRightEdge(d, top + FpvDateBelowTime),
						HeightElement h => OnRightEdge(h, top + FpvRightColumnTop),
						HomeDistanceElement d => OnRightEdge(d, top + FpvRightColumnTop + FpvRowStep),
						VerticalSpeedElement v => OnRightEdge(v, top + FpvRightColumnTop + 2 * FpvRowStep),
						DistanceElement => e with { X = m, Y = leftY, Visible = true },
						TripStatElement => e with { X = m, Y = leftY + FpvRowStep, Visible = true },
						RcSticksElement => e with { X = sticksX, Y = bottom - OverlayElementBounds.StickPadSize, Visible = true },
						AttitudeIndicatorElement => e with { X = width / 2f - dial - FpvDialGap / 2, Y = bottom - dial, Visible = true },
						Orientation3DElement => e with { X = width / 2f + dial + FpvDialGap / 2, Y = bottom - dial, Visible = true },
						MapWidgetElement map => map with { MapZoom = 17, MapDynamicZoom = true, MapDynamicZoomMaxFactor = 3, ShowEsriLabels = false },
						SpeedGaugeElement or MapAttributionElement => e,
						_ => e with { Visible = false }
					};
					return placed switch
					{
						LabeledStatElement stat => stat with { AccentColor = white },
						_ => placed
					};
				}),
				new TripStatElement { X = m, Y = leftY + 2 * FpvRowStep, Stat = TripStatKind.AverageSpeed, AccentColor = white, Id = "TripStat.AverageSpeed" },
				new TripStatElement { X = m, Y = leftY + 3 * FpvRowStep, Stat = TripStatKind.MaxHeight, AccentColor = white, Id = "TripStat.MaxHeight" },
				new TripStatElement { X = m, Y = leftY + 4 * FpvRowStep, Stat = TripStatKind.MaxHomeDistance, AccentColor = white, Id = "TripStat.MaxHomeDistance" }
			]
		};
	}

	// The widest the GPS and the signal widgets get at 4K (measured), the gap between them, and the rhythm of the left column (the battery's own height).
	private const float DroneGpsWidth = 240;
	private const float DroneSignalWidth = 280;
	private const float DroneTopGap = 60;
	private const float FpvDateBelowTime = 64, FpvBatteryWidth = 502, FpvSignalWidth = 242, FpvLeftColumnTop = 340, FpvRightColumnTop = 150, FpvRowStep = 160, FpvAlertsBelowTop = 170, FpvDialGap = 60, FpvMapToSticks = 110;
	private const float DroneColumnStep = 220;

	// The sticks and the two dials stacked down the right edge under the top row: where the first starts, and the gap between them.
	private const float DroneControlsTop = 200;
	private const float DroneControlsGap = 40;

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
		// In the gap between UtcTimeText and CameraInfo - below CameraModelText it covered the map.
		float tripStatY = utcY + 110;

		const float ring = OverlayElementBounds.RingHalfStroke;
		float mapCx = m + (OverlayElementBounds.MapRadius + ring);
		float mapCy = height - m - (OverlayElementBounds.MapRadius + ring);
		float compassCx = mapCx + (OverlayElementBounds.MapRadius + 40 + OverlayElementBounds.CompassRadius);
		float compassCy = height - m - (OverlayElementBounds.CompassRadius + ring);
		// The frame's own bottom-left corner, closer to the edge than the margin, under the map (its text's baseline).
		const float mapCreditInset = 12;
		float mapCreditY = height - mapCreditInset - 6;
		float gMeterCx = width - m - (OverlayElementBounds.GMeterRadius + 3);
		float gMeterCy = m + (OverlayElementBounds.GMeterRadius + 3);
		float sunCx = width - m - (OverlayElementBounds.SunRadius + 2);
		// Below the G-meter's readout, which hangs 62 px under its ring at 4K - level with the ring, it covered the readout.
		float sunCy = m + OverlayElementBounds.GMeterRadius * 2 + OverlayElementBounds.SunRadius + 40 + 62;
		// Centered along the bottom edge, clear of Compass (bottom-left) and SpeedGauge (bottom-right)
		// at their default positions.
		float progressBarCx = width / 2f;
		float progressBarCy = height - m - 16;
		// In the progress bar's place, before it: the bar takes over the bottom center once the watermark has gone.
		float watermarkY = progressBarCy + WatermarkBelowBar;
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
			new MapWidgetElement { X = mapCx, Y = mapCy },
			new MapAttributionElement { X = mapCreditInset, Y = mapCreditY, Scale = 0.8f },
			new GMeterElement { X = gMeterCx, Y = gMeterCy },
			// The opening of the bottom center, one layer: the watermark for the first seconds, fading out, and the progress bar
			// fading in where it was as soon as it's gone (both counted from the route overview card's end when it's on).
			new WatermarkElement
			{
				X = width / 2f, Y = watermarkY, DisappearAtSeconds = WatermarkSeconds, OutAnimationType = OverlayAnimationType.Fade
			},
			new TripProgressBarElement
			{
				X = progressBarCx, Y = progressBarCy, AnimationType = OverlayAnimationType.Fade, AppearAtSeconds = WatermarkSeconds,
				ShowFromSpeedKmh = null, LayerId = nameof(OverlayElementType.Watermark)
			},
			// Top right, under the G-meter - placed by hand, so it's a spot in the 4K reference space rather than derived from a margin.
			new GpsAlertElement { X = 3451.7102f, Y = 426.52588f },
			// The rest are off by default - added after the layout above, so they mustn't move or cover any of it.
			new PitchGaugeElement { X = pitchCx, Y = rollCy, Visible = false },
			// Off by default; turned on it comes in where the watermark's credit has gone, as the progress bar does.
				new ProfileChartElement
				{
					X = chartX, Y = chartY, Visible = false, AnimationType = OverlayAnimationType.Fade, AppearAtSeconds = WatermarkSeconds, LayerId = nameof(OverlayElementType.Watermark)
				},
			new TripStatElement { X = m - 1, Y = tripStatY, Visible = false },
			new TextElement { X = width / 2f, Y = dateY, Visible = false },
			new ImageElement { X = width / 2f, Y = m, Visible = false },
			new AmbientTemperatureElement { X = 1200, Y = 500, Visible = false },
			new ImuTemperatureElement { X = 2200, Y = 500, Visible = false },
			new DeviceDistanceElement { X = 1200, Y = 800, Visible = false },
			new DeviceBatteryElement { X = 2200, Y = 800, Visible = false },
			new AudioLevelElement { X = 1200, Y = 1100, Visible = false },
			new WindProcessingElement { X = 2200, Y = 1100, Visible = false },
			new ImageLumaElement { X = 1200, Y = 1400, Visible = false },
			new HeightElement { X = 1200, Y = 1700, Visible = false },
			new HomeDistanceElement { X = 2900, Y = 800, Visible = false },
			new VerticalSpeedElement { X = 2200, Y = 1400, Visible = false },
			new SatellitesElement { X = 2900, Y = 1100, Visible = false },
			new DroneBatteryElement { X = 2740, Y = 1400, Visible = false },
			new FlightAlertsElement { X = width / 2f, Y = m, Visible = false },
			new SignalElement { X = 1200, Y = 2000, Visible = false },
			new FlightModeElement { X = 2900, Y = 1700, Visible = false },
			new RcSticksElement { X = 2900, Y = 1960, Visible = false },
			new AttitudeIndicatorElement { X = 2430, Y = 1880, Visible = false },
			new Orientation3DElement { X = 2760, Y = 1880, Visible = false }
		];

		// Each type's built-in instance gets a stable id (its own type name) rather than a random Guid, so a
		// freshly created preset's JSON is deterministic. Any additional instance of a type the GUI lets
		// someone drag on later gets a real Guid.
		return new OverlayPreset(id, name, [.. elements.Select(e => e with { Id = e.Type.ToString() })]);
	}
}
