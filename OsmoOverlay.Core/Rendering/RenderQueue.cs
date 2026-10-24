using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Localization;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Core;

/// <summary>What to do when a render's output file is already there.</summary>
public enum ExistingOutput
{
	/// <summary>Leave the file alone and go on to the next render.</summary>
	Skip,

	/// <summary>Write over it.</summary>
	Overwrite,

	/// <summary>Write beside it, as "name (2).mp4".</summary>
	Number
}

public enum QueueState
{
	Waiting,
	Rendering,
	Done,
	Failed,
	Skipped,
	Cancelled
}

/// <summary>
///     One render to queue: a saved project, and what it should be rendered with besides what the project holds. Output is
///     the project's own path when null; ProfileId an ExportProfile's (null: the settings as they are); Alpha renders the
///     transparent overlay alone instead of the video. Name is what the render is called in the queue - the project's, since the
///     ProjectPath is the queue's own copy of it (QueueDetails falls back to the first recording's name).
/// </summary>
public sealed record QueueRequest(string ProjectPath, string? Output = null, string? ProfileId = null, AlphaCodec? Alpha = null, string? Name = null);

/// <summary>A request in the queue and how far it got. Changes are reported by RenderQueue.ItemChanged.</summary>
public sealed class QueueItem(QueueRequest request)
{
	public QueueRequest Request { get; } = request;

	public QueueState State { get; internal set; } = QueueState.Waiting;

	/// <summary>Why it failed or was skipped, or the render's latest status line.</summary>
	public string Message { get; internal set; } = "";

	/// <summary>The file it writes (or wrote) - known once it starts.</summary>
	public string? OutputPath { get; internal set; }

	/// <summary>0 to 1 while rendering.</summary>
	public double Progress { get; internal set; }

	public TimeSpan Elapsed { get; internal set; }

	public bool IsFinished => State is QueueState.Done or QueueState.Failed or QueueState.Skipped or QueueState.Cancelled;
}

/// <summary>
///     Renders waiting projects one after another - what the render queue window and `--project` with several projects run. A
///     render that fails is noted and the queue goes on to the next; cancelling stops it and leaves the rest waiting. Items may
///     be added, removed and moved while it runs: it takes the next waiting one each time.
/// </summary>
public sealed class RenderQueue
{
	/// <summary>What a queue needs from the world, replaced in tests: the render itself, the probe of a project's recordings, the files.</summary>
	public sealed record Services(
		Func<RenderOptions, IProgress<RenderStatus>?, CancellationToken, Task<RenderResult>> Render,
		Func<OverlayProject, ProjectRender.Timeline> Probe,
		Func<OverlaySettings> LoadSettings,
		Func<IReadOnlyList<SettingsPreset>> LoadSettingsPresets,
		Func<string, bool> FileExists)
	{
		public static Services Default { get; } = new(RenderJob.RunAsync, ProjectRender.Probe, OverlaySettingsStore.Load,
			() => SettingsPresetStore.Load().Presets, File.Exists);
	}

	private readonly Lock _gate = new();
	private readonly List<QueueItem> _items = [];
	private readonly Services _services;

	public RenderQueue(Services? services = null)
	{
		_services = services ?? Services.Default;
	}

	/// <summary>An item's state, progress or message changed. Raised on whatever thread the render reports from.</summary>
	public event Action<QueueItem>? ItemChanged;

	/// <summary>Items were added, removed, moved or cleared.</summary>
	public event Action? ListChanged;

	public bool IsRunning { get; private set; }

	public IReadOnlyList<QueueItem> Items
	{
		get
		{
			lock (_gate) return [.. _items];
		}
	}

	public QueueItem Add(QueueRequest request)
	{
		var item = new QueueItem(request);
		lock (_gate) _items.Add(item);
		ListChanged?.Invoke();
		return item;
	}

	/// <summary>Takes a waiting or finished item out; one being rendered stays.</summary>
	public bool Remove(QueueItem item)
	{
		lock (_gate)
		{
			if (item.State == QueueState.Rendering || !_items.Remove(item)) return false;
		}

		ListChanged?.Invoke();
		return true;
	}

	/// <summary>Moves a waiting item up (-1) or down (+1) past the other waiting ones.</summary>
	public bool Move(QueueItem item, int direction)
	{
		lock (_gate)
		{
			int index = _items.IndexOf(item);
			if (index < 0 || item.State != QueueState.Waiting) return false;

			int step = Math.Sign(direction);
			int target = index + step;
			while (target >= 0 && target < _items.Count && _items[target].State != QueueState.Waiting) target += step;
			if (target < 0 || target >= _items.Count) return false;

			_items.RemoveAt(index);
			_items.Insert(target, item);
		}

		ListChanged?.Invoke();
		return true;
	}

	/// <summary>Takes out everything that's done, failed, skipped or cancelled.</summary>
	public void ClearFinished()
	{
		lock (_gate) _items.RemoveAll(i => i.IsFinished);
		ListChanged?.Invoke();
	}

	/// <summary>Puts a cancelled or failed item back to wait for the next run.</summary>
	public bool Retry(QueueItem item)
	{
		lock (_gate)
		{
			if (item.State is not (QueueState.Failed or QueueState.Cancelled or QueueState.Skipped)) return false;

			item.State = QueueState.Waiting;
			item.Message = "";
			item.Progress = 0;
		}

		ItemChanged?.Invoke(item);
		return true;
	}

	/// <summary>Renders the waiting items in order until none is left (or `ct` cancels - the item rendering then is Cancelled, the rest still wait).</summary>
	public async Task RunAsync(ExistingOutput existing, CancellationToken ct)
	{
		lock (_gate)
		{
			if (IsRunning) throw new InvalidOperationException("The queue is already running.");
			IsRunning = true;
		}

		try
		{
			// Read once: a setting changed while the queue runs doesn't change the renders after it, which would be half one way, half the other.
			OverlaySettings current = _services.LoadSettings();
			IReadOnlyList<SettingsPreset> presets = _services.LoadSettingsPresets();
			while (!ct.IsCancellationRequested && TakeNext() is { } item)
			{
				await RenderAsync(item, existing, current, presets, ct);
				if (item.State == QueueState.Cancelled) break;
			}
		}
		finally
		{
			lock (_gate) IsRunning = false;
		}
	}

	private QueueItem? TakeNext()
	{
		lock (_gate)
		{
			QueueItem? next = _items.FirstOrDefault(i => i.State == QueueState.Waiting);
			if (next is not null) next.State = QueueState.Rendering;
			return next;
		}
	}

	private void Update(QueueItem item, QueueState state, string message, TimeSpan? elapsed = null, double? progress = null)
	{
		lock (_gate)
		{
			item.State = state;
			item.Message = message;
			if (elapsed is { } time) item.Elapsed = time;
			if (progress is { } done) item.Progress = done;
		}

		ItemChanged?.Invoke(item);
	}

	private async Task RenderAsync(QueueItem item, ExistingOutput existing, OverlaySettings current, IReadOnlyList<SettingsPreset> presets, CancellationToken ct)
	{
		Update(item, QueueState.Rendering, "");
		try
		{
			if (!_services.FileExists(item.Request.ProjectPath))
			{
				Update(item, QueueState.Failed, string.Format(CoreStrings.Queue_ProjectNotFound, item.Request.ProjectPath));
				return;
			}

			OverlayProject project = OverlayProject.Load(item.Request.ProjectPath);
			if (ProjectRender.MissingFiles(project, _services.FileExists).Where(p => project.InputPaths.Contains(p)).ToList() is { Count: > 0 } missing)
			{
				Update(item, QueueState.Failed, string.Format(CoreStrings.Queue_RecordingsMissing, string.Join(", ", missing.Select(Path.GetFileName))));
				return;
			}

			OverlaySettings settings = ProjectRender.SettingsFor(project, current, presets);
			if (item.Request.ProfileId is { } profileId)
			{
				if (ExportProfile.Find(profileId) is not { } profile)
				{
					Update(item, QueueState.Failed, string.Format(CoreStrings.Queue_ProfileUnknown, profileId));
					return;
				}

				settings = profile.ApplyTo(settings);
			}

			string requested = item.Request.Output ?? ProjectRender.OutputPathFor(project, settings);
			if (item.Request.Alpha is { } alpha) requested = RenderOptions.AlphaOutputPath(requested, alpha);
			string? output = OutputFiles.Resolve(requested, existing, _services.FileExists);
			item.OutputPath = output ?? requested;
			if (output is null)
			{
				Update(item, QueueState.Skipped, string.Format(CoreStrings.Queue_OutputExists, Path.GetFileName(requested)));
				return;
			}

			ProjectRender.Timeline timeline = _services.Probe(project);
			RenderOptions options = ProjectRender.Options(project, timeline, output, settings, _services.FileExists) with
			{
				// Resolve already chose a path that may be written (a free one, or the one to write over).
				Overwrite = existing == ExistingOutput.Overwrite,
				Alpha = item.Request.Alpha
			};
			var progress = new Progress<RenderStatus>(status =>
			{
				// Progress<T> delivers on another thread: a report can arrive after the render has ended and set its final state.
				lock (_gate)
				{
					if (item.State != QueueState.Rendering) return;

					if (status.TotalFrames > 0) item.Progress = Math.Clamp((double)status.CurrentFrame / status.TotalFrames, 0, 1);
					item.Message = status.Message;
				}

				ItemChanged?.Invoke(item);
			});

			RenderResult result = await _services.Render(options, progress, ct);
			if (result.Success)
				Update(item, QueueState.Done, "", result.Elapsed, 1);
			else if (ct.IsCancellationRequested)
				Update(item, QueueState.Cancelled, CoreStrings.Render_Cancelled, result.Elapsed);
			else
				Update(item, QueueState.Failed, result.ErrorMessage ?? CoreStrings.Queue_Failed, result.Elapsed);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			AppLogger.Error(ex, string.Format(CoreStrings.Queue_ItemFailed, item.Request.ProjectPath, ex.Message));
			Update(item, QueueState.Failed, ex.Message);
		}
		catch (OperationCanceledException)
		{
			Update(item, QueueState.Cancelled, CoreStrings.Render_Cancelled);
		}
	}
}

/// <summary>Where a render may write, given what is there already.</summary>
public static class OutputFiles
{
	private const int MaxNumber = 10_000;

	/// <summary>The path to write `path`'s render to under `existing`, or null to skip it (the file is there and mustn't be touched).</summary>
	public static string? Resolve(string path, ExistingOutput existing, Func<string, bool>? exists = null)
	{
		exists ??= File.Exists;
		if (!exists(path)) return path;

		switch (existing)
		{
			case ExistingOutput.Overwrite:
				return path;
			case ExistingOutput.Skip:
				return null;
			default:
				string dir = Path.GetDirectoryName(path) ?? "";
				string name = Path.GetFileNameWithoutExtension(path);
				string extension = Path.GetExtension(path);
				for (int n = 2; n < MaxNumber; n++)
				{
					string candidate = Path.Combine(dir, $"{name} ({n}){extension}");
					if (!exists(candidate)) return candidate;
				}

				throw new IOException(CoreStrings.Queue_NoFreeName);
		}
	}
}
