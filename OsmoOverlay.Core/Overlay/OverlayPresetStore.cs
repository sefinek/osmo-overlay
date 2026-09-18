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
			RefreshBuiltInDefault(presets, width, height);
			BackfillMissingWidgetTypes(presets, width, height);
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
				AtomicFile.WriteAllText(PresetPath(preset.Id), JsonSerializer.Serialize(preset));

			OverlaySettingsStore.Save(OverlaySettingsStore.Load() with { ActivePresetId = activePresetId });
		}
		catch (Exception ex)
		{
			// Best-effort cache: a failed write should not break the editing flow.
			AppLogger.Warn(ex, "Failed to write overlay presets");
		}
	}

	/// <summary>
	///     The built-in Default preset is read-only and must always match whatever CreateDefault
	///     currently computes, not a stale snapshot from whenever it was first written to disk - guards
	///     against both a later CreateDefault change leaving old installs with outdated positions, and
	///     default.json going missing entirely (e.g. deleted by hand - Save()'s cleanup only ever
	///     removes files, never restores one). Cheap to recompute, so this always overwrites rather than
	///     checking whether anything actually changed.
	/// </summary>
	private static void RefreshBuiltInDefault(List<OverlayPreset> presets, int width, int height)
	{
		var index = presets.FindIndex(p => p.Id == DefaultPresetId);
		var fresh = OverlayPreset.CreateDefault(DefaultPresetId, "Default", width, height);

		if (index < 0)
		{
			// Wasn't just stale, it was actually missing from disk - write it back immediately so the
			// fix survives even if the user closes the app without triggering any other Save() first.
			// CreateDirectory covers the very first run too, where PresetsDir doesn't exist yet.
			presets.Insert(0, fresh);
			Directory.CreateDirectory(PresetsDir);
			AtomicFile.WriteAllText(PresetPath(fresh.Id), JsonSerializer.Serialize(fresh));
		}
		else
		{
			presets[index] = fresh;
		}
	}

	/// <summary>
	///     Backfills any widget type missing from a preset (see OverlayPreset.WithMissingDefaultsFilled)
	///     and, when that actually changed a preset, writes it straight back so the fix is permanent
	///     from this load onward rather than only sticking once the user happens to edit something.
	/// </summary>
	private static void BackfillMissingWidgetTypes(List<OverlayPreset> presets, int width, int height)
	{
		for (var i = 0; i < presets.Count; i++)
		{
			OverlayPreset original = presets[i];
			OverlayPreset filled = original.WithMissingDefaultsFilled(width, height);
			if (ReferenceEquals(original, filled)) continue;

			presets[i] = filled;
			AtomicFile.WriteAllText(PresetPath(filled.Id), JsonSerializer.Serialize(filled));
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
			AtomicFile.WriteAllText(PresetPath(preset.Id), JsonSerializer.Serialize(preset));

		var activeId = data.ActivePresetId ?? data.Presets[0].Id;
		OverlaySettingsStore.Save(OverlaySettingsStore.Load() with { ActivePresetId = activeId });
		File.Delete(LegacyStorePath);
	}

	private sealed record StoredData(List<OverlayPreset> Presets, string? ActivePresetId);
}
