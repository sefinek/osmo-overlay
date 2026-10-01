namespace OsmoOverlay.Cameras.Dji;

/// <summary>The camera's djmd stream names it by DJI's internal code ("DJI AC004"); this maps the known codes to the model's own name.</summary>
public static class DjiModels
{
	public const string OsmoAction5Pro = "DJI Osmo Action 5 Pro";
	public const string OsmoAction6 = "DJI Osmo Action 6";

	private static readonly Dictionary<string, string> ByCode = new(StringComparer.OrdinalIgnoreCase)
	{
		["DJI AC004"] = OsmoAction5Pro,
		["DJI AC006"] = OsmoAction6
	};

	public static bool IsSupported(string? model)
	{
		return model is OsmoAction5Pro or OsmoAction6;
	}

	/// <summary>The model's name for a known code; anything else (another model, exiftool's own name) as it came.</summary>
	public static string? DisplayName(string? deviceName)
	{
		return deviceName is not null && ByCode.TryGetValue(deviceName.Trim(), out string? name) ? name : deviceName;
	}
}
