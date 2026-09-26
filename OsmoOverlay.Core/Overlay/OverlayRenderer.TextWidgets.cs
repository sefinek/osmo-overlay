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
		var text = OverlayTimeFormatting.FormatElapsed(frame.Raw.SampleTimeSeconds);
		DrawOutlined(canvas, text, 0, 0, TextFont(element, OverlayElementBounds.DateFontSize), TextColorOf(element),
			outlineColor: OutlineColorOf(element), outlineWidthScale: element.OutlineWidth);

		// Above the time, so a caption added later doesn't move the time itself.
		if (!string.IsNullOrWhiteSpace(element.Label))
			DrawOutlined(canvas, element.Label.ToUpperInvariant(), 0, -OverlayElementBounds.CaptionAboveOffset,
				TextFont(element, OverlayElementBounds.LabelFontSize), TextColorOf(element),
				outlineColor: OutlineColorOf(element), outlineWidthScale: element.OutlineWidth);
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
		var lines = OverlayElementBounds.TextLines(element.Text);
		for (var i = 0; i < lines.Length; i++)
			DrawOutlined(canvas, lines[i], 0, i * font.Spacing, font, color, outlineColor: outline, outlineWidthScale: element.OutlineWidth);
	}

	private void DrawElevation(SKCanvas canvas, DerivedFrame frame, ElevationElement element)
	{
		var meters = element.Reference == ElevationReference.SeaLevel ? frame.Raw.AltitudeMeters : frame.Raw.AltitudeMeters - _startAltitude;
		var (value, unit) = FormatAltitude(meters, element.Units);
		DrawStat(canvas, element, element.Label ?? "ELEVATION", value, unit);
	}

	/// <summary>The value so far at `frame` (TripStats) - where it is in the recording found by its time.</summary>
	private void DrawTripStat(SKCanvas canvas, DerivedFrame frame, TripStatElement element)
	{
		var i = _allFrames.Count > 0 ? TelemetryProcessor.FindIndex(_allFrames, frame.Raw.SampleTimeSeconds) : -1;
		var (value, unit) = i < 0
			? ("--", "")
			: element.Stat switch
			{
				TripStatKind.MaxSpeed => FormatSpeed(_tripStats.MaxSpeedKmh(i), element.Units),
				TripStatKind.AverageSpeed => FormatSpeed(_tripStats.AverageSpeedKmh(i), element.Units),
				TripStatKind.ElevationGain => FormatAltitude(_tripStats.ElevationGainMeters(i), element.Units),
				TripStatKind.ElevationLoss => FormatAltitude(_tripStats.ElevationLossMeters(i), element.Units),
				_ => (OverlayTimeFormatting.FormatElapsed(_tripStats.MovingSeconds(i)), "")
			};
		DrawStat(canvas, element, element.Label ?? DefaultTripStatLabel(element.Stat), value, unit);
	}

	public static string DefaultTripStatLabel(TripStatKind stat)
	{
		return stat switch
		{
			TripStatKind.MaxSpeed => "MAX SPEED",
			TripStatKind.AverageSpeed => "AVG SPEED",
			TripStatKind.ElevationGain => "ELEVATION GAIN",
			TripStatKind.ElevationLoss => "ELEVATION LOSS",
			_ => "MOVING TIME"
		};
	}

	/// <summary>Whole metres/feet - an altitude or a climb, where FormatDistance's decimals would only show GPS noise.</summary>
	private static (string Value, string Unit) FormatAltitude(double meters, UnitSystem units)
	{
		return units == UnitSystem.Imperial
			? (F(meters * MetersToFeet, "0"), "FT")
			: (F(meters, "0"), "M");
	}

	private void DrawGradient(SKCanvas canvas, DerivedFrame frame, GradientElement element)
	{
		DrawStat(canvas, element, element.Label ?? "GRADIENT", F(frame.GradientPercent, "0"), "%");
	}

	private void DrawDistance(SKCanvas canvas, DerivedFrame frame, DistanceElement element)
	{
		var (distanceValue, distanceUnit) = FormatDistance(frame.CumulativeDistanceMeters, element.Units);
		DrawStat(canvas, element, element.Label ?? "TOTAL DISTANCE", distanceValue, distanceUnit);
	}

	private static (string Value, string Unit) FormatDistance(double meters, UnitSystem units)
	{
		if (units == UnitSystem.Imperial)
			return meters >= MilesInMeters
				? (F(meters / MilesInMeters, "0.00"), "MI")
				: (F(meters * MetersToFeet, "0.00"), "FT");

		return meters >= 1000
			? (F(meters / 1000.0, "0.00"), "KM")
			: (F(meters, "0.00"), "M");
	}

	/// <summary>Same KmhToMph conversion SpeedGauge/GaugeMaxSpeed already use, exposed here for callers (RouteIntro) that report a speed as plain text rather than on the gauge itself.</summary>
	private static (string Value, string Unit) FormatSpeed(double kmh, UnitSystem units)
	{
		return units == UnitSystem.Imperial
			? (F(kmh * KmhToMph, "0"), "MPH")
			: (F(kmh, "0"), "KM/H");
	}

	/// <summary>
	///     ISO/shutter speed/color temperature straight from the djmd stream's camera-settings block
	///     (see DjiMetaTelemetryParser). Each field is independently nullable (a fallback exiftool read
	///     may not populate all three), so each renders "--" rather than pulling the whole widget down.
	/// </summary>
	private void DrawCameraInfo(SKCanvas canvas, DerivedFrame frame, CameraInfoElement element)
	{
		SKFont labelFont = TextFont(element, OverlayElementBounds.LabelFontSize);
		SKFont smallFont = TextFont(element, OverlayElementBounds.SmallFontSize);
		SKColor textColor = TextColorOf(element);
		SKColor accentColor = AccentColorOf(element);
		SKColor outlineColor = OutlineColorOf(element);

		DrawOutlined(canvas, (element.Label ?? "CAMERA").ToUpperInvariant(), 0, 0, labelFont, textColor,
			outlineColor: outlineColor, outlineWidthScale: element.OutlineWidth);

		var isoText = frame.Raw.Iso is { } iso ? $"ISO {F(iso, "0")}" : "ISO --";
		var colorTempText = frame.Raw.ColorTemperatureKelvin is { } kelvin ? $"{kelvin} K" : "-- K";

		DrawOutlined(canvas, isoText, 0, 58, smallFont, accentColor, outlineColor: outlineColor, outlineWidthScale: element.OutlineWidth);
		DrawOutlined(canvas, FormatShutter(frame.Raw.ShutterSeconds), 0, 102, smallFont, accentColor,
			outlineColor: outlineColor, outlineWidthScale: element.OutlineWidth);
		DrawOutlined(canvas, colorTempText, 0, 146, smallFont, accentColor, outlineColor: outlineColor, outlineWidthScale: element.OutlineWidth);
	}

	/// <summary>Shutter speed as photographers read it (a "1/500" fraction) above 1s, plain seconds below - "1/0.5" would be nonsense.</summary>
	private static string FormatShutter(double? seconds)
	{
		if (seconds is not > 0) return "-- S";
		var denominator = 1.0 / seconds.Value;
		return denominator >= 1 ? $"1/{F(denominator, "0")} S" : $"{F(seconds.Value, "0.0")} S";
	}

	private void DrawStat(SKCanvas canvas, LabeledStatElement element, string label, string value, string unit)
	{
		SKFont valueFont = TextFont(element, OverlayElementBounds.ValueFontSize);
		SKColor textColor = TextColorOf(element);
		SKColor outlineColor = OutlineColorOf(element);

		DrawOutlined(canvas, label.ToUpperInvariant(), 0, 0, TextFont(element, OverlayElementBounds.LabelFontSize), textColor,
			outlineColor: outlineColor, outlineWidthScale: element.OutlineWidth);
		DrawOutlined(canvas, value, 0, 90, valueFont, AccentColorOf(element), outlineColor: outlineColor, outlineWidthScale: element.OutlineWidth);
		var valueWidth = valueFont.MeasureText(value);
		DrawOutlined(canvas, unit, valueWidth + 12, 90, TextFont(element, OverlayElementBounds.UnitFontSize), textColor,
			outlineColor: outlineColor, outlineWidthScale: element.OutlineWidth);
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
