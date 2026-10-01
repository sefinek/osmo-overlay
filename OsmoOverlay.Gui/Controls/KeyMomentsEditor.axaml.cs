using System.Globalization;
using Avalonia;
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
	private static readonly PeakKind[] PeakKinds = Enum.GetValues<PeakKind>();

	private List<KeyMoment> _moments = [];
	private double _fps;

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
		IsEnabled = totalFrames > 0;
		PeakRows.Children.Clear();

		bool any = false;
		if (frames is { Count: > 0 } && totalFrames > 0)
		{
			foreach (PeakKind kind in PeakKinds)
			{
				if (KeyMoments.Find(frames, kind, fps, totalFrames) is not { } peak) continue;

				AddPeakRow(kind, peak, units);
				any = true;
			}
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

	private void OnAddClick(object? sender, RoutedEventArgs e)
	{
		AddRequested?.Invoke();
	}

	private void AddPeakRow(PeakKind kind, Peak peak, UnitSystem units)
	{
		string label = KeyMoments.Label(kind);
		var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
		var value = new TextBlock
		{
			Text = $"{FormatValue(kind, peak.Value, units)}  -  {TimeText.Format(peak.Frame / _fps)}",
			Opacity = 0.6,
			FontSize = 12,
			VerticalAlignment = VerticalAlignment.Center
		};
		var jump = new Button
		{
			Content = new Grid
			{
				ColumnDefinitions = new ColumnDefinitions("*,Auto"),
				ColumnSpacing = 8,
				Children = { name, value }
			},
			HorizontalAlignment = HorizontalAlignment.Stretch,
			HorizontalContentAlignment = HorizontalAlignment.Stretch
		};
		Grid.SetColumn(value, 1);
		ToolTip.SetTip(jump, $"Go to the {label.ToLowerInvariant()}");
		jump.Click += (_, _) => SeekRequested?.Invoke(peak.Frame);

		var save = new Button
		{
			Content = new IconView { Data = Icons.Flag, Width = 12, Height = 12 },
			Padding = new Thickness(9, 0),
			VerticalAlignment = VerticalAlignment.Stretch
		};
		ToolTip.SetTip(save, "Save it as a key moment");
		save.Click += (_, _) => MomentAdded?.Invoke(new KeyMoment(peak.Frame, label));

		var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
		Grid.SetColumn(save, 1);
		row.Children.Add(jump);
		row.Children.Add(save);
		PeakRows.Children.Add(row);
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
		ToolTip.SetTip(time, "Go to this moment");
		time.Click += (_, _) => SeekRequested?.Invoke(moment.Frame);

		var name = new TextBox { Text = moment.Name, PlaceholderText = "Name" };
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
		ToolTip.SetTip(remove, "Remove this key moment");
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
