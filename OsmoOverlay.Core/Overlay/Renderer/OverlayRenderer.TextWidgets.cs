using OsmoOverlay.Core.Localization;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Telemetry;
using SkiaSharp;

namespace OsmoOverlay.Core.Overlay;

/// <summary>Text/stat widgets: DateTimeText/UtcTimeText, ElapsedTimeText, CameraModelText, Text, Elevation/Gradient/Distance, TripStat and CameraInfo.</summary>
public sealed partial class OverlayRenderer
{
	private const double MetersToFeet = 3.28084;
	private const double MilesInMeters = 1609.344;

	public const string TextDefault = "Your text";

	// (Locale, DateFormat) pairs already reported as invalid - a bad one would otherwise log once per frame.
	private readonly HashSet<(string?, string?)> _reportedTimeFormats = [];

	/// <summary>Shared by DateTimeText (local time) and UtcTimeText (raw UTC, no conversion) - both use the same DateFormat/Locale fields.</summary>
	private void DrawTimeText(SKCanvas canvas, DerivedFrame frame, TimeTextElementBase element, bool toLocal)
	{
		// Falls back to the container's own recording-start tag + time into the recording when this frame has no
		// real GPS timestamp (see SourceInfo.ContainerCreationTimeUtc) - approximate (camera clock, not
		// a GPS-synced one), but still far more useful than "--" for a file that never had a fix at
		// all. The GUI signals this with a warning icon next to the widget (see
		// MainWindow.UsesTimeFallback) rather than marking it inside the burned-in video text.
		DateTime? utc = frame.Raw.GpsTimestamp ??
		                _containerRecordingStartUtc?.AddSeconds(frame.Raw.RecordingTimeSeconds);

		string dateText;
		if (utc is not { } resolvedUtc)
		{
			dateText = "--";
		}
		else
		{
			DateTime shown = toLocal ? resolvedUtc.ToLocalFromUtc() : resolvedUtc;
			// A hand-edited/shared preset can carry an invalid Locale or DateFormat string - fail soft
			// (fall back to the default) instead of throwing out of Render and aborting the whole export,
			// same "one bad user-editable field" policy as the map widget's placeholder fallback.
			if (!OverlayTimeFormatting.TryFormat(shown, element.DateFormat, element.Locale, out dateText) &&
			    _reportedTimeFormats.Add((element.Locale, element.DateFormat)))
				AppLogger.Warn($"Time widget: invalid Locale/DateFormat ('{element.Locale}' / '{element.DateFormat}') - using default");

			if (!toLocal) dateText += "  UTC";
		}

		DrawOutlined(canvas, dateText, 0, 0, TextFont(element, OverlayElementBounds.DateFontSize), TextColorOf(element),
			outlineColor: OutlineColorOf(element), outlineWidthScale: element.OutlineWidth);
	}

	/// <summary>Time since the recording's own frame 0 (SampleTimeSeconds), not a wall-clock reading - a stopwatch, distinct from DateTimeText/UtcTimeText.</summary>
	private void DrawElapsedTime(SKCanvas canvas, DerivedFrame frame, ElapsedTimeTextElement element)
	{
		string text = OverlayTimeFormatting.FormatElapsed(frame.Raw.SampleTimeSeconds);
		SKFont timeFont = TextFont(element, OverlayElementBounds.DateFontSize);
		SKFont captionFont = TextFont(element, OverlayElementBounds.LabelFontSize);
		string? caption = string.IsNullOrWhiteSpace(element.Label) ? null : element.Label.ToUpperInvariant();
		float box = element.TextAlign == TextAlign.Left ? 0 : Math.Max(OverlayElementBounds.TextBoxRight(element), Math.Max(timeFont.MeasureText(text), caption is null ? 0 : captionFont.MeasureText(caption)));
		DrawOutlined(canvas, text, OverlayElementBounds.AlignedX(element.TextAlign, box, timeFont.MeasureText(text)), 0, timeFont, TextColorOf(element),
			outlineColor: OutlineColorOf(element), outlineWidthScale: element.OutlineWidth);

		// Above the time, so a caption added later doesn't move the time itself.
		if (caption is not null)
		{
			DrawOutlined(canvas, caption, OverlayElementBounds.AlignedX(element.TextAlign, box, captionFont.MeasureText(caption)), -OverlayElementBounds.CaptionAboveOffset,
				TextFont(element, OverlayElementBounds.LabelFontSize), TextColorOf(element),
				outlineColor: OutlineColorOf(element), outlineWidthScale: element.OutlineWidth);
		}
	}

	/// <summary>Static per-recording text (FileSummary.CameraModel) - same value on every frame, so unlike the other widgets nothing here depends on `frame`.</summary>
	private void DrawCameraModel(SKCanvas canvas, CameraModelTextElement element)
	{
		DrawOutlined(canvas, _cameraModel ?? "--", 0, 0, TextFont(element, OverlayElementBounds.DateFontSize), TextColorOf(element),
			outlineColor: OutlineColorOf(element), outlineWidthScale: element.OutlineWidth);
	}

	/// <summary>Lines split on line breaks, each a DateFontSize line below the last - baseline of the first at (0, 0).</summary>
	private void DrawText(SKCanvas canvas, TextElement element)
	{
		SKFont font = TextFont(element, OverlayElementBounds.DateFontSize);
		SKColor color = TextColorOf(element);
		SKColor outline = OutlineColorOf(element);
		string[] lines = OverlayElementBounds.TextLines(element.Text);
		float box = lines.Max(l => font.MeasureText(l));
		for (int i = 0; i < lines.Length; i++)
			DrawOutlined(canvas, lines[i], OverlayElementBounds.AlignedX(element.TextAlign, box, font.MeasureText(lines[i])), i * font.Spacing, font, color, outlineColor: outline, outlineWidthScale: element.OutlineWidth);
	}

	private void DrawElevation(SKCanvas canvas, DerivedFrame frame, ElevationElement element)
	{
		double meters = element.Reference == ElevationReference.SeaLevel ? frame.Raw.AltitudeMeters : frame.Raw.AltitudeMeters - _startAltitude;
		(string value, string unit) = FormatAltitude(meters, element.Units, element.Decimals);
		DrawStat(canvas, element, element.Label ?? DefaultLabel(element), value, unit);
	}

	/// <summary>The value so far at `frame` (TripStats) - where it is in the recording found by its time.</summary>
	private void DrawTripStat(SKCanvas canvas, DerivedFrame frame, TripStatElement element)
	{
		int i = _allFrames.Count > 0 ? TelemetryProcessor.FindIndex(_allFrames, frame.Raw.SampleTimeSeconds) : -1;
		(string value, string unit) = i < 0
			? ("--", "")
			: element.Stat switch
			{
				TripStatKind.MaxSpeed => FormatSpeed(_tripStats.MaxSpeedKmh(i), element.Units, element.Decimals),
				TripStatKind.AverageSpeed => FormatSpeed(_tripStats.AverageSpeedKmh(i), element.Units, element.Decimals),
				TripStatKind.ElevationGain => FormatAltitude(_tripStats.ElevationGainMeters(i), element.Units, element.Decimals),
				TripStatKind.ElevationLoss => FormatAltitude(_tripStats.ElevationLossMeters(i), element.Units, element.Decimals),
				TripStatKind.MaxHeight => FormatAltitude(_tripStats.MaxHeightMeters(i), element.Units, element.Decimals),
				TripStatKind.MaxHomeDistance => FormatDistance(_tripStats.MaxHomeDistanceMeters(i), element.Units, element.Decimals),
				_ => (OverlayTimeFormatting.FormatElapsed(_tripStats.MovingSeconds(i)), "")
			};
		DrawStat(canvas, element, element.Label ?? DefaultLabel(element), value, unit);
	}

	/// <summary>A labeled widget's caption without a Label of its own, in the interface's language (drawn in capitals).</summary>
	public static string DefaultLabel(LabeledStatElement element)
	{
		return element switch
		{
			ElevationElement => CoreStrings.Overlay_Elevation,
			GradientElement => CoreStrings.Overlay_Gradient,
			DistanceElement => CoreStrings.Overlay_TotalDistance,
			AmbientTemperatureElement => CoreStrings.Overlay_AmbientTemperature,
			ImuTemperatureElement => CoreStrings.Overlay_ImuTemperature,
			DeviceDistanceElement => CoreStrings.Overlay_DeviceDistance,
			DeviceBatteryElement => CoreStrings.Overlay_DeviceBattery,
			AudioLevelElement => CoreStrings.Overlay_AudioLevel,
			WindProcessingElement => CoreStrings.Overlay_WindProcessing,
			ImageLumaElement => CoreStrings.Overlay_ImageLuma,
			HeightElement => CoreStrings.Overlay_Height,
			HomeDistanceElement => CoreStrings.Overlay_HomeDistance,
			VerticalSpeedElement => CoreStrings.Overlay_VerticalSpeed,
			SatellitesElement => CoreStrings.Overlay_Satellites,
			DroneBatteryElement => CoreStrings.Overlay_DroneBattery,
			SignalElement => CoreStrings.Overlay_Signal,
			FlightModeElement => CoreStrings.Overlay_FlightModeLabel,
			CameraInfoElement => CoreStrings.Overlay_Camera,
			TripStatElement stat => DefaultTripStatLabel(stat.Stat),
			ProfileChartElement chart => DefaultProfileLabel(chart.Series),
			_ => ""
		};
	}

	public static string DefaultTripStatLabel(TripStatKind stat)
	{
		return stat switch
		{
			TripStatKind.MaxSpeed => CoreStrings.Overlay_MaxSpeed,
			TripStatKind.AverageSpeed => CoreStrings.Overlay_AverageSpeed,
			TripStatKind.ElevationGain => CoreStrings.Overlay_ElevationGain,
			TripStatKind.ElevationLoss => CoreStrings.Overlay_ElevationLoss,
			TripStatKind.MaxHeight => CoreStrings.Overlay_MaxHeight,
			TripStatKind.MaxHomeDistance => CoreStrings.Overlay_MaxHomeDistance,
			_ => CoreStrings.Overlay_MovingTime
		};
	}

	/// <summary>Whole metres/feet - an altitude or a climb, where FormatDistance's decimals would only show GPS noise.</summary>
	private static (string Value, string Unit) FormatAltitude(double meters, UnitSystem units, int? decimals = null)
	{
		string format = Digits(decimals, "0");
		return units == UnitSystem.Imperial
			? (F(meters * MetersToFeet, format), "FT")
			: (F(meters, format), "M");
	}

	/// <summary>A number format with `decimals` digits after the point, or `fallback` when the widget has no choice made.</summary>
	private static string Digits(int? decimals, string fallback)
	{
		return decimals is { } digits ? (digits <= 0 ? "0" : "0." + new string('0', Math.Min(digits, 3))) : fallback;
	}

	private void DrawGradient(SKCanvas canvas, DerivedFrame frame, GradientElement element)
	{
		DrawStat(canvas, element, element.Label ?? DefaultLabel(element), F(frame.GradientPercent, Digits(element.Decimals, "0")), "%");
	}

	private void DrawDistance(SKCanvas canvas, DerivedFrame frame, DistanceElement element)
	{
		(string distanceValue, string distanceUnit) = FormatDistance(frame.CumulativeDistanceMeters, element.Units, element.Decimals);
		DrawStat(canvas, element, element.Label ?? DefaultLabel(element), distanceValue, distanceUnit);
	}

	private void DrawSensorStat(SKCanvas canvas, DerivedFrame frame, LabeledStatElement element)
	{
		(string value, string unit) = element switch
		{
			AmbientTemperatureElement t => FormatTemperature(frame.Raw.AmbientTemperatureCelsius, t.Units, t.Decimals),
			ImuTemperatureElement t => FormatTemperature(frame.Raw.ImuTemperatureCelsius, t.Units, t.Decimals),
			DeviceDistanceElement d => frame.Raw.DeviceDistanceMeters is { } meters && double.IsFinite(meters) ? FormatDistance(meters, d.Units, d.Decimals) : ("--", d.Units == UnitSystem.Imperial ? "FT" : "M"),
			HeightElement h => frame.Raw.HeightAboveHomeMeters is { } height && double.IsFinite(height) ? FormatAltitude(height, h.Units, h.Decimals) : ("--", h.Units == UnitSystem.Imperial ? "FT" : "M"),
			HomeDistanceElement d => frame.Raw.DistanceFromHomeMeters is { } away && double.IsFinite(away) ? FormatDistance(away, d.Units, d.Decimals) : ("--", d.Units == UnitSystem.Imperial ? "FT" : "M"),
			VerticalSpeedElement v => FormatVerticalSpeed(frame.Raw.VerticalSpeedMs, v.Units, v.Decimals),
			SatellitesElement => (frame.Raw.GpsSatellites is >= 0 and { } satellites ? F(satellites, "0") : "--", ""),
			DeviceBatteryElement => (frame.Raw.DeviceBatteryPercent is { } percent && double.IsFinite(percent) ? F(percent, "0") : "--", "%"),
			WindProcessingElement => (frame.Raw.WindProcessingLevel is >= 0 and <= 100 and { } wind ? F(wind, "0") : "--", "/100"),
			ImageLumaElement => (frame.Raw.ImageLuma is >= 0 and <= 255 and { } luma ? F(luma, "0") : "--", "/255"),
			_ => ("--", "")
		};
		DrawStat(canvas, element, element.Label ?? DefaultLabel(element), value, unit, StatValueColor(element, frame));
		if (element is SatellitesElement { ShowBars: true })
		{
			float left = TextFont(element, OverlayElementBounds.ValueFontSize).MeasureText(value) + OverlayElementBounds.SignalBarsGap;
			DrawSignalBars(canvas, element, left, OverlayElementBounds.StatValueBaselineY, frame.Raw.GpsLevel);
		}

		if (element is WindProcessingElement)
		{
			string state = frame.Raw.WindProcessingActive switch { true => CoreStrings.Overlay_Active, false => CoreStrings.Overlay_Inactive, _ => "--" };
			SKColor stateColor = frame.Raw.WindProcessingActive == true ? AccentColorOf(element) : TextColorOf(element).WithAlpha(170);
			DrawOutlined(canvas, state.ToUpperInvariant(), 0, 150, TextFont(element, OverlayElementBounds.SmallFontSize), stateColor,
				outlineColor: OutlineColorOf(element), outlineWidthScale: element.OutlineWidth);
		}
	}

	private static readonly SKColor ThresholdWarnDefault = SKColor.Parse("#FF9800");
	private static readonly SKColor ThresholdAlertDefault = SKColor.Parse("#F44336");

	/// <summary>0 for nothing to say, 1 warn, 2 alert - mapped to the widget's own colors (orange and red by default).</summary>
	private static SKColor? ThresholdColor(ThresholdStatElement element, int severity)
	{
		return severity switch
		{
			2 => ResolveColor(element.AlertColor, ThresholdAlertDefault),
			1 => ResolveColor(element.WarnColor, ThresholdWarnDefault),
			_ => null
		};
	}

	/// <summary>The color a reading takes when it's past its limits: null keeps the accent color.</summary>
	private static SKColor? StatValueColor(LabeledStatElement element, DerivedFrame frame)
	{
		switch (element)
		{
			case RangeLimitedElement limited:
			{
				double? value = limited is HeightElement ? frame.Raw.HeightAboveHomeMeters : frame.Raw.DistanceFromHomeMeters;
				if (value is not { } meters || !double.IsFinite(meters)) return null;

				return ThresholdColor(limited, limited.AlertAboveMeters is { } alert && meters > alert ? 2 : limited.WarnAboveMeters is { } warn && meters > warn ? 1 : 0);
			}
			case SatellitesElement { ColorByCount: true } gps when frame.Raw.GpsSatellites is >= 0 and { } count:
				return ThresholdColor(gps, count < gps.AlertBelow ? 2 : count < gps.WarnBelow ? 1 : 0);
			case VerticalSpeedElement { } vertical when frame.Raw.VerticalSpeedMs is { } rate && double.IsFinite(rate):
				return Math.Abs(rate) < 0.05 ? null : rate > 0 ? ParseColor(vertical.ClimbColor) : ParseColor(vertical.SinkColor);
			default:
				return null;
		}
	}

	private static SKColor? ParseColor(string? hex)
	{
		return string.IsNullOrWhiteSpace(hex) ? null : ResolveColor(hex, White);
	}

	/// <summary>The flight mode's color: ATTI (no GPS hold) is an alert, the modes that take the aircraft down (go home, landing) a warning.</summary>
	private static SKColor? ModeColor(FlightModeElement element, string mode)
	{
		if (!element.ColorByMode) return null;

		return ThresholdColor(element, mode switch
		{
			"ATTI" => 2,
			"Go Home" or "Auto Landing" or "Confirm Landing" => 1,
			_ => 0
		});
	}

	internal static (string Value, string Unit) FormatVerticalSpeed(double? metersPerSecond, UnitSystem units, int? decimals = null)
	{
		string unit = units == UnitSystem.Imperial ? "FT/S" : "M/S";
		if (metersPerSecond is not { } speed || !double.IsFinite(speed)) return ("--", unit);

		string digits = Digits(decimals, "0.0");
		double shown = units == UnitSystem.Imperial ? speed * MetersToFeet : speed;
		return (Math.Abs(shown) < 0.05 ? F(0, digits) : F(shown, $"+{digits};-{digits}"), unit);
	}

	internal static (string Value, string Unit) FormatTemperature(double? celsius, UnitSystem units, int? decimals = null) =>
		(celsius is { } value && double.IsFinite(value) ? F(units == UnitSystem.Imperial ? value * 1.8 + 32 : value, Digits(decimals, "0.0")) : "--",
			units == UnitSystem.Imperial ? "°F" : "°C");

	/// <summary>
	///     Whole metres/feet below a kilometre/mile - a GPS position isn't good to the centimetre. The unit switches on the
	///     rounded value, so 999.6 m reads 1.00 KM rather than 1000 M.
	/// </summary>
	private static (string Value, string Unit) FormatDistance(double meters, UnitSystem units, int? decimals = null)
	{
		if (units == UnitSystem.Imperial)
		{
			return Math.Round(meters * MetersToFeet) >= 5280
				? (F(meters / MilesInMeters, Digits(decimals, "0.00")), "MI")
				: (F(meters * MetersToFeet, Digits(decimals, "0")), "FT");
		}

		return Math.Round(meters) >= 1000
			? (F(meters / 1000.0, Digits(decimals, "0.00")), "KM")
			: (F(meters, Digits(decimals, "0")), "M");
	}

	/// <summary>Same KmhToMph conversion SpeedGauge/GaugeMaxSpeed already use, exposed here for callers (RouteIntro) that report a speed as plain text rather than on the gauge itself.</summary>
	private static (string Value, string Unit) FormatSpeed(double kmh, UnitSystem units, int? decimals = null)
	{
		string digits = Digits(decimals, "0");
		return units == UnitSystem.Imperial
			? (F(kmh * KmhToMph, digits), "MPH")
			: (F(kmh, digits), "KM/H");
	}

	/// <summary>
	///     ISO/shutter speed/color temperature as the camera recorded them (DJI: the djmd stream's
	///     camera-settings block). Each field is independently nullable (a camera or a fallback read
	///     may not populate all three), so each renders "--" rather than pulling the whole widget down.
	/// </summary>
	private void DrawCameraInfo(SKCanvas canvas, DerivedFrame frame, CameraInfoElement element)
	{
		SKFont labelFont = TextFont(element, OverlayElementBounds.LabelFontSize);
		SKFont smallFont = TextFont(element, OverlayElementBounds.SmallFontSize);
		SKColor textColor = TextColorOf(element);
		SKColor accentColor = AccentColorOf(element);
		SKColor outlineColor = OutlineColorOf(element);

		string label = (element.Label ?? DefaultLabel(element)).ToUpperInvariant();
		string isoText = frame.Raw.Iso is { } iso ? $"ISO {F(iso, "0")}" : "ISO --";
		string shutterText = FormatShutter(frame.Raw.ShutterSeconds);
		string colorTempText = frame.Raw.ColorTemperatureKelvin is { } kelvin ? $"{kelvin} K" : "-- K";
		float box = element.TextAlign == TextAlign.Left ? 0 : Math.Max(OverlayElementBounds.TextBoxRight(element),
			Math.Max(labelFont.MeasureText(label), new[] { isoText, shutterText, colorTempText }.Max(t => smallFont.MeasureText(t))));
		float X(SKFont font, string text) => OverlayElementBounds.AlignedX(element.TextAlign, box, font.MeasureText(text));

		DrawOutlined(canvas, label, X(labelFont, label), 0, labelFont, textColor, outlineColor: outlineColor, outlineWidthScale: element.OutlineWidth);
		DrawOutlined(canvas, isoText, X(smallFont, isoText), 58, smallFont, accentColor, outlineColor: outlineColor, outlineWidthScale: element.OutlineWidth);
		DrawOutlined(canvas, shutterText, X(smallFont, shutterText), 102, smallFont, accentColor, outlineColor: outlineColor, outlineWidthScale: element.OutlineWidth);
		DrawOutlined(canvas, colorTempText, X(smallFont, colorTempText), 146, smallFont, accentColor, outlineColor: outlineColor, outlineWidthScale: element.OutlineWidth);
	}

	/// <summary>Shutter speed as photographers read it (a "1/500" fraction) above 1s, plain seconds below - "1/0.5" would be nonsense.</summary>
	private static string FormatShutter(double? seconds)
	{
		if (seconds is not > 0) return "-- S";
		double denominator = 1.0 / seconds.Value;
		return denominator >= 1 ? $"1/{F(denominator, "0")} S" : $"{F(seconds.Value, "0.0")} S";
	}

	private void DrawStat(SKCanvas canvas, LabeledStatElement element, string label, string value, string unit, SKColor? valueColor = null)
	{
		SKFont valueFont = TextFont(element, OverlayElementBounds.ValueFontSize);
		SKColor textColor = TextColorOf(element);
		SKColor outlineColor = OutlineColorOf(element);

		SKFont labelFont = TextFont(element, OverlayElementBounds.LabelFontSize), unitFont = TextFont(element, OverlayElementBounds.UnitFontSize);
		string caption = element.UppercaseLabel ? label.ToUpperInvariant() : label;
		float valueWidth = valueFont.MeasureText(value), labelWidth = element.ShowLabel ? labelFont.MeasureText(caption) : 0;
		float readout = valueWidth + (element.ShowUnit ? OverlayElementBounds.StatUnitGapX + unitFont.MeasureText(unit) : 0);
		TextAlign align = OverlayElementBounds.SupportsTextAlign(element) ? element.TextAlign : TextAlign.Left;
		float box = align == TextAlign.Left ? 0 : Math.Max(OverlayElementBounds.TextBoxRight(element), Math.Max(labelWidth, readout));
		float readoutX = OverlayElementBounds.AlignedX(align, box, readout);

		if (element.ShowLabel)
		{
			DrawOutlined(canvas, caption, OverlayElementBounds.AlignedX(align, box, labelWidth), 0, labelFont, textColor,
				outlineColor: outlineColor, outlineWidthScale: element.OutlineWidth);
		}

		DrawOutlined(canvas, value, readoutX, 90, valueFont, valueColor ?? AccentColorOf(element), outlineColor: outlineColor, outlineWidthScale: element.OutlineWidth);
		if (element.ShowUnit)
		{
			DrawOutlined(canvas, unit, readoutX + valueWidth + OverlayElementBounds.StatUnitGapX, 90, unitFont, textColor,
				outlineColor: outlineColor, outlineWidthScale: element.OutlineWidth);
		}
	}

	/// <summary>This widget's own font at `baseSize`, from its FontFamily override (or the built-in HUD font when null/not installed) - see OverlayRenderer.GetFont.</summary>
	private SKFont TextFont(StyledOverlayElement element, float baseSize)
	{
		return GetFont(element.FontFamily, baseSize);
	}

	private static SKColor TextColorOf(StyledOverlayElement element)
	{
		return ResolveColor(element.TextColor, White);
	}

	private static SKColor AccentColorOf(LabeledStatElement element)
	{
		return ResolveColor(element.AccentColor, Accent);
	}

	private static SKColor OutlineColorOf(StyledOverlayElement element)
	{
		return ResolveColor(element.OutlineColor, Shadow);
	}
}
