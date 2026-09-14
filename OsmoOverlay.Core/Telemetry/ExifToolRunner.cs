using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OsmoOverlay.Core.Telemetry;

public sealed record TelemetryExtractionResult(List<TelemetryFrame> Frames, string? CameraModel);

public static partial class ExifToolRunner
{
	private const string ExifToolExe = "exiftool";

	[GeneratedRegex(@"model_name:([^;]+)")]
	private static partial Regex ModelNameInCategoryRegex();

	public static string? GetCameraModel(string inputPath)
	{
		ProcessStartInfo psi = ProcessHelper.CreateHidden(ExifToolExe, "-Model", "-Make", "-Category", "-j", inputPath);

		using Process process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start exiftool.");
		var stdout = process.StandardOutput.ReadToEnd();
		process.WaitForExit();

		if (process.ExitCode != 0) return null;

		JsonArray? array = JsonNode.Parse(stdout)?.AsArray();
		if (array is not { Count: > 0 }) return null;

		JsonObject obj = array[0]!.AsObject();
		var model = obj["Model"]?.GetValue<string>();
		if (!string.IsNullOrWhiteSpace(model)) return model;

		var make = obj["Make"]?.GetValue<string>();
		var category = obj["Category"]?.GetValue<string>();
		Match code = category is not null ? ModelNameInCategoryRegex().Match(category) : Match.Empty;

		return (make, code.Success) switch
		{
			(not null, true) => $"{make} ({code.Groups[1].Value})",
			(not null, false) => make,
			(null, true) => code.Groups[1].Value,
			_ => null
		};
	}

	public static TelemetryExtractionResult Extract(string inputPath)
	{
		ProcessStartInfo psi = ProcessHelper.CreateHidden(ExifToolExe,
			"-ee", "-G3", "-n", "-json", "-a", "-s",
			"-FrameNumber", "-SampleTime", "-Model",
			"-GPSLatitude", "-GPSLongitude", "-GPSAltitude", "-GPSDateTime",
			"-AccelerometerX", "-AccelerometerY", "-AccelerometerZ",
			inputPath);

		using Process process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start exiftool.");
		var stdout = process.StandardOutput.ReadToEnd();
		var stderr = process.StandardError.ReadToEnd();
		process.WaitForExit();

		if (process.ExitCode != 0)
			throw new InvalidOperationException($"exiftool exited with an error ({process.ExitCode}): {stderr}");

		JsonArray array = JsonNode.Parse(stdout)?.AsArray()
		                  ?? throw new InvalidOperationException("Empty exiftool output.");

		if (array.Count == 0)
			throw new InvalidOperationException($"exiftool returned no data for {inputPath}.");

		JsonObject fileObject = array[0]!.AsObject();
		var docs = new Dictionary<string, Dictionary<string, JsonNode?>>();

		foreach ((var key, JsonNode? value) in fileObject)
		{
			if (key == "SourceFile") continue;

			var separatorIndex = key.IndexOf(':');
			var docLabel = separatorIndex >= 0 ? key[..separatorIndex] : "Doc1";
			var field = separatorIndex >= 0 ? key[(separatorIndex + 1)..] : key;

			if (!docs.TryGetValue(docLabel, out Dictionary<string, JsonNode?>? fields))
				docs[docLabel] = fields = new Dictionary<string, JsonNode?>();

			fields[field] = value;
		}

		var rawFrames = new List<(int FrameNumber, double SampleTime, double? Lat, double? Lon, double? Alt,
			DateTime? GpsTimestamp, double AccelX, double AccelY, double AccelZ)>(docs.Count);

		foreach ((var docLabel, Dictionary<string, JsonNode?> fields) in docs)
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
		foreach ((int FrameNumber, double SampleTime, double? Lat, double? Lon, double? Alt, DateTime? GpsTimestamp, double AccelX, double AccelY, double AccelZ) raw in rawFrames)
		{
			var (lat, lon, altitudeMeters) = gpsFill.Apply(raw.Lat, raw.Lon, raw.Alt);

			frames.Add(new TelemetryFrame(
				raw.FrameNumber,
				raw.SampleTime,
				lat,
				lon,
				altitudeMeters,
				raw.GpsTimestamp,
				raw.AccelX,
				raw.AccelY,
				raw.AccelZ));
		}

		var cameraModel = docs.Values
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
		if (value.TryGetValue<double>(out var d)) return d;
		return double.Parse(value.GetValue<string>(), CultureInfo.InvariantCulture);
	}

	private static DateTime? GetDateTime(Dictionary<string, JsonNode?> fields, string key)
	{
		if (!fields.TryGetValue(key, out JsonNode? node) || node is null) return null;
		var raw = node.GetValue<string>();
		return DateTime.TryParseExact(raw, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture,
			DateTimeStyles.None, out DateTime dt)
			? dt
			: null;
	}
}
