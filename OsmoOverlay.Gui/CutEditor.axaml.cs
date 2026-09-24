using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
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
	private readonly List<(TextBox From, TextBox To)> _rows = [];
	// Applying cuts rebuilds the preview's overlay renderer - not on every keystroke of "1:30.250".
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

	private static readonly (RouteJoin Join, string Label)[] RouteJoins =
	[
		(RouteJoin.Gap, "Break it - resume after the cut"),
		(RouteJoin.Dashed, "Dashed line across the cut"),
		(RouteJoin.Straight, "Straight line, as if nothing was cut")
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
		SelectionText.Text = selection is null ? "Nothing selected yet." : $"Selected: {selection}";
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
		ToolTip.SetTip(number, "Go to the start of this cut");
		var from = new TextBox { Text = TimeText.Format(cut.Start / _fps), PlaceholderText = "From" };
		var to = new TextBox { Text = TimeText.Format(cut.End / _fps), PlaceholderText = "To" };
		var remove = new Button
		{
			Content = new IconView { Data = Icons.Close, Width = 11, Height = 11 },
			VerticalAlignment = VerticalAlignment.Stretch,
			Padding = new Thickness(9, 0)
		};
		ToolTip.SetTip(remove, "Keep this part after all");

		var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,*,Auto"), ColumnSpacing = 6 };
		Control[] cells = [number, from, new TextBlock { Text = "-", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.55 }, to, remove];
		for (var i = 0; i < cells.Length; i++)
		{
			Grid.SetColumn(cells[i], i);
			row.Children.Add(cells[i]);
		}

		(TextBox From, TextBox To) entry = (from, to);
		_rows.Add(entry);
		CutRows.Children.Add(row);

		number.Click += (_, _) =>
		{
			if (ReadFrame(from.Text, "", out var start) is null) SeekRequested?.Invoke(start);
		};
		from.TextChanged += (_, _) => OnEdited(false);
		to.TextChanged += (_, _) => OnEdited(false);
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
		for (var i = 0; i < CutRows.Children.Count; i++)
			if (CutRows.Children[i] is Grid { Children: [Button number, ..] })
				number.Content = (i + 1).ToString();
	}

	private void OnEdited(bool immediately)
	{
		if (_populating) return;

		var error = Validate(out List<FrameRange>? cuts);
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
		for (var i = 0; i < _rows.Count; i++)
		{
			(TextBox fromBox, TextBox toBox) = _rows[i];
			var name = $"Cut {i + 1}";
			if (ReadFrame(fromBox.Text, $"{name}: From", out var start) is { } fromError) return fromError;
			if (ReadFrame(toBox.Text, $"{name}: To", out var end) is { } toError) return toError;
			if (start >= _totalFrames) return $"{name}: From is past the end of the recording ({TimeText.Format(_totalFrames / _fps)}).";
			if (end <= start) return $"{name}: To must come after From.";

			parsed.Add(new FrameRange(start, end));
		}

		if (CutList.RemovesEverything(parsed, _totalFrames)) return "The cuts remove the whole recording - nothing would be left to render.";

		cuts = parsed;
		return null;
	}

	/// <summary>A typed time as the nearest frame, within the recording.</summary>
	private string? ReadFrame(string? text, string name, out long frame)
	{
		frame = 0;
		if (string.IsNullOrWhiteSpace(text)) return $"{name} is empty.";
		if (!TimeText.TryParse(text, out var seconds)) return $"{name}: \"{text.Trim()}\" isn't a time - use e.g. 1:30 or 1:30.250.";

		frame = Math.Clamp((long)Math.Round(seconds * _fps), 0, _totalFrames);
		return null;
	}
}
