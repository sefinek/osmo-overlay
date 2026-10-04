using System.Globalization;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Gui;

/// <summary>
///     The key moments panel in the left column - a view only, MainWindow.Moments.cs owns the moments. The jump buttons
///     seek to a peak of the telemetry (KeyMoments.Find); the flag beside one, and the Add button (at the frame on screen),
///     report a new moment, and edits to the list (a name, a removal) come back as MomentsEdited.
/// </summary>
public partial class KeyMomentsEditor : UserControl
{
	private const int PeaksPerCategory = 3;

	private static readonly PeakKind[] PeakKinds = Enum.GetValues<PeakKind>();

	private List<KeyMoment> _moments = [];
	private double _fps;
	// What Attach was given, so the peaks can be found again when "only while riding" is switched.
	private long _totalFrames;
	private IReadOnlyList<DerivedFrame>? _frames;
	private UnitSystem _units;

	public KeyMomentsEditor()
	{
		InitializeComponent();
		IsEnabled = false;
	}

	public event Action? AddRequested;
	public event Action<KeyMoment>? MomentAdded;
	public event Action<List<KeyMoment>>? MomentsEdited;
	public event Action<long>? SeekRequested;

	/// <summary>Binds the panel to a loaded recording and finds its peaks, or disables it (no frames) when there is none.</summary>
	public void Attach(double fps, long totalFrames, IReadOnlyList<DerivedFrame>? frames, UnitSystem units)
	{
		_fps = fps;
		_totalFrames = totalFrames;
		_frames = frames;
		_units = units;
		IsEnabled = totalFrames > 0;
		PeakRows.Children.Clear();
		ShowPeaks();
	}

	/// <summary>
	///     Each category with its peaks. The categories already shown are updated in place, not built again: a new expander
	///     plays its chevron's turn, which made every switch of "only while riding" flick all of them. A new recording
	///     starts from an empty list, every category closed.
	/// </summary>
	private void ShowPeaks()
	{
		bool movingOnly = MovingOnlyCheck.IsChecked == true;
		bool any = false;
		int position = 0;
		foreach (PeakKind kind in PeakKinds)
		{
			List<Peak> peaks = _frames is { Count: > 0 } frames && _totalFrames > 0
				? KeyMoments.FindTop(frames, kind, _fps, _totalFrames, PeaksPerCategory, movingOnly)
				: [];
			Expander? shown = PeakRows.Children.OfType<Expander>().FirstOrDefault(e => e.Tag is PeakKind k && k == kind);
			if (peaks.Count == 0)
			{
				if (shown is null) continue;

				shown.IsVisible = false;
				position++;
				continue;
			}

			if (shown is null)
			{
				shown = new Expander
				{
					HorizontalAlignment = HorizontalAlignment.Stretch,
					HorizontalContentAlignment = HorizontalAlignment.Stretch,
					CornerRadius = new CornerRadius(8),
					// The peaks fade in and out instead of popping up as the expander opens and closes.
					ContentTransition = new CrossFade(TimeSpan.FromMilliseconds(150)),
					Tag = kind
				};
				PeakRows.Children.Insert(position, shown);
			}

			shown.Header = CategoryHeader(kind, peaks, _units);
			shown.Content = CategoryRows(kind, peaks, _units);
			shown.IsVisible = true;
			position++;
			any = true;
		}

		NoPeaksText.IsVisible = !any;
	}

	/// <summary>Shows the moments set elsewhere (a project, an add from a key) without reporting them back.</summary>
	public void Show(IReadOnlyList<KeyMoment> moments)
	{
		_moments = [.. moments];
		MomentRows.Children.Clear();
		for (int i = 0; i < _moments.Count; i++) AddMomentRow(i);
		MomentListPanel.IsVisible = _moments.Count > 0;
	}

	/// <summary>A filter of this view only, on again for every start of the app.</summary>
	private void OnMovingOnlyClick(object? sender, RoutedEventArgs e)
	{
		ShowPeaks();
	}

	private void OnAddClick(object? sender, RoutedEventArgs e)
	{
		AddRequested?.Invoke();
	}

	/// <summary>A category's name and its best value, on its expander's header.</summary>
	private static Grid CategoryHeader(PeakKind kind, List<Peak> peaks, UnitSystem units)
	{
		var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
		var best = new TextBlock
		{
			Text = FormatValue(kind, peaks[0].Value, units),
			Opacity = 0.6,
			FontSize = 12,
			VerticalAlignment = VerticalAlignment.Center
		};
		Grid.SetColumn(best, 1);
		header.Children.Add(new TextBlock { Text = KeyMoments.Label(kind), VerticalAlignment = VerticalAlignment.Center });
		header.Children.Add(best);
		return header;
	}

	/// <summary>A category's top peaks, each with its own jump and flag.</summary>
	private StackPanel CategoryRows(PeakKind kind, List<Peak> peaks, UnitSystem units)
	{
		var rows = new StackPanel { Spacing = 6 };
		for (int i = 0; i < peaks.Count; i++) rows.Children.Add(CreatePeakRow(kind, peaks[i], i, units));
		return rows;
	}

	private Grid CreatePeakRow(PeakKind kind, Peak peak, int rank, UnitSystem units)
	{
		string label = KeyMoments.Label(kind);
		var value = new TextBlock { Text = $"{rank + 1}.  {FormatValue(kind, peak.Value, units)}", VerticalAlignment = VerticalAlignment.Center };
		var time = new TextBlock
		{
			Text = TimeText.Format(peak.Frame / _fps),
			Opacity = 0.6,
			FontSize = 12,
			VerticalAlignment = VerticalAlignment.Center
		};
		Grid.SetColumn(time, 1);
		var jump = new Button
		{
			Content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Children = { value, time } },
			HorizontalAlignment = HorizontalAlignment.Stretch,
			HorizontalContentAlignment = HorizontalAlignment.Stretch
		};
		ToolTip.SetTip(jump, string.Format(Strings.KeyMoments_GoToPeak, label));
		jump.Click += (_, _) => SeekRequested?.Invoke(peak.Frame);

		var save = new Button
		{
			Content = new IconView { Data = Icons.Flag, Width = 12, Height = 12 },
			Padding = new Thickness(9, 0),
			VerticalAlignment = VerticalAlignment.Stretch
		};
		ToolTip.SetTip(save, Strings.KeyMoments_SaveAsMoment);
		string name = rank == 0 ? label : $"{label} ({rank + 1})";
		save.Click += (_, _) => MomentAdded?.Invoke(new KeyMoment(peak.Frame, name));

		var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
		Grid.SetColumn(save, 1);
		row.Children.Add(jump);
		row.Children.Add(save);
		return row;
	}

	private void AddMomentRow(int index)
	{
		KeyMoment moment = _moments[index];
		var time = new Button
		{
			Content = TimeText.Format(moment.Frame / _fps),
			Classes = { "toolbar" },
			MinWidth = 78,
			Padding = new Thickness(8, 0),
			VerticalAlignment = VerticalAlignment.Stretch,
			HorizontalContentAlignment = HorizontalAlignment.Center,
			VerticalContentAlignment = VerticalAlignment.Center
		};
		ToolTip.SetTip(time, Strings.KeyMoments_GoToMoment);
		time.Click += (_, _) => SeekRequested?.Invoke(moment.Frame);

		var name = new TextBox { Text = moment.Name, PlaceholderText = Strings.KeyMoments_Name };

		void CommitName()
		{
			string text = name.Text?.Trim() ?? "";
			if (index >= _moments.Count || _moments[index].Name == text) return;

			_moments[index] = _moments[index] with { Name = text };
			MomentsEdited?.Invoke([.. _moments]);
		}

		name.LostFocus += (_, _) => CommitName();
		name.KeyDown += (_, e) =>
		{
			if (e.Key != Key.Enter) return;

			CommitName();
			e.Handled = true;
		};

		var remove = new Button
		{
			Content = new IconView { Data = Icons.Close, Width = 11, Height = 11 },
			VerticalAlignment = VerticalAlignment.Stretch,
			Padding = new Thickness(9, 0)
		};
		ToolTip.SetTip(remove, Strings.KeyMoments_Remove);
		remove.Click += (_, _) =>
		{
			_moments.RemoveAt(index);
			MomentsEdited?.Invoke([.. _moments]);
		};

		var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 6 };
		Control[] cells = [time, name, remove];
		for (int i = 0; i < cells.Length; i++)
		{
			Grid.SetColumn(cells[i], i);
			row.Children.Add(cells[i]);
		}

		MomentRows.Children.Add(row);
	}

	private static string FormatValue(PeakKind kind, double value, UnitSystem units)
	{
		bool imperial = units == UnitSystem.Imperial;
		CultureInfo c = CultureInfo.InvariantCulture;
		return kind switch
		{
			PeakKind.TopSpeed => imperial ? $"{(value * 0.621371).ToString("0", c)} mph" : $"{value.ToString("0", c)} km/h",
			PeakKind.HighestPoint or PeakKind.LowestPoint => imperial ? $"{(value * 3.28084).ToString("0", c)} ft" : $"{value.ToString("0", c)} m",
			PeakKind.StrongestG => $"{value.ToString("0.00", c)} G",
			PeakKind.SteepestClimb or PeakKind.SteepestDescent => $"{value.ToString("0.0", c)}%",
			PeakKind.MaxLean => $"{value.ToString("0", c)}°",
			_ => value.ToString("0.##", c)
		};
	}
}
