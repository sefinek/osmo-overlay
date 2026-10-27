using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Localization;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Gui;

/// <summary>
///     The render queue (RenderQueue): the projects waiting to be rendered one after another, each named as the project is and
///     opening to the recordings it renders, with their progress. It only shows and edits the queue; running it belongs to the
///     main window (StartQueueAsync), so closing this window stops nothing.
/// </summary>
public partial class QueueWindow : Window
{
	private static readonly ChoiceOption<ExistingOutput>[] ExistingOptions =
	[
		new(Strings.Queue_ExistsSkip, ExistingOutput.Skip),
		new(Strings.Queue_ExistsOverwrite, ExistingOutput.Overwrite),
		new(Strings.Queue_ExistsNumber, ExistingOutput.Number)
	];

	private static readonly FilePickerFileType ProjectFileType = new(Strings.Project_FileType) { Patterns = [$"*{OverlayProject.Extension}"] };

	private readonly MainWindow? _main;
	private readonly RenderQueue _queue;
	private readonly Dictionary<QueueItem, QueueRow> _rows = [];
	private readonly Dictionary<string, QueueDetails> _details = [];
	private readonly Lock _gate = new();
	private bool _refreshPending;

	public QueueWindow() : this(null)
	{
	}

	public QueueWindow(MainWindow? main)
	{
		InitializeComponent();
		_main = main;
		_queue = main?.Queue ?? new RenderQueue();
		ExistingCombo.ItemsSource = ExistingOptions;
		ExistingCombo.SelectedIndex = 0;

		_queue.ListChanged += RequestRefresh;
		_queue.ItemChanged += OnItemChanged;
		Closed += (_, _) =>
		{
			_queue.ListChanged -= RequestRefresh;
			_queue.ItemChanged -= OnItemChanged;
		};
		Refresh();
	}

	/// <summary>The main window started or stopped the queue.</summary>
	internal void RunningChanged()
	{
		Dispatcher.UIThread.Post(UpdateButtons);
	}

	private void OnItemChanged(QueueItem item)
	{
		RequestRefresh();
	}

	/// <summary>Reports come from the render's threads, many a second at times - one refresh of the screen is queued for all of them.</summary>
	private void RequestRefresh()
	{
		lock (_gate)
		{
			if (_refreshPending) return;

			_refreshPending = true;
		}

		Dispatcher.UIThread.Post(() =>
		{
			lock (_gate) _refreshPending = false;
			Refresh();
		}, DispatcherPriority.Background);
	}

	private async void OnStartClick(object? sender, RoutedEventArgs e)
	{
		if (_main is null) return;

		ExistingOutput existing = (ExistingCombo.SelectedItem as ChoiceOption<ExistingOutput>)?.Value ?? ExistingOutput.Skip;
		await _main.StartQueueAsync(existing);
	}

	private void OnStopClick(object? sender, RoutedEventArgs e)
	{
		_main?.StopQueue();
	}

	private async void OnAddProjectsClick(object? sender, RoutedEventArgs e)
	{
		if (_main is null) return;

		IReadOnlyList<IStorageFile> files = await StorageProvider.OpenRememberedAsync("project-open", new FilePickerOpenOptions
		{
			Title = Strings.Queue_PickProjects,
			AllowMultiple = true,
			FileTypeFilter = [ProjectFileType]
		});
		if (files.Count > 0) await ShowNotAddedAsync(_main.QueueProjectFiles(files.Select(f => f.Path.LocalPath)));
	}

	private async void OnAddRecordingsClick(object? sender, RoutedEventArgs e)
	{
		if (_main is null) return;

		IReadOnlyList<IStorageFile> files = await StorageProvider.OpenRememberedAsync("recordings", new FilePickerOpenOptions
		{
			Title = Strings.Queue_PickRecordings,
			AllowMultiple = true,
			FileTypeFilter =
			[
				new FilePickerFileType(Strings.Main_CameraRecordings) { Patterns = ["*.mp4", "*.MP4", "*.insv", "*.INSV", "*.lrv", "*.LRV"] },
				new FilePickerFileType(Strings.Common_AllFiles) { Patterns = ["*"] }
			]
		});
		if (files.Count == 0) return;

		List<string> paths = [.. files.Select(f => f.Path.LocalPath).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase)];
		bool joined = false;
		if (paths.Count > 1)
		{
			DialogChoice choice = await ConfirmDialog.AskOrSkipAsync(this, Strings.Queue_RecordingsTitle, string.Format(Strings.Queue_RecordingsMessage, paths.Count),
				Strings.Queue_RecordingsSeparate, Strings.Queue_RecordingsJoined);
			if (choice == DialogChoice.Cancel) return;

			joined = choice == DialogChoice.Skip;
		}

		await ShowNotAddedAsync(_main.QueueRecordings(paths, joined));
	}

	private async Task ShowNotAddedAsync(List<string> problems)
	{
		if (problems.Count > 0)
			await ConfirmDialog.ShowAsync(this, Strings.Queue_Title, string.Format(Strings.Queue_NotAdded, string.Join('\n', problems)), kind: DialogKind.Warning);
	}

	private void OnClearFinishedClick(object? sender, RoutedEventArgs e)
	{
		_queue.ClearFinished();
	}

	private void UpdateButtons()
	{
		bool running = _queue.IsRunning;
		StartButton.IsEnabled = !running && _queue.Items.Any(i => i.State == QueueState.Waiting);
		StopButton.IsEnabled = running;
		ExistingCombo.IsEnabled = !running;
	}

	/// <summary>
	///     The rows follow the queue: one per item, made when the item first shows and kept for as long as it's in the queue - so an open
	///     row stays open while the render changes under it, and only what changed is redrawn.
	/// </summary>
	private void Refresh()
	{
		IReadOnlyList<QueueItem> items = _queue.Items;
		foreach (QueueItem gone in _rows.Keys.Where(item => !items.Contains(item)).ToList()) _rows.Remove(gone);

		List<Control> shown = [];
		foreach (QueueItem item in items)
		{
			if (!_rows.TryGetValue(item, out QueueRow? row)) _rows[item] = row = new QueueRow(item, DetailsOf(item), _queue);

			row.Update();
			shown.Add(row.Root);
		}

		if (!ItemsPanel.Children.SequenceEqual(shown))
		{
			ItemsPanel.Children.Clear();
			ItemsPanel.Children.AddRange(shown);
		}

		EmptyText.IsVisible = items.Count == 0;
		SummaryText.Text = string.Format(Strings.Queue_Waiting, items.Count(i => i.State == QueueState.Waiting));
		UpdateButtons();
	}

	/// <summary>Read once per queued copy - it never changes.</summary>
	private QueueDetails DetailsOf(QueueItem item)
	{
		string path = item.Request.ProjectPath;
		if (_details.TryGetValue(path, out QueueDetails? known)) return known;

		return _details[path] = QueueDetails.Of(item.Request, OverlaySettingsStore.Load());
	}

	/// <summary>One queued render: its name and state on top, the recordings and the output when it's opened.</summary>
	private sealed class QueueRow
	{
		private readonly QueueItem _item;
		private readonly QueueDetails _details;
		private readonly RenderQueue _queue;
		private readonly TextBlock _subtitle;
		private readonly TextBlock _message;
		private readonly ProgressBar _progress;
		private readonly TextBlock _state;
		private readonly StackPanel _buttons;
		private readonly TextBlock _output;
		private QueueState? _buttonsFor;

		public QueueRow(QueueItem item, QueueDetails details, RenderQueue queue)
		{
			_item = item;
			_details = details;
			_queue = queue;

			var title = new TextBlock { Text = details.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
			_subtitle = new TextBlock { Foreground = Palette.TextMuted, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
			_message = new TextBlock { Foreground = Palette.TextMuted, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis };
			_progress = new ProgressBar { Minimum = 0, Maximum = 1, Height = 8, ShowProgressText = false };
			_state = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 90 };
			_buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
			_output = new TextBlock { TextWrapping = TextWrapping.Wrap };

			var text = new StackPanel { Spacing = 3 };
			text.Children.AddRange([title, _subtitle, _message, _progress]);
			var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 12, Margin = new Thickness(0, 4) };
			header.Children.Add(text);
			Grid.SetColumn(_state, 1);
			header.Children.Add(_state);
			Grid.SetColumn(_buttons, 2);
			header.Children.Add(_buttons);

			Root = new Expander
			{
				Header = header,
				Content = Details(),
				HorizontalAlignment = HorizontalAlignment.Stretch,
				HorizontalContentAlignment = HorizontalAlignment.Stretch,
				CornerRadius = new CornerRadius(8),
				ContentTransition = new CrossFade(TimeSpan.FromMilliseconds(150))
			};
		}

		public Expander Root { get; }

		/// <summary>What the project is made of: the recordings it renders, where the result goes, what it holds besides.</summary>
		private StackPanel Details()
		{
			var panel = new StackPanel { Spacing = 10, Margin = new Thickness(4, 2) };
			if (_details.Problem is { } problem)
				panel.Children.Add(new TextBlock { Text = string.Format(Strings.Queue_Unreadable, problem), Foreground = Palette.Danger, TextWrapping = TextWrapping.Wrap });

			var recordings = new StackPanel { Spacing = 2 };
			foreach (string recording in _details.Recordings)
			{
				bool found = File.Exists(recording);
				var line = new TextBlock { Text = found ? Path.GetFileName(recording) : $"{Path.GetFileName(recording)} ({Strings.Queue_FileMissing})", TextTrimming = TextTrimming.CharacterEllipsis };
				if (!found) line.Foreground = Palette.Danger;
				ToolTip.SetTip(line, recording);
				recordings.Children.Add(line);
			}

			if (_details.Recordings.Count > 0) panel.Children.Add(Section(Strings.Queue_DetailsRecordings, recordings));
			panel.Children.Add(Section(Strings.Queue_DetailsOutput, _output));
			if (_details.Recordings.Count > 0) panel.Children.Add(Section(Strings.Queue_DetailsContent, new TextBlock { Text = Contents(), TextWrapping = TextWrapping.Wrap }));
			return panel;
		}

		private string Contents()
		{
			List<string> parts = [];
			if (_details.Cuts > 0) parts.Add(string.Format(Strings.Queue_DetailsCuts, _details.Cuts));
			if (_details.MusicClips > 0) parts.Add(string.Format(Strings.Queue_DetailsMusic, _details.MusicClips));
			if (_details.EditedVolume) parts.Add(Strings.Queue_DetailsVolume);
			if (_details.ExternalTrack) parts.Add(Strings.Queue_DetailsTrack);
			return parts.Count == 0 ? Strings.Queue_DetailsNothing : string.Join(", ", parts);
		}

		private static StackPanel Section(string label, Control content)
		{
			var panel = new StackPanel { Spacing = 3 };
			panel.Children.Add(new TextBlock { Text = label, Foreground = Palette.TextMuted, FontSize = 12 });
			panel.Children.Add(content);
			return panel;
		}

		/// <summary>After the item or the queue changed.</summary>
		public void Update()
		{
			string output = _item.OutputPath ?? _details.OutputPath;
			_output.Text = output;
			_subtitle.Text = _details.Recordings.Count > 0
				? $"{Plural.Format(Strings.Queue_RecordingsCount, _details.Recordings.Count)} - {Path.GetFileName(output)}"
				: Path.GetFileName(output);
			_message.Text = _item.State switch
			{
				QueueState.Done => "",
				QueueState.Skipped => $"{_item.Message} {Strings.Queue_SkippedHint}",
				_ => _item.Message
			};
			_message.IsVisible = _message.Text.Length > 0;
			_progress.IsVisible = _item.State == QueueState.Rendering;
			_progress.Value = _item.Progress;
			_state.Text = StateText();
			_state.Foreground = StateBrush();

			// Only when the state changed: the buttons are replaced, and one being pressed at that moment would lose the click.
			if (_buttonsFor == _item.State) return;

			_buttonsFor = _item.State;
			_buttons.Children.Clear();
			if (_item.State == QueueState.Waiting)
			{
				_buttons.Children.Add(Button(Strings.Queue_MoveUp, () => _queue.Move(_item, -1)));
				_buttons.Children.Add(Button(Strings.Queue_MoveDown, () => _queue.Move(_item, 1)));
			}

			if (_item.State is QueueState.Failed or QueueState.Cancelled or QueueState.Skipped)
				_buttons.Children.Add(Button(Strings.Queue_Retry, () => _queue.Retry(_item)));
			if (_item.State == QueueState.Done && _item.OutputPath is { } written)
				_buttons.Children.Add(Button(Strings.Common_ShowInFolder, () => ExplorerHelper.ShowInFolder(written)));
			if (_item.State != QueueState.Rendering)
				_buttons.Children.Add(Button(Strings.Queue_Remove, () => _queue.Remove(_item)));
		}

		private static Button Button(string text, Action action)
		{
			var button = new Button { Content = text };
			button.Classes.Add("toolbar");
			button.Click += (_, _) => action();
			return button;
		}

		private string StateText()
		{
			return _item.State switch
			{
				QueueState.Waiting => Strings.Queue_StateWaiting,
				QueueState.Rendering => $"{Strings.Queue_StateRendering} {_item.Progress * 100:0}%",
				QueueState.Done => Strings.Queue_StateDone,
				QueueState.Failed => Strings.Queue_StateFailed,
				QueueState.Skipped => Strings.Queue_StateSkipped,
				_ => Strings.Queue_StateCancelled
			};
		}

		private IBrush StateBrush()
		{
			return _item.State switch
			{
				QueueState.Done => Palette.Success,
				QueueState.Failed => Palette.Danger,
				QueueState.Skipped or QueueState.Cancelled => Palette.Warning,
				QueueState.Rendering => Palette.Accent,
				_ => Palette.TextMuted
			};
		}
	}
}
