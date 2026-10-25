using System.Text.Json;
using Avalonia.Interactivity;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Gui.Native;

namespace OsmoOverlay.Gui;

/// <summary>
///     The render queue (RenderQueue): projects added from here are copied into the queue's folder (QueueStore) and rendered one
///     after another, from the queue window. The queue is run here, not by that window, so it goes on when the window is closed.
///     It and a render of the main window never run together - both want the whole machine.
/// </summary>
public partial class MainWindow
{
	private readonly RenderQueue _queue = new();
	private readonly QueueStore _queueStore = new();
	private QueueWindow? _queueWindow;
	private CancellationTokenSource? _queueCts;

	internal RenderQueue Queue => _queue;

	internal bool IsQueueRunning => _queue.IsRunning;

	/// <summary>True from the moment Start is pressed, not only once the queue's own thread is going.</summary>
	private bool QueueBusy => _queue.IsRunning || _queueCts is not null;

	private void WireQueue()
	{
		// What was waiting when the app last closed (a render that was running then waits again).
		foreach (QueueRequest request in _queueStore.Load()) _queue.Add(request);
		_queue.ListChanged += SaveQueue;
		_queue.ListChanged += RequestQueueButtonUpdate;
		_queue.ItemChanged += _ => RequestQueueButtonUpdate();
		UpdateQueueButton();
	}

	private void RequestQueueButtonUpdate()
	{
		Avalonia.Threading.Dispatcher.UIThread.Post(UpdateQueueButton);
	}

	/// <summary>"Queue (3)": a queue that outlives the app is easy to forget, so what waits in it shows on the button.</summary>
	private void UpdateQueueButton()
	{
		int waiting = _queue.Items.Count(i => i.State is QueueState.Waiting or QueueState.Rendering);
		QueueOpenButton.Content = waiting > 0 ? $"{Strings.Main_Queue} ({waiting})" : Strings.Main_Queue;
		UpdateExportProfileEnabled();
	}

	private void SaveQueue()
	{
		_queueStore.Save(_queue.Items.Where(i => i.State is not (QueueState.Done or QueueState.Skipped)).Select(i => i.Request));
	}

	private string ProjectDisplayName()
	{
		return Path.GetFileNameWithoutExtension(_projectPath ?? _summary?.InputPaths.FirstOrDefault() ?? "");
	}

	private void OnAddToQueueClick(object? sender, RoutedEventArgs e)
	{
		if (_phase != UiPhase.SummaryReady || _summary is null || !CanRenderTelemetry) return;

		if (string.IsNullOrWhiteSpace(OutputPathBox.Text))
		{
			AppendLog(Strings.Render_EnterOutputPath);
			return;
		}

		try
		{
			string path = _queueStore.Snapshot(BuildProject());
			_queue.Add(new QueueRequest(path, Name: ProjectDisplayName()));
			AppendLog(string.Format(Strings.Queue_Added, Path.GetFileName(OutputPathBox.Text)));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			AppLogger.Error(ex, string.Format(Strings.Queue_AddFailed, ex.Message));
		}
	}

	/// <summary>Queues saved project files, each as a copy of what it holds now. Returns the ones that couldn't be queued, each with why.</summary>
	internal List<string> QueueProjectFiles(IEnumerable<string> paths)
	{
		List<string> problems = [];
		foreach (string path in paths)
		{
			try
			{
				_queue.Add(new QueueRequest(_queueStore.Import(path), Name: Path.GetFileNameWithoutExtension(path)));
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
			{
				problems.Add($"{Path.GetFileName(path)}: {ex.Message}");
			}
		}

		return problems;
	}

	/// <summary>
	///     Queues recordings that were never opened here: joined, as one recording (a camera's split files), or each file as a render
	///     of its own - with the settings and overlay in use, no edits. Returns what couldn't be queued, each with why.
	/// </summary>
	internal List<string> QueueRecordings(IReadOnlyList<string> recordings, bool joined)
	{
		OverlaySettings settings = OverlaySettingsStore.Load();
		List<string> problems = [];
		foreach (IReadOnlyList<string> job in joined ? [recordings] : recordings.Select(path => (IReadOnlyList<string>)[path]))
		{
			try
			{
				_queue.Add(new QueueRequest(_queueStore.Snapshot(ProjectRender.ForRecordings(job, settings)), Name: Path.GetFileNameWithoutExtension(job[0])));
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				problems.Add($"{Path.GetFileName(job[0])}: {ex.Message}");
			}
		}

		return problems;
	}

	private void OnOpenQueueClick(object? sender, RoutedEventArgs e)
	{
		OpenQueueWindow();
	}

	internal void OpenQueueWindow()
	{
		if (_queueWindow is { } open)
		{
			open.Activate();
			return;
		}

		_queueWindow = new QueueWindow(this);
		_queueWindow.Closed += (_, _) => _queueWindow = null;
		_queueWindow.Show(this);
	}

	internal async Task StartQueueAsync(ExistingOutput existing)
	{
		if (QueueBusy) return;

		if (_phase == UiPhase.Rendering || _renderStarting)
		{
			await ConfirmDialog.ShowAsync(this, Strings.Queue_Title, Strings.Queue_RenderBusy, kind: DialogKind.Warning);
			return;
		}

		_previewPlayer.Pause();
		_queueCts = new CancellationTokenSource();
		TaskbarProgress.SetState(this, TaskbarProgress.State.Normal);
		_queueWindow?.RunningChanged();
		CancellationTokenSource cts = _queueCts;
		UpdateExportProfileEnabled();
		try
		{
			_queue.ItemChanged += OnQueueItemProgress;
			await Task.Run(() => _queue.RunAsync(existing, cts.Token));
		}
		finally
		{
			_queue.ItemChanged -= OnQueueItemProgress;
			_queueCts = null;
			TaskbarProgress.SetState(this, TaskbarProgress.State.NoProgress);
			SaveQueue();
			_queueWindow?.RunningChanged();
			UpdateExportProfileEnabled();
		}

		if (cts.IsCancellationRequested) return;

		IReadOnlyList<QueueItem> items = _queue.Items;
		int done = items.Count(i => i.State == QueueState.Done);
		int rest = items.Count(i => i.State is QueueState.Failed or QueueState.Skipped);
		await NotifyRenderFinishedAsync(Strings.Queue_FinishedTitle, string.Format(Strings.Queue_FinishedMessage, done, rest),
			rest == 0 ? DialogKind.Success : DialogKind.Warning);
	}

	internal void StopQueue()
	{
		_queueCts?.Cancel();
	}

	private void OnQueueItemProgress(QueueItem item)
	{
		if (item.State != QueueState.Rendering || item.Progress <= 0) return;

		Avalonia.Threading.Dispatcher.UIThread.Post(() => TaskbarProgress.SetValue(this, (ulong)(item.Progress * 1000), 1000));
	}
}
