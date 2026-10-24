using System.Globalization;
using System.Text;
using System.Xml;
using OsmoOverlay.Core.Localization;

namespace OsmoOverlay.Core.Telemetry;

/// <summary>
///     The telemetry a recording was processed into (TelemetryProcessor: the speed after the calibration, smoothed heading,
///     slope, distance, tilt, G) as a CSV table or a GPX track, for other tools. Whole recording, before any cuts. Numbers are always
///     written with a dot, whatever the culture. Where the GPS had no fix the position is left empty, not the last known one or (0, 0).
/// </summary>
public static class TelemetryExport
{
	public const string CsvExtension = ".csv";
	public const string GpxExtension = ".gpx";

	private static readonly string[] CsvColumns =
	[
		"time_s", "utc", "latitude", "longitude", "altitude_m", "speed_kmh", "heading_deg", "gradient_pct", "distance_m",
		"roll_deg", "pitch_deg", "g_total", "g_lateral", "g_longitudinal", "gps_fix"
	];

	/// <summary>True for a path this can write: .csv or .gpx.</summary>
	public static bool IsSupported(string path)
	{
		return Format(path) is not null;
	}

	/// <summary>
	///     Writes `frames` to `path` as its extension says and returns how many it wrote. `intervalSeconds` keeps one frame in that
	///     time (0 = every frame - a recording has tens of them a second); `fallbackStartUtc` (the container's recording start) dates
	///     frames the GPS gave no time for.
	/// </summary>
	public static int Write(string path, IReadOnlyList<DerivedFrame> frames, double intervalSeconds = 0, DateTime? fallbackStartUtc = null)
	{
		string? format = Format(path);
		if (format is null) throw new ArgumentException(CoreStrings.TelemetryExport_UnsupportedFormat, nameof(path));

		AtomicFile.Write(path, stream =>
		{
			using var writer = new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024, true);
			if (format == CsvExtension) WriteCsv(writer, frames, intervalSeconds, fallbackStartUtc);
			else WriteGpx(writer, frames, intervalSeconds, fallbackStartUtc);
		});
		return Thin(frames, intervalSeconds).Count();
	}

	public static void WriteCsv(TextWriter writer, IReadOnlyList<DerivedFrame> frames, double intervalSeconds = 0, DateTime? fallbackStartUtc = null)
	{
		writer.Write(string.Join(',', CsvColumns));
		writer.Write('\n');
		foreach (DerivedFrame frame in Thin(frames, intervalSeconds))
		{
			TelemetryFrame raw = frame.Raw;
			writer.Write(string.Join(',',
				Number(raw.RecordingTimeSeconds, "0.###"),
				Time(frame, fallbackStartUtc) is { } utc ? Utc(utc) : "",
				raw.HasGpsFix ? Number(raw.Latitude, "0.0000000") : "",
				raw.HasGpsFix ? Number(raw.Longitude, "0.0000000") : "",
				raw.HasGpsFix ? Number(raw.AltitudeMeters, "0.##") : "",
				Number(frame.SpeedKmh, "0.##"),
				Number(frame.HeadingDegrees, "0.#"),
				Number(frame.GradientPercent, "0.##"),
				Number(frame.CumulativeDistanceMeters, "0.##"),
				Number(frame.RollDegrees, "0.##"),
				Number(frame.PitchDegrees, "0.##"),
				Number(raw.GForce, "0.###"),
				Number(frame.LateralAccelG, "0.###"),
				Number(frame.LongitudinalAccelG, "0.###"),
				raw.HasGpsFix ? "1" : "0"));
			writer.Write('\n');
		}
	}

	/// <summary>
	///     A GPX 1.1 track of the frames that have a GPS fix: position, altitude, time, and the speed (m/s) and course in Garmin's
	///     track point extension. A new segment starts where the recording has a gap (a file recorded after a stop, a stretch without a fix).
	/// </summary>
	public static void WriteGpx(TextWriter writer, IReadOnlyList<DerivedFrame> frames, double intervalSeconds = 0, DateTime? fallbackStartUtc = null)
	{
		const string gpxNs = "http://www.topografix.com/GPX/1/1";
		const string tpxNs = "http://www.garmin.com/xmlschemas/TrackPointExtension/v1";
		using var xml = XmlWriter.Create(writer, new XmlWriterSettings { Indent = true, OmitXmlDeclaration = false, NewLineChars = "\n", CloseOutput = false });
		xml.WriteStartDocument();
		xml.WriteStartElement("gpx", gpxNs);
		xml.WriteAttributeString("version", "1.1");
		xml.WriteAttributeString("creator", "OsmoOverlay");
		xml.WriteAttributeString("xmlns", "gpxtpx", null, tpxNs);
		xml.WriteStartElement("trk", gpxNs);
		xml.WriteElementString("name", gpxNs, "OsmoOverlay");

		bool open = false;
		foreach (DerivedFrame frame in Thin(frames, intervalSeconds))
		{
			TelemetryFrame raw = frame.Raw;
			if (!raw.HasGpsFix)
			{
				if (open) xml.WriteEndElement();
				open = false;
				continue;
			}

			if (open && frame.StartsAfterCut)
			{
				xml.WriteEndElement();
				open = false;
			}

			if (!open)
			{
				xml.WriteStartElement("trkseg", gpxNs);
				open = true;
			}

			xml.WriteStartElement("trkpt", gpxNs);
			xml.WriteAttributeString("lat", Number(raw.Latitude, "0.0000000"));
			xml.WriteAttributeString("lon", Number(raw.Longitude, "0.0000000"));
			xml.WriteElementString("ele", gpxNs, Number(raw.AltitudeMeters, "0.##"));
			if (Time(frame, fallbackStartUtc) is { } utc) xml.WriteElementString("time", gpxNs, Utc(utc));
			xml.WriteStartElement("extensions", gpxNs);
			xml.WriteStartElement("TrackPointExtension", tpxNs);
			xml.WriteElementString("speed", tpxNs, Number(frame.SpeedKmh / 3.6, "0.###"));
			xml.WriteElementString("course", tpxNs, Number(frame.HeadingDegrees, "0.#"));
			xml.WriteEndElement();
			xml.WriteEndElement();
			xml.WriteEndElement();
		}

		if (open) xml.WriteEndElement();
		xml.WriteEndElement();
		xml.WriteEndElement();
		xml.WriteEndDocument();
	}

	/// <summary>One frame in every `intervalSeconds` of the recording (the first of each), or all of them.</summary>
	internal static IEnumerable<DerivedFrame> Thin(IReadOnlyList<DerivedFrame> frames, double intervalSeconds)
	{
		if (intervalSeconds <= 0 || !double.IsFinite(intervalSeconds))
		{
			foreach (DerivedFrame frame in frames) yield return frame;
			yield break;
		}

		double next = double.NegativeInfinity;
		foreach (DerivedFrame frame in frames)
		{
			double t = frame.Raw.RecordingTimeSeconds;
			if (t < next && !frame.Raw.StartsAfterGap) continue;

			next = t + intervalSeconds;
			yield return frame;
		}
	}

	private static DateTime? Time(DerivedFrame frame, DateTime? fallbackStartUtc)
	{
		return frame.Raw.GpsTimestamp ?? fallbackStartUtc?.AddSeconds(frame.Raw.RecordingTimeSeconds);
	}

	private static string Utc(DateTime time)
	{
		return DateTime.SpecifyKind(time, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
	}

	private static string Number(double value, string format)
	{
		return value.ToString(format, CultureInfo.InvariantCulture);
	}

	private static string? Format(string path)
	{
		string extension = Path.GetExtension(path);
		if (extension.Equals(CsvExtension, StringComparison.OrdinalIgnoreCase)) return CsvExtension;
		return extension.Equals(GpxExtension, StringComparison.OrdinalIgnoreCase) ? GpxExtension : null;
	}
}
