using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Cameras;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Cameras.Dji;

/// <summary>
///     DJI telemetry through the external exiftool - the fallback when the native djmd decoder throws (another model or
///     firmware may lay the stream out differently). Shares GpsForwardFill with it.
/// </summary>
internal static class ExifToolTelemetry
{
	public static TelemetryExtractionResult Extract(string inputPath)
	{
		ProcessStartInfo psi = ProcessHelper.CreateHidden(ExifToolRunner.ExifToolExe,
			"-ee", "-G3", "-n", "-json", "-a", "-s",
			"-FrameNumber", "-SampleTime", "-Model",
			"-GPSLatitude", "-GPSLongitude", "-GPSAltitude", "-GPSDateTime",
			"-AccelerometerX", "-AccelerometerY", "-AccelerometerZ",
			inputPath);

		(int exitCode, string stdout, string stderr) = ProcessHelper.RunCaptured(psi);
		if (exitCode != 0)
			throw new InvalidOperationException($"exiftool exited with an error ({exitCode}): {stderr}");

		JsonArray array = JsonNode.Parse(stdout)?.AsArray()
		                  ?? throw new InvalidOperationException("Empty exiftool output.");

		if (array.Count == 0)
			throw new InvalidOperationException($"exiftool returned no data for {inputPath}.");

		JsonObject fileObject = array[0]!.AsObject();
		var docs = new Dictionary<string, Dictionary<string, JsonNode?>>();

		foreach ((string key, JsonNode? value) in fileObject)
		{
			if (key == "SourceFile") continue;

			int separatorIndex = key.IndexOf(':');
			string docLabel = separatorIndex >= 0 ? key[..separatorIndex] : "Doc1";
			string field = separatorIndex >= 0 ? key[(separatorIndex + 1)..] : key;

			if (!docs.TryGetValue(docLabel, out Dictionary<string, JsonNode?>? fields))
				docs[docLabel] = fields = [];

			fields[field] = value;
		}

		var rawFrames = new List<(int FrameNumber, double SampleTime, double? Lat, double? Lon, double? Alt,
			DateTime? GpsTimestamp, double AccelX, double AccelY, double AccelZ)>(docs.Count);

		foreach ((string docLabel, Dictionary<string, JsonNode?> fields) in docs)
		{
			int frameNumber;
			if (fields.TryGetValue("FrameNumber", out JsonNode? frameNumberNode) && frameNumberNode is not null)
				frameNumber = frameNumberNode.GetValue<int>();
			else if (docLabel == "Doc1")
				frameNumber = 0;
			else
				continue;

			rawFrames.Add((
				frameNumber,
				GetDouble(fields, "SampleTime"),
				GetNullableDouble(fields, "GPSLatitude"),
				GetNullableDouble(fields, "GPSLongitude"),
				GetNullableDouble(fields, "GPSAltitude"),
				GetDateTime(fields, "GPSDateTime"),
				GetDouble(fields, "AccelerometerX"),
				GetDouble(fields, "AccelerometerY"),
				GetDouble(fields, "AccelerometerZ")));
		}

		rawFrames.Sort((a, b) => a.FrameNumber.CompareTo(b.FrameNumber));

		var frames = new List<TelemetryFrame>(rawFrames.Count);
		var gpsFill = new GpsForwardFill();
		foreach ((int frameNumber, double sampleTime, double? rawLat, double? rawLon, double? rawAlt, DateTime? gpsTimestamp, double accelX, double accelY, double accelZ) in rawFrames)
		{
			(double lat, double lon, double altitudeMeters, bool hasFix) = gpsFill.Apply(rawLat, rawLon, rawAlt);

			frames.Add(new TelemetryFrame(
				frameNumber,
				sampleTime,
				lat,
				lon,
				altitudeMeters,
				gpsTimestamp,
				accelX,
				accelY,
				accelZ,
				HasGpsFix: hasFix));
		}

		string? cameraModel = docs.Values
			.Select(fields => fields.TryGetValue("Model", out JsonNode? m) ? m?.GetValue<string>() : null)
			.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

		return new TelemetryExtractionResult(frames, cameraModel);
	}

	private static double GetDouble(Dictionary<string, JsonNode?> fields, string key)
	{
		return GetNullableDouble(fields, key) ?? 0.0;
	}

	private static double? GetNullableDouble(Dictionary<string, JsonNode?> fields, string key)
	{
		if (!fields.TryGetValue(key, out JsonNode? node) || node is null) return null;

		JsonValue value = node.AsValue();
		if (value.TryGetValue<double>(out double d)) return d;
		return double.Parse(value.GetValue<string>(), CultureInfo.InvariantCulture);
	}

	private static DateTime? GetDateTime(Dictionary<string, JsonNode?> fields, string key)
	{
		if (!fields.TryGetValue(key, out JsonNode? node) || node is null) return null;
		string raw = node.GetValue<string>();
		return DateTime.TryParseExact(raw, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture,
			DateTimeStyles.None, out DateTime dt)
			? dt
			: null;
	}
}
