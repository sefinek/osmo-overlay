using System.Globalization;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Telemetry;
using SkiaSharp;

namespace OsmoOverlay.Core.Overlay;

/// <summary>Text/stat widgets: DateTimeText/UtcTimeText, ElapsedTimeText, CameraModelText, Elevation/Gradient/Distance and CameraInfo.</summary>
public sealed partial class OverlayRenderer
{
	private const double MetersToFeet = 3.28084;
	private const double MilesInMeters = 1609.344;
	private const string DefaultDateFormat = "dd/MM/yyyy  HH:mm:ss";

	private void DrawDateTime(SKCanvas canvas, DerivedFrame frame, OverlayElement element)
	{
		DrawTimeText(canvas, frame, element, true);
	}

	private void DrawUtcTime(SKCanvas canvas, DerivedFrame frame, OverlayElement element)
	{
		DrawTimeText(canvas, frame, element, false);
	}

	/// <summary>Shared by DateTimeText (local time) and UtcTimeText (raw UTC, no conversion) - both use the same DateFormat/Locale fields.</summary>
	private void DrawTimeText(SKCanvas canvas, DerivedFrame frame, OverlayElement element, bool toLocal)
	{
		canvas.Save();
		canvas.Translate(element.X, element.Y);
		canvas.Scale(_scale, _scale);

		// Falls back to the container's own recording-start tag + elapsed time when this frame has no
		// real GPS timestamp (see SourceInfo.ContainerCreationTimeUtc) - approximate (camera clock, not
		// a GPS-synced one), but still far more useful than "--" for a file that never had a fix at
		// all. The GUI signals this with a warning icon next to the checkbox (see
		// MainWindow.RefreshElementCheckboxes) rather than marking it inside the burned-in video text.
		DateTime? utc = frame.Raw.GpsTimestamp ??
		                _containerRecordingStartUtc?.AddSeconds(frame.Raw.SampleTimeSeconds);

		string dateText;
		if (utc is not { } resolvedUtc)
		{
			dateText = "--";
		}
		else
		{
			DateTime shown = toLocal ? resolvedUtc.ToLocalFromUtc() : resolvedUtc;
			try
			{
				CultureInfo culture = element.Locale is null ? CultureInfo.CurrentCulture : CultureInfo.GetCultureInfo(element.Locale);
				dateText = shown.ToString(element.DateFormat ?? DefaultDateFormat, culture);
			}
			catch (Exception ex) when (ex is CultureNotFoundException or FormatException)
			{
				// A hand-edited/shared preset can carry an invalid Locale or DateFormat string - fail
				// soft (fall back to the default) instead of throwing out of Render and aborting the
				// whole export, same "one bad user-editable field" policy as the map widget's
				// placeholder fallback.
				AppLogger.Warn(ex,
					$"Time widget: invalid Locale/DateFormat ('{element.Locale}' / '{element.DateFormat}') - using default.");
				dateText = shown.ToString(DefaultDateFormat, CultureInfo.CurrentCulture);
			}

			if (!toLocal) dateText += "  UTC";
		}

		DrawOutlined(canvas, dateText, 0, 0, _dateFont, White);

		canvas.Restore();
	}

	/// <summary>Time since the recording's own frame 0 (SampleTimeSeconds), not a wall-clock reading - a stopwatch, distinct from DateTimeText/UtcTimeText.</summary>
	private void DrawElapsedTime(SKCanvas canvas, DerivedFrame frame, OverlayElement element)
	{
		canvas.Save();
		canvas.Translate(element.X, element.Y);
		canvas.Scale(_scale, _scale);

		TimeSpan elapsed = TimeSpan.FromSeconds(Math.Max(frame.Raw.SampleTimeSeconds, 0));
		var text = elapsed.TotalHours >= 1
			? $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
			: $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
		DrawOutlined(canvas, text, 0, 0, _dateFont, White);

		canvas.Restore();
	}

	/// <summary>Static per-recording text (FileSummary.CameraModel) - same value on every frame, so unlike the other widgets nothing here depends on `frame`.</summary>
	private void DrawCameraModel(SKCanvas canvas, OverlayElement element)
	{
		canvas.Save();
		canvas.Translate(element.X, element.Y);
		canvas.Scale(_scale, _scale);

		DrawOutlined(canvas, _cameraModel ?? "--", 0, 0, _dateFont, White);

		canvas.Restore();
	}

	private void DrawElevation(SKCanvas canvas, DerivedFrame frame, OverlayElement element)
	{
		canvas.Save();
		canvas.Translate(element.X, element.Y);
		canvas.Scale(_scale, _scale);
		var meters = frame.Raw.AltitudeMeters - _startAltitude;
		var (value, unit) = element.Units == UnitSystem.Imperial
			? (F(meters * MetersToFeet, "0"), "FT")
			: (F(meters, "0"), "M");
		DrawStat(canvas, element.Label ?? "ELEVATION", value, unit);
		canvas.Restore();
	}

	private void DrawGradient(SKCanvas canvas, DerivedFrame frame, OverlayElement element)
	{
		canvas.Save();
		canvas.Translate(element.X, element.Y);
		canvas.Scale(_scale, _scale);
		DrawStat(canvas, element.Label ?? "GRADIENT", F(frame.GradientPercent, "0"), "%");
		canvas.Restore();
	}

	private void DrawDistance(SKCanvas canvas, DerivedFrame frame, OverlayElement element)
	{
		canvas.Save();
		canvas.Translate(element.X, element.Y);
		canvas.Scale(_scale, _scale);
		var (distanceValue, distanceUnit) = FormatDistance(frame.CumulativeDistanceMeters, element.Units);
		DrawStat(canvas, element.Label ?? "TOTAL DISTANCE", distanceValue, distanceUnit);
		canvas.Restore();
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
	///     (see DjiMetaTelemetryParser) - exposure metadata that was already being extracted for every
	///     file but never shown anywhere. Each field is independently nullable (a fallback exiftool read
	///     may not populate all three), so each renders "--" rather than pulling the whole widget down.
	/// </summary>
	private void DrawCameraInfo(SKCanvas canvas, DerivedFrame frame, OverlayElement element)
	{
		canvas.Save();
		canvas.Translate(element.X, element.Y);
		canvas.Scale(_scale, _scale);

		DrawOutlined(canvas, (element.Label ?? "CAMERA").ToUpperInvariant(), 0, 0, _labelFont, White);

		var isoText = frame.Raw.Iso is { } iso ? $"ISO {F(iso, "0")}" : "ISO --";
		var colorTempText = frame.Raw.ColorTemperatureKelvin is { } kelvin ? $"{kelvin} K" : "-- K";

		DrawOutlined(canvas, isoText, 0, 58, _smallFont, Accent);
		DrawOutlined(canvas, FormatShutter(frame.Raw.ShutterSeconds), 0, 102, _smallFont, Accent);
		DrawOutlined(canvas, colorTempText, 0, 146, _smallFont, Accent);

		canvas.Restore();
	}

	/// <summary>Shutter speed as photographers read it (a "1/500" fraction) above 1s, plain seconds below - "1/0.5" would be nonsense.</summary>
	private static string FormatShutter(double? seconds)
	{
		if (seconds is not > 0) return "-- S";
		var denominator = 1.0 / seconds.Value;
		return denominator >= 1 ? $"1/{F(denominator, "0")} S" : $"{F(seconds.Value, "0.0")} S";
	}

	private void DrawStat(SKCanvas canvas, string label, string value, string unit)
	{
		DrawOutlined(canvas, label.ToUpperInvariant(), 0, 0, _labelFont, White);
		DrawOutlined(canvas, value, 0, 90, _valueFont, Accent);
		var valueWidth = _valueFont.MeasureText(value);
		DrawOutlined(canvas, unit, valueWidth + 12, 90, _unitFont, White);
	}
}
