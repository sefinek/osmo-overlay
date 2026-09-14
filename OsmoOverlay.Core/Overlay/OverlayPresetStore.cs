using System.Text.Json;

namespace OsmoOverlay.Core.Overlay;

/// <summary>
///     Persists named overlay layout presets shared across the GUI editor and the CLI (which has no
///     editor, so it just renders with whichever preset is marked active).
/// </summary>
public static class OverlayPresetStore
{
	private const string DefaultPresetId = "default";

	private static readonly string StoreDir = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OsmoOverlay");

	private static string StorePath => Path.Combine(StoreDir, "overlay-presets.json");

	public static (List<OverlayPreset> Presets, string ActivePresetId) Load(int width, int height)
	{
		try
		{
			if (File.Exists(StorePath))
			{
				var data = JsonSerializer.Deserialize<StoredData>(File.ReadAllText(StorePath));
				if (data is { Presets.Count: > 0 })
					return (data.Presets, data.ActivePresetId ?? data.Presets[0].Id);
			}
		}
		catch
		{
			// Best-effort: a corrupt or unreadable presets file falls back to the built-in default.
		}

		var defaultPreset = OverlayPreset.CreateDefault(DefaultPresetId, "Default", width, height);
		return ([defaultPreset], defaultPreset.Id);
	}

	public static void Save(List<OverlayPreset> presets, string activePresetId)
	{
		try
		{
			Directory.CreateDirectory(StoreDir);
			var data = new StoredData(presets, activePresetId);
			File.WriteAllText(StorePath, JsonSerializer.Serialize(data));
		}
		catch
		{
			// Best-effort cache: a failed write should not break the editing flow.
		}
	}

	private sealed record StoredData(List<OverlayPreset> Presets, string? ActivePresetId);
}
