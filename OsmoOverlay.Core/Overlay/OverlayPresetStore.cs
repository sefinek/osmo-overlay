using System.Text.Json;
using OsmoOverlay.Core.Logging;

namespace OsmoOverlay.Core.Overlay;

/// <summary>
///     Persists named overlay layout presets shared across the GUI editor and the CLI (which has no
///     editor, so it just renders with whichever preset is marked active). Each preset lives in its
///     own file under PresetsDir (named by its id), rather than one combined file, so presets stay
///     individually diffable/shareable and a corrupt file only loses that one preset.
/// </summary>
public static class OverlayPresetStore
{
	/// <summary>
	///     Id of the built-in read-only preset created on first run. The GUI locks editing (rename,
	///     delete, reposition/toggle widgets, per-widget settings) whenever this preset is active, so
	///     there's always one untouched baseline layout - editing goes through Duplicate instead.
	/// </summary>
	public const string DefaultPresetId = "default";

	private static readonly string StoreDir = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OsmoOverlay");

	private static string PresetsDir => Path.Combine(StoreDir, "presets");

	// Pre-v2 layout: every preset plus the active id in a single overlay-presets.json. Migrated to
	// one-file-per-preset (below) on first load found, if the new presets dir doesn't exist yet.
	private static string LegacyStorePath => Path.Combine(StoreDir, "overlay-presets.json");

	public static (List<OverlayPreset> Presets, string ActivePresetId) Load(int width, int height)
	{
		try
		{
			MigrateLegacyStoreIfNeeded();

			List<OverlayPreset> presets = LoadPresetFiles();
			if (presets.Count > 0)
			{
				var activeId = OverlaySettingsStore.Load().ActivePresetId;
				if (activeId is null || presets.All(p => p.Id != activeId)) activeId = presets[0].Id;
				return (presets, activeId);
			}
		}
		catch (Exception ex)
		{
			// Best-effort: corrupt or unreadable presets fall back to the built-in default.
			AppLogger.Warn(ex, "Overlay presets are corrupt or unreadable, falling back to the default preset");
		}

		var defaultPreset = OverlayPreset.CreateDefault(DefaultPresetId, "Default", width, height);
		return ([defaultPreset], defaultPreset.Id);
	}

	public static void Save(List<OverlayPreset> presets, string activePresetId)
	{
		try
		{
			Directory.CreateDirectory(PresetsDir);

			HashSet<string> keepFiles = presets.Select(p => PresetPath(p.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
			foreach (var file in Directory.EnumerateFiles(PresetsDir, "*.json"))
				if (!keepFiles.Contains(file))
					File.Delete(file);

			foreach (OverlayPreset preset in presets)
				File.WriteAllText(PresetPath(preset.Id), JsonSerializer.Serialize(preset));

			OverlaySettingsStore.Save(OverlaySettingsStore.Load() with { ActivePresetId = activePresetId });
		}
		catch (Exception ex)
		{
			// Best-effort cache: a failed write should not break the editing flow.
			AppLogger.Warn(ex, "Failed to write overlay presets");
		}
	}

	private static string PresetPath(string id)
	{
		return Path.Combine(PresetsDir, $"{id}.json");
	}

	private static List<OverlayPreset> LoadPresetFiles()
	{
		List<OverlayPreset> presets = [];
		if (!Directory.Exists(PresetsDir)) return presets;

		foreach (var file in Directory.EnumerateFiles(PresetsDir, "*.json"))
			try
			{
				var preset = JsonSerializer.Deserialize<OverlayPreset>(File.ReadAllText(file));
				if (preset is not null) presets.Add(preset);
			}
			catch (Exception ex)
			{
				// One corrupt preset file shouldn't take the rest of the presets down with it.
				AppLogger.Warn(ex, $"Skipping unreadable overlay preset file: {file}");
			}

		return presets;
	}

	/// <summary>
	///     One-time upgrade path for installs that still have the old single-file store: splits it into
	///     one file per preset and removes the old file, so it isn't re-migrated (or shadows the new
	///     files) on the next run.
	/// </summary>
	private static void MigrateLegacyStoreIfNeeded()
	{
		if (Directory.Exists(PresetsDir) || !File.Exists(LegacyStorePath)) return;

		var data = JsonSerializer.Deserialize<StoredData>(File.ReadAllText(LegacyStorePath));
		if (data is not { Presets.Count: > 0 }) return;

		Directory.CreateDirectory(PresetsDir);
		foreach (OverlayPreset preset in data.Presets)
			File.WriteAllText(PresetPath(preset.Id), JsonSerializer.Serialize(preset));

		var activeId = data.ActivePresetId ?? data.Presets[0].Id;
		OverlaySettingsStore.Save(OverlaySettingsStore.Load() with { ActivePresetId = activeId });
		File.Delete(LegacyStorePath);
	}

	private sealed record StoredData(List<OverlayPreset> Presets, string? ActivePresetId);
}
