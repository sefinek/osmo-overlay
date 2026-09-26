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

	// Preset files this process has successfully loaded or written, with the JSON last seen on disk for
	// each - the only files Save may delete, and lets WritePreset skip rewriting a preset that didn't
	// change (Save runs on every single settings-field edit, for every preset).
	private static readonly Dictionary<string, string> KnownPresetFiles = new(StringComparer.OrdinalIgnoreCase);

	public static (List<OverlayPreset> Presets, string ActivePresetId) Load(int width, int height)
	{
		try
		{
			List<OverlayPreset> presets = LoadPresetFiles();
			RefreshBuiltInDefault(presets, width, height);
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
			// The fallback below isn't what's on disk - don't let the next Save treat it as authoritative.
			KnownPresetFiles.Clear();
		}

		var defaultPreset = OverlayPreset.CreateDefault(DefaultPresetId, "Default", width, height);
		return ([defaultPreset], defaultPreset.Id);
	}

	public static void Save(List<OverlayPreset> presets, string activePresetId)
	{
		try
		{
			Directory.CreateDirectory(PresetsDir);

			foreach (OverlayPreset preset in presets)
				WritePreset(preset);

			// Only files this store itself loaded or wrote are candidates for removal - a preset file that
			// failed to parse (hand-edited, written by a newer version with an unknown widget type) was
			// never in `presets` to begin with, and must not be silently wiped by the next unrelated edit.
			HashSet<string> keepFiles = presets.Select(p => PresetPath(p.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
			foreach (var file in KnownPresetFiles.Keys.Where(f => !keepFiles.Contains(f)).ToList())
			{
				File.Delete(file);
				KnownPresetFiles.Remove(file);
			}

			OverlaySettings settings = OverlaySettingsStore.Load();
			if (settings.ActivePresetId != activePresetId)
				OverlaySettingsStore.Save(settings with { ActivePresetId = activePresetId });
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
			WritePreset(fresh);
		}
		else
		{
			presets[index] = fresh;
		}
	}

	/// <summary>Serializes one preset to an arbitrary file the user picked, so it can be shared/sent to someone else.</summary>
	public static void ExportToFile(OverlayPreset preset, string filePath)
	{
		AtomicFile.WriteAllText(filePath, JsonSerializer.Serialize(preset));
	}

	/// <summary>Deserializes a preset from a file (e.g. one received from someone else) - null on any read/parse failure, logged the same way a corrupt local preset file is in LoadPresetFiles.</summary>
	public static OverlayPreset? ImportFromFile(string filePath)
	{
		try
		{
			return Validate(JsonSerializer.Deserialize<OverlayPreset>(File.ReadAllText(filePath)));
		}
		catch (Exception ex)
		{
			AppLogger.Warn(ex, $"Failed to import overlay preset from: {filePath}");
			return null;
		}
	}

	private static void WritePreset(OverlayPreset preset)
	{
		var path = PresetPath(preset.Id);
		var json = JsonSerializer.Serialize(preset);
		if (KnownPresetFiles.TryGetValue(path, out var onDisk) && onDisk == json) return;

		AtomicFile.WriteAllText(path, json);
		KnownPresetFiles[path] = json;
	}

	private static string PresetPath(string id)
	{
		return Path.Combine(PresetsDir, $"{id}.json");
	}

	private static List<OverlayPreset> LoadPresetFiles()
	{
		KnownPresetFiles.Clear();
		List<OverlayPreset> presets = [];
		if (!Directory.Exists(PresetsDir)) return presets;

		foreach (var file in Directory.EnumerateFiles(PresetsDir, "*.json"))
			try
			{
				var json = File.ReadAllText(file);
				OverlayPreset preset = Validate(JsonSerializer.Deserialize<OverlayPreset>(json));
				if (presets.Any(p => p.Id == preset.Id))
				{
					AppLogger.Warn($"Skipping overlay preset file with a duplicate id '{preset.Id}': {file}");
					continue;
				}

				presets.Add(preset);
				KnownPresetFiles[file] = json;
			}
			catch (Exception ex)
			{
				// One corrupt preset file shouldn't take the rest of the presets down with it.
				AppLogger.Warn(ex, $"Skipping unreadable overlay preset file: {file}");
			}

		return presets;
	}

	/// <summary>
	///     Valid JSON can still deserialize into a preset the rest of the app can't handle (null Elements,
	///     a null entry in Elements, an element without an Id, missing Id/Name) - reject it here so it's
	///     skipped like any other unreadable file instead of throwing later from the GUI. Id also becomes
	///     a file name (PresetPath), so anything that isn't a plain file name is rejected too.
	/// </summary>
	private static OverlayPreset Validate(OverlayPreset? preset)
	{
		if (preset is null) throw new InvalidDataException("Preset file is empty (null).");
		if (preset.Elements is null || preset.Elements.Any(e => e is null))
			throw new InvalidDataException("Preset has no Elements list or contains a null element.");
		if (preset.Elements.Any(e => string.IsNullOrEmpty(e.Id)))
			throw new InvalidDataException("Preset contains an element without an Id.");
		if (string.IsNullOrWhiteSpace(preset.Id) || preset.Id is "." or ".." ||
		    preset.Id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
			throw new InvalidDataException($"Preset id '{preset.Id}' is not a valid file name.");

		List<OverlayElement> elements =
		[
			.. preset.Elements.Select(e => e.Scale is >= OverlayElementBounds.MinElementScale and <= OverlayElementBounds.MaxElementScale
				? e
				: e with { Scale = float.IsFinite(e.Scale) ? Math.Clamp(e.Scale, OverlayElementBounds.MinElementScale, OverlayElementBounds.MaxElementScale) : 1f })
		];

		List<OverlayLayer>? layers = preset.Layers?.Where(l => l is not null && !string.IsNullOrEmpty(l.Id)).ToList();
		return preset with { Name = preset.Name ?? preset.Id, Elements = elements, Layers = layers };
	}
}
