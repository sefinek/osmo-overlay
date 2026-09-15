using System.Text.Json;
using OsmoOverlay.Core.Logging;

namespace OsmoOverlay.Core.Overlay;

/// <summary>
///     Small pieces of overlay-related state that aren't tied to any single preset's layout: which
///     preset is active, and whether the attribution watermark is shown.
/// </summary>
public sealed record OverlaySettings(string? ActivePresetId = null, bool ShowWatermark = true);

/// <summary>
///     Persists OverlaySettings to one general settings.json, shared between the GUI and the CLI the
///     same way overlay presets are.
/// </summary>
public static class OverlaySettingsStore
{
	private static readonly string StoreDir = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OsmoOverlay");

	private static string StorePath => Path.Combine(StoreDir, "settings.json");

	public static OverlaySettings Load()
	{
		try
		{
			if (File.Exists(StorePath))
			{
				var settings = JsonSerializer.Deserialize<OverlaySettings>(File.ReadAllText(StorePath));
				if (settings is not null) return settings;
			}
		}
		catch (Exception ex)
		{
			// Best-effort: a corrupt or unreadable settings file falls back to the defaults.
			AppLogger.Warn(ex, "Overlay settings file is corrupt or unreadable, falling back to defaults");
		}

		return new OverlaySettings();
	}

	public static void Save(OverlaySettings settings)
	{
		try
		{
			Directory.CreateDirectory(StoreDir);
			File.WriteAllText(StorePath, JsonSerializer.Serialize(settings));
		}
		catch (Exception ex)
		{
			// Best-effort cache: a failed write should not break the editing flow.
			AppLogger.Warn(ex, "Failed to write overlay settings file");
		}
	}
}
