using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Gui;

public enum CutAction
{
	MarkIn,
	MarkOut,
	CutSelection,
	ClearSelection
}

/// <summary>
///     The cut editor in the left column - a view only, MainWindow.Cuts.cs owns the cuts and the selection.
///     Typed edits are validated and reported through CutsEdited after a short pause in typing (an invalid one
///     only shows its error); the buttons are reported as CutRequested, the same actions as the preview's
///     I/O/X keys, so both go through one place.
/// </summary>
public partial class CutEditor : UserControl
{
	// A cut's number button seeks this much before the cut, so the picture leading into it shows.
	private const double SeekLeadInSeconds = 3;

	private readonly List<CutRow> _rows = [];
	// Applying cuts remaps the preview's telemetry - not on every keystroke of "1:30.250".
	private readonly DispatcherTimer _applyDelay = new() { Interval = TimeSpan.FromMilliseconds(400) };
	private double _fps;
	private long _totalFrames;
	private bool _populating;
	private List<FrameRange>? _pending;

	public CutEditor()
	{
		InitializeComponent();
		IsEnabled = false;
		ShowSelection(null, false);
		_applyDelay.Tick += (_, _) => Flush();
	}

	private static readonly (CutTransitionKind? Kind, string Label)[] TransitionKinds =
	[
		(null, Strings.Cut_TransitionNone),
		(CutTransitionKind.FadeBlack, Strings.Cut_TransitionFadeBlack),
		(CutTransitionKind.FadeWhite, Strings.Cut_TransitionFadeWhite),
		(CutTransitionKind.Crossfade, Strings.Cut_TransitionCrossfade),
		(CutTransitionKind.WipeLeft, Strings.Cut_TransitionWipeLeft),
		(CutTransitionKind.WipeRight, Strings.Cut_TransitionWipeRight),
		(CutTransitionKind.SlideLeft, Strings.Cut_TransitionSlideLeft),
		(CutTransitionKind.SlideRight, Strings.Cut_TransitionSlideRight)
	];

	private static readonly (RouteJoin Join, string Label)[] RouteJoins =
	[
		(RouteJoin.Gap, Strings.Cut_RouteGap),
		(RouteJoin.Dashed, Strings.Cut_RouteDashed),
		(RouteJoin.Straight, Strings.Cut_RouteStraight)
	];

	private bool _populatingRouteJoin;

	public event Action<List<FrameRange>>? CutsEdited;

	/// <summary>The route-across-cuts setting picked here (saved and applied by MainWindow).</summary>
	public event Action<RouteJoin>? RouteJoinChanged;

	public void ShowRouteJoin(RouteJoin join)
	{
		_populatingRouteJoin = true;
		RouteJoinCombo.ItemsSource ??= RouteJoins.Select(r => r.Label).ToList();
		RouteJoinCombo.SelectedIndex = Math.Max(0, Array.FindIndex(RouteJoins, r => r.Join == join));
		_populatingRouteJoin = false;
	}

	private void OnRouteJoinChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (_populatingRouteJoin || RouteJoinCombo.SelectedIndex < 0) return;

		RouteJoinChanged?.Invoke(RouteJoins[RouteJoinCombo.SelectedIndex].Join);
	}

	public event Action<CutAction>? CutRequested;
	public event Action<long>? SeekRequested;

	/// <summary>Binds the editor to a loaded recording, or disables it (0 frames) when there is none.</summary>
	public void Attach(double fps, long totalFrames)
	{
		_fps = fps;
		_totalFrames = totalFrames;
		IsEnabled = totalFrames > 0;
	}

	/// <summary>Shows cuts set elsewhere (cutting a selection, a newly loaded file) without reporting them back.</summary>
	public void Show(IReadOnlyList<FrameRange> cuts)
	{
		_applyDelay.Stop();
		_pending = null;
		_populating = true;
		_rows.Clear();
		CutRows.Children.Clear();
		foreach (FrameRange cut in cuts) AddRow(cut);
		CutListPanel.IsVisible = _rows.Count > 0;
		ShowError(null);
		_populating = false;
	}

	public void ShowSummary(string summary)
	{
		SummaryText.Text = summary;
	}

	/// <param name="selection">The selection as text, or null when nothing is selected.</param>
	/// <param name="canCut">Whether there is a selection to cut out.</param>
	public void ShowSelection(string? selection, bool canCut)
	{
		SelectionText.Text = selection is null ? Strings.Cut_NothingSelected : string.Format(Strings.Cut_Selected, selection);
		SelectionText.Opacity = selection is null ? 0.6 : 1;
		ClearSelectionButton.IsVisible = selection is not null;
		CutSelectionButton.IsEnabled = canCut;
	}

	public void ShowError(string? message)
	{
		ErrorText.Text = message;
		ErrorText.IsVisible = message is not null;
	}

	private void OnMarkInClick(object? sender, RoutedEventArgs e)
	{
		Request(CutAction.MarkIn);
	}

	private void OnMarkOutClick(object? sender, RoutedEventArgs e)
	{
		Request(CutAction.MarkOut);
	}

	private void OnCutSelectionClick(object? sender, RoutedEventArgs e)
	{
		Request(CutAction.CutSelection);
	}

	private void OnClearSelectionClick(object? sender, RoutedEventArgs e)
	{
		Request(CutAction.ClearSelection);
	}

	private void Request(CutAction action)
	{
		// A typed edit still waiting for its pause is applied first - the action builds on it.
		Flush();
		CutRequested?.Invoke(action);
	}

	private void AddRow(FrameRange cut)
	{
		var number = new Button
		{
			Content = (_rows.Count + 1).ToString(),
			Classes = { "toolbar" },
			MinWidth = 30,
			Padding = new Thickness(6, 0),
			VerticalAlignment = VerticalAlignment.Stretch,
			HorizontalContentAlignment = HorizontalAlignment.Center,
			VerticalContentAlignment = VerticalAlignment.Center
		};
		ToolTip.SetTip(number, Strings.Cut_GoToBefore);
		var from = new TextBox { Text = TimeText.Format(cut.Start / _fps), PlaceholderText = Strings.Cut_From };
		var to = new TextBox { Text = TimeText.Format(cut.End / _fps), PlaceholderText = Strings.Cut_To };
		var remove = new Button
		{
			Content = new IconView { Data = Icons.Close, Width = 11, Height = 11 },
			VerticalAlignment = VerticalAlignment.Stretch,
			Padding = new Thickness(9, 0)
		};
		ToolTip.SetTip(remove, Strings.Cut_KeepAfterAll);

		var times = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,*,Auto"), ColumnSpacing = 6 };
		Control[] cells = [number, from, new TextBlock { Text = "-", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.55 }, to, remove];
		for (int i = 0; i < cells.Length; i++)
		{
			Grid.SetColumn(cells[i], i);
			times.Children.Add(cells[i]);
		}

		var kind = new ComboBox
		{
			ItemsSource = TransitionKinds.Select(k => k.Label).ToList(),
			SelectedIndex = Math.Max(0, Array.FindIndex(TransitionKinds, k => k.Kind == cut.Transition?.Kind)),
			HorizontalAlignment = HorizontalAlignment.Stretch
		};
		var length = new NumericUpDown
		{
			Minimum = (decimal)CutTransition.MinLengthSeconds,
			Maximum = (decimal)CutTransition.MaxLengthSeconds,
			Increment = 0.1m,
			FormatString = "0.0",
			Value = (decimal)(cut.Transition?.LengthSeconds ?? CutTransition.DefaultLengthSeconds),
			MinWidth = 110,
			IsVisible = cut.Transition is not null
		};
		var transition = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 6 };
		Control[] transitionCells = [kind, length, new TextBlock { Text = "s", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.55 }];
		for (int i = 0; i < transitionCells.Length; i++)
		{
			Grid.SetColumn(transitionCells[i], i);
			transition.Children.Add(transitionCells[i]);
		}

		var unit = (TextBlock)transitionCells[2];
		var overlapNote = new TextBlock
		{
			Text = Strings.Cut_OverlapNote,
			Classes = { "hint" },
			TextWrapping = TextWrapping.Wrap,
			IsVisible = false
		};
		var row = new StackPanel { Spacing = 4, Children = { times, transition, overlapNote } };
		var entry = new CutRow(from, to, kind, length);

		void RefreshTransitionRow()
		{
			// Only between two kept parts is there anything to fade (a cut from the very start or to the very end has no join).
			bool joins = ReadFrame(from.Text, "", out long start) is null && ReadFrame(to.Text, "", out long end) is null &&
			             start > 0 && end < _totalFrames;
			transition.IsVisible = joins;
			length.IsVisible = unit.IsVisible = kind.SelectedIndex > 0;
			overlapNote.IsVisible = joins && TransitionKinds[Math.Max(0, kind.SelectedIndex)].Kind is { } selected &&
			                        new CutTransition(selected).Overlaps;
		}

		RefreshTransitionRow();
		_rows.Add(entry);
		CutRows.Children.Add(row);

		number.Click += (_, _) =>
		{
			if (ReadFrame(from.Text, "", out long start) is null) SeekRequested?.Invoke(Math.Max(0, start - (long)Math.Round(SeekLeadInSeconds * _fps)));
		};
		from.TextChanged += (_, _) =>
		{
			RefreshTransitionRow();
			OnEdited(false);
		};
		to.TextChanged += (_, _) =>
		{
			RefreshTransitionRow();
			OnEdited(false);
		};
		kind.SelectionChanged += (_, _) =>
		{
			RefreshTransitionRow();
			OnEdited(true);
		};
		length.ValueChanged += (_, _) => OnEdited(false);
		remove.Click += (_, _) =>
		{
			_rows.Remove(entry);
			CutRows.Children.Remove(row);
			RenumberRows();
			CutListPanel.IsVisible = _rows.Count > 0;
			OnEdited(true);
		};
	}

	private void RenumberRows()
	{
		for (int i = 0; i < CutRows.Children.Count; i++)
		{
			if (CutRows.Children[i] is StackPanel { Children: [Grid { Children: [Button number, ..] }, ..] })
				number.Content = (i + 1).ToString();
		}
	}

	private void OnEdited(bool immediately)
	{
		if (_populating) return;

		string? error = Validate(out List<FrameRange>? cuts);
		ShowError(error);

		_applyDelay.Stop();
		_pending = cuts;
		if (cuts is null) return;

		if (immediately) Flush();
		else _applyDelay.Start();
	}

	private void Flush()
	{
		_applyDelay.Stop();
		if (_pending is not { } cuts) return;

		_pending = null;
		CutsEdited?.Invoke(cuts);
	}

	/// <summary>Every row as typed, in its own order - merging overlaps would reorder the rows under the caret, so that waits for the next cut.</summary>
	private string? Validate(out List<FrameRange>? cuts)
	{
		cuts = null;
		List<FrameRange> parsed = [];
		for (int i = 0; i < _rows.Count; i++)
		{
			(TextBox fromBox, TextBox toBox, ComboBox kindBox, NumericUpDown lengthBox) = _rows[i];
			string name = string.Format(Strings.Cut_Name, i + 1);
			if (ReadFrame(fromBox.Text, $"{name}: {Strings.Cut_From}", out long start) is { } fromError) return fromError;
			if (ReadFrame(toBox.Text, $"{name}: {Strings.Cut_To}", out long end) is { } toError) return toError;
			if (start >= _totalFrames) return string.Format(Strings.Cut_FromPastEnd, name, TimeText.Format(_totalFrames / _fps));
			if (end <= start) return string.Format(Strings.Cut_ToBeforeFrom, name);

			CutTransitionKind? kind = TransitionKinds[Math.Max(0, kindBox.SelectedIndex)].Kind;
			CutTransition? transition = kind is { } k
				? new CutTransition(k, lengthBox.Value is { } seconds ? (double)seconds : CutTransition.DefaultLengthSeconds)
				: null;
			parsed.Add(new FrameRange(start, end, transition));
		}

		if (CutList.RemovesEverything(parsed, _totalFrames)) return Strings.Cut_RemovesEverything;

		cuts = parsed;
		return null;
	}

	private sealed record CutRow(TextBox From, TextBox To, ComboBox Kind, NumericUpDown Length);

	/// <summary>A typed time as the nearest frame, within the recording.</summary>
	private string? ReadFrame(string? text, string name, out long frame)
	{
		frame = 0;
		if (string.IsNullOrWhiteSpace(text)) return string.Format(Strings.Cut_FieldEmpty, name);
		if (!TimeText.TryParse(text, out double seconds)) return string.Format(Strings.Cut_NotATime, name, text.Trim());

		frame = Math.Clamp((long)Math.Round(seconds * _fps), 0, _totalFrames);
		return null;
	}
}
