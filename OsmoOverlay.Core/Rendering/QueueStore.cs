using System.Text.Json;
using OsmoOverlay.Core.Localization;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Core;

/// <summary>
///     The render queue kept on disk so it outlives the app: each queued render is a copy of the project as it was when it was queued
///     (a later edit or save of the project doesn't change what is rendered), and queue.json lists what is still to render.
///     Best-effort like the other stores - a queue that can't be read is an empty one.
/// </summary>
public sealed class QueueStore(string? directory = null)
{
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	private readonly string _directory = directory ?? Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OsmoOverlay", "queue");

	private string ListPath => Path.Combine(_directory, "queue.json");

	public string Snapshot(OverlayProject project)
	{
		Directory.CreateDirectory(_directory);
		string path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + OverlayProject.Extension);
		project.Save(path);
		return path;
	}

	/// <summary>
	///     Queues a project file as it is now: read (so a file that isn't a project, is from a newer version or points off the local drives
	///     is refused here, not when its turn comes) and copied like any other queued project. Throws what OverlayProject.Load throws.
	/// </summary>
	public string Import(string projectPath)
	{
		return Snapshot(OverlayProject.Load(projectPath));
	}

	/// <summary>Remembers `requests` as what is left to render, and removes the copies nothing refers to any more.</summary>
	public void Save(IEnumerable<QueueRequest> requests)
	{
		List<QueueRequest> list = [.. requests];
		try
		{
			Directory.CreateDirectory(_directory);
			AtomicFile.WriteAllText(ListPath, JsonSerializer.Serialize(list, JsonOptions));
			DeleteUnreferenced(list);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			AppLogger.Warn(ex, string.Format(CoreStrings.Queue_SaveFailed, ex.Message));
		}
	}

	public List<QueueRequest> Load()
	{
		try
		{
			if (!File.Exists(ListPath)) return [];

			List<QueueRequest> list = JsonSerializer.Deserialize<List<QueueRequest>>(File.ReadAllText(ListPath)) ?? [];
			return [.. list.Where(r => !string.IsNullOrWhiteSpace(r.ProjectPath) && OverlayPresetStore.IsLocalFilePath(r.ProjectPath) && File.Exists(r.ProjectPath))];
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
		{
			AppLogger.Warn(ex, string.Format(CoreStrings.Queue_LoadFailed, ex.Message));
			return [];
		}
	}

	/// <summary>Deletes the queue's own copies that no request points at. A project the user saved elsewhere is never touched.</summary>
	private void DeleteUnreferenced(IReadOnlyList<QueueRequest> keep)
	{
		HashSet<string> kept = [.. keep.Select(r => Path.GetFullPath(r.ProjectPath))];
		foreach (string file in Directory.EnumerateFiles(_directory, "*" + OverlayProject.Extension))
		{
			if (!kept.Contains(Path.GetFullPath(file))) TryDelete(file);
		}
	}

	private static void TryDelete(string file)
	{
		try
		{
			File.Delete(file);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			AppLogger.Info($"Queue copy not deleted ({file}): {ex.Message}");
		}
	}
}
