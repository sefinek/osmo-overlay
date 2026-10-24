using System.Text.Json;
using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Localization;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Core.Overlay;

/// <summary>
///     Persists the settings presets (SettingsPreset), one file each under SettingsPresetsDir named by its id, like the overlay
///     presets. Which one is active is OverlaySettings.ActiveSettingsPresetId; the settings themselves stay where they are,
///     so nothing that reads settings.json knows about presets. Switching copies a preset's values into the settings
///     (Activate), and every settings save copies them back into the active preset (Sync).
/// </summary>
public static class SettingsPresetStore
{
	/// <summary>
	///     The preset made from the settings the first time, and always there: it can't be renamed, deleted or given a layout, so
	///     there is one baseline to come back to - like the overlay's own Default preset.
	/// </summary>
	public const string DefaultId = "default";

	public const string DefaultName = "Default";

	private static readonly string StoreDir = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OsmoOverlay");

	private static string SettingsPresetsDir => Path.Combine(StoreDir, "settings-presets");

	// The preset and values the last Sync found already in its file - most saves change nothing a preset holds, and then
	// there is no file to read.
	private static readonly Lock SyncGate = new();
	private static string? _syncedId;
	private static string? _syncedValues;

	private static readonly string[] NvencPresets = ["p4", "p5", "p6", "p7"];

	/// <summary>
	///     Every preset (Default first, then by name) and the active one. Default is made from the settings as they are when it
	///     is missing, so nothing changes for someone who never opens this: their settings become the "Default" preset.
	/// </summary>
	public static (List<SettingsPreset> Presets, string ActiveId) Load()
	{
		List<SettingsPreset> presets = ReadAll();
		OverlaySettings settings = OverlaySettingsStore.Load();
		if (presets.All(p => p.Id != DefaultId))
		{
			var first = new SettingsPreset(DefaultId, DefaultName, SettingsPresetValues.From(settings));
			Write(first);
			presets.Insert(0, first);
		}

		string activeId = presets.Any(p => p.Id == settings.ActiveSettingsPresetId) ? settings.ActiveSettingsPresetId! : presets[0].Id;
		if (settings.ActiveSettingsPresetId != activeId) OverlaySettingsStore.Save(settings with { ActiveSettingsPresetId = activeId });
		return (presets, activeId);
	}

	/// <summary>A new preset with the default values and the overlay layout currently in use; saved, not yet active.</summary>
	public static SettingsPreset Create(string name)
	{
		var preset = new SettingsPreset(NewId(), name, new SettingsPresetValues()) { LayoutPresetId = OverlaySettingsStore.Load().ActivePresetId };
		Write(preset);
		return preset;
	}

	/// <summary>A copy of the preset under another name and id, saved.</summary>
	public static SettingsPreset Duplicate(SettingsPreset source, string name)
	{
		SettingsPreset copy = source with { Id = NewId(), Name = name };
		Write(copy);
		return copy;
	}

	/// <summary>Changes one preset as it is on disk now - the values of the active one are kept up to date there, so a copy held for a while is stale.</summary>
	public static void Modify(string id, Func<SettingsPreset, SettingsPreset> change)
	{
		if (ReadAll().FirstOrDefault(p => p.Id == id) is { } preset) Write(change(preset));
	}

	public static void Delete(string id)
	{
		if (id == DefaultId) return;

		try
		{
			lock (SyncGate)
			{
				if (_syncedId == id) _syncedId = null;
			}

			File.Delete(PathOf(id));
		}
		catch (Exception ex)
		{
			AppLogger.Warn(ex, CoreStrings.SettingsPresets_WriteFailed);
		}
	}

	/// <summary>
	///     The settings with this preset's values in, saved, and the preset marked active. Its overlay layout becomes the active one
	///     when it still exists. Whatever was edited in the settings until now is already in the previous preset (Sync).
	/// </summary>
	public static OverlaySettings Activate(SettingsPreset preset)
	{
		OverlaySettings settings = preset.Values.ApplyTo(OverlaySettingsStore.Load()) with { ActiveSettingsPresetId = preset.Id };
		if (preset.LayoutPresetId is { } layout && OverlayPresetStore.Load().Presets.Any(p => p.Id == layout))
			settings = settings with { ActivePresetId = layout };

		OverlaySettingsStore.Save(settings);
		return settings;
	}

	/// <summary>
	///     Called by OverlaySettingsStore.Save: the active preset takes the values the settings hold now. A preset that was deleted
	///     is not brought back, and a file that did not change is not written.
	/// </summary>
	internal static void Sync(OverlaySettings settings)
	{
		if (settings.ActiveSettingsPresetId is not { } id) return;

		string values = JsonSerializer.Serialize(SettingsPresetValues.From(settings));
		lock (SyncGate)
		{
			if (_syncedId == id && _syncedValues == values) return;
		}

		try
		{
			string path = PathOf(id);
			if (!File.Exists(path)) return;

			string json = File.ReadAllText(path);
			if (JsonSerializer.Deserialize<SettingsPreset>(json) is not { } preset) return;

			string updated = JsonSerializer.Serialize(preset with { Values = SettingsPresetValues.From(settings) });
			if (updated != json) AtomicFile.WriteAllText(path, updated);
			lock (SyncGate)
			{
				_syncedId = id;
				_syncedValues = values;
			}
		}
		catch (Exception ex)
		{
			AppLogger.Warn(ex, CoreStrings.SettingsPresets_WriteFailed);
		}
	}

	public static void ExportToFile(SettingsPreset preset, string filePath)
	{
		AtomicFile.WriteAllText(filePath, JsonSerializer.Serialize(preset with { LayoutPresetId = null }));
	}

	/// <summary>
	///     A preset from a file someone sent, with a new id and values brought into the ranges the settings allow; null on any
	///     failure (logged). Its overlay layout is not taken - an id means nothing on another computer.
	/// </summary>
	public static SettingsPreset? ImportFromFile(string filePath)
	{
		try
		{
			SettingsPreset? imported = JsonSerializer.Deserialize<SettingsPreset>(File.ReadAllText(filePath));
			if (imported?.Values is not { } values) throw new InvalidDataException("Preset file is empty or has no values.");

			SettingsPreset preset = imported with
			{
				Id = NewId(),
				Name = string.IsNullOrWhiteSpace(imported.Name) ? Path.GetFileNameWithoutExtension(filePath) : imported.Name.Trim(),
				Values = Sanitize(values),
				LayoutPresetId = null
			};
			Write(preset);
			return preset;
		}
		catch (Exception ex)
		{
			AppLogger.Warn(ex, string.Format(CoreStrings.SettingsPresets_ImportFailed, filePath));
			return null;
		}
	}

	internal static SettingsPresetValues Sanitize(SettingsPresetValues values)
	{
		return values with
		{
			SpeedCorrectionPercent = SpeedCalibration.Clamp(values.SpeedCorrectionPercent),
			RouteIntroDurationSeconds = double.IsFinite(values.RouteIntroDurationSeconds) ? Math.Clamp(values.RouteIntroDurationSeconds, 1, 120) : 12,
			NvencPreset = NvencPresets.Contains(values.NvencPreset) ? values.NvencPreset : "p7",
			OutputBitrateMultiplier = double.IsFinite(values.OutputBitrateMultiplier) ? Math.Clamp(values.OutputBitrateMultiplier, 1, 4) : 1,
			OutputResolution = values.OutputResolution is { } r && OutputVideo.Resolutions.Contains(r) ? r : null,
			OutputCodec = values.OutputCodec is "h264" or "hevc" ? values.OutputCodec : null
		};
	}

	private static List<SettingsPreset> ReadAll()
	{
		List<SettingsPreset> presets = [];
		if (!Directory.Exists(SettingsPresetsDir)) return presets;

		foreach (string file in Directory.EnumerateFiles(SettingsPresetsDir, "*.json"))
		{
			try
			{
				SettingsPreset? preset = JsonSerializer.Deserialize<SettingsPreset>(File.ReadAllText(file));
				if (preset?.Values is null || string.IsNullOrWhiteSpace(preset.Id) || !SamePath(PathOf(preset.Id), file))
					throw new InvalidDataException("Preset has no values or its id does not match the file name.");

				presets.Add(preset with { Name = string.IsNullOrWhiteSpace(preset.Name) ? preset.Id : preset.Name });
			}
			catch (Exception ex)
			{
				AppLogger.Warn(ex, string.Format(CoreStrings.SettingsPresets_SkippingUnreadable, file));
			}
		}

		presets.Sort((a, b) => a.Id == DefaultId ? -1 : b.Id == DefaultId ? 1 : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
		return presets;
	}

	private static void Write(SettingsPreset preset)
	{
		try
		{
			Directory.CreateDirectory(SettingsPresetsDir);
			AtomicFile.WriteAllText(PathOf(preset.Id), JsonSerializer.Serialize(preset));
		}
		catch (Exception ex)
		{
			AppLogger.Warn(ex, CoreStrings.SettingsPresets_WriteFailed);
		}
	}

	private static bool SamePath(string a, string b)
	{
		return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
	}

	private static string NewId()
	{
		return Guid.NewGuid().ToString("N");
	}

	private static string PathOf(string id)
	{
		return Path.Combine(SettingsPresetsDir, $"{id}.json");
	}
}
