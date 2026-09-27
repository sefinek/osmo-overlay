using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OsmoOverlay.Core.Telemetry;

public static partial class ExifToolRunner
{
	internal const string ExifToolExe = "exiftool";

	[GeneratedRegex(@"model_name:([^;]+)")]
	private static partial Regex ModelNameInCategoryRegex();

	public static string? GetCameraModel(string inputPath)
	{
		ProcessStartInfo psi = ProcessHelper.CreateHidden(ExifToolExe, "-Model", "-Make", "-Category", "-j", inputPath);

		var (exitCode, stdout, _) = ProcessHelper.RunCaptured(psi);
		if (exitCode != 0) return null;

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
}
