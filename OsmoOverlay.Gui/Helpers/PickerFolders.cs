using System.Text.Json;
using Avalonia.Platform.Storage;
using OsmoOverlay.Core.Logging;

namespace OsmoOverlay.Gui;

/// <summary>
///     The folder each file picker opens in: the one that scenario's last pick came from (recordings, a project to open, a
///     project to save, music, a GPS track...), not the one the system dialog remembers for the whole program. Kept in
///     picker-folders.json beside settings.json; a folder that is gone falls back to the system's own choice.
/// </summary>
public static class PickerFolders
{
	private static readonly string StorePath = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OsmoOverlay", "picker-folders.json");

	private static readonly Lock Gate = new();
	private static Dictionary<string, string>? _folders;

	public static async Task<IReadOnlyList<IStorageFile>> OpenRememberedAsync(this IStorageProvider provider, string scenario, FilePickerOpenOptions options)
	{
		options.SuggestedStartLocation ??= await StartAsync(provider, scenario);
		IReadOnlyList<IStorageFile> files = await provider.OpenFilePickerAsync(options);
		if (files.Count > 0) Remember(scenario, files[0].TryGetLocalPath());
		return files;
	}

	public static async Task<IStorageFile?> SaveRememberedAsync(this IStorageProvider provider, string scenario, FilePickerSaveOptions options)
	{
		options.SuggestedStartLocation ??= await StartAsync(provider, scenario);
		IStorageFile? file = await provider.SaveFilePickerAsync(options);
		if (file is not null) Remember(scenario, file.TryGetLocalPath());
		return file;
	}

	private static async Task<IStorageFolder?> StartAsync(IStorageProvider provider, string scenario)
	{
		string? folder;
		lock (Gate) folder = Load().GetValueOrDefault(scenario);
		return folder is not null && Directory.Exists(folder) ? await provider.TryGetFolderFromPathAsync(folder) : null;
	}

	private static void Remember(string scenario, string? picked)
	{
		if (Path.GetDirectoryName(picked) is not { Length: > 0 } folder) return;

		lock (Gate)
		{
			Dictionary<string, string> folders = Load();
			if (folders.TryGetValue(scenario, out string? current) && current == folder) return;

			folders[scenario] = folder;
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
				File.WriteAllText(StorePath, JsonSerializer.Serialize(folders));
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				AppLogger.Warn(ex, $"Can't save {StorePath}");
			}
		}
	}

	private static Dictionary<string, string> Load()
	{
		if (_folders is not null) return _folders;

		try
		{
			if (File.Exists(StorePath)) _folders = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(StorePath));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
		{
			AppLogger.Warn(ex, $"Can't read {StorePath}");
		}

		return _folders ??= [];
	}
}
