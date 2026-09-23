using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Logging;

namespace OsmoOverlay.Gui;

public partial class CompareVideosWindow : Window
{
	// The comparison table is a hand-built Grid (label column + one column per file) rather than a
	// DataGrid control - this repo has no DataGrid dependency, and the table's shape (a handful of
	// fixed property rows against up to MaxFiles columns) doesn't need one.
	private const int MaxFiles = 6;
	private static readonly IBrush DiffBrush = Palette.Warning;
	private static readonly IBrush RowBandBrush = Palette.SubtleFill;
	private static readonly IBrush SectionDividerBrush = Palette.Stroke;
	private readonly Dictionary<string, string> _errors = [];

	private readonly List<string> _paths = [];
	private readonly Dictionary<string, FileSummary?> _summaries = []; // missing key = not loaded yet, null value = still loading
	private bool _showOnlyDifferences;

	public CompareVideosWindow()
	{
		InitializeComponent();
		Render();
	}

	/// <summary>Opens pre-loaded with the given files, e.g. "Compare files" from the color tag fixer's success dialog.</summary>
	public CompareVideosWindow(IEnumerable<string> initialPaths) : this()
	{
		foreach (var path in initialPaths)
		{
			if (_paths.Count >= MaxFiles) break;
			if (_paths.Contains(path)) continue;
			_paths.Add(path);
			LoadSummary(path);
		}

		Render();
	}

	private async void OnAddFilesClick(object? sender, RoutedEventArgs e)
	{
		TopLevel? topLevel = GetTopLevel(this);
		if (topLevel is null) return;

		if (_paths.Count >= MaxFiles)
		{
			await ConfirmDialog.ShowAsync(this, "Limit reached",
				$"You can compare up to {MaxFiles} files at a time - remove one first.",
				kind: DialogKind.Info, windowTitle: "Compare videos");
			return;
		}

		IReadOnlyList<IStorageFile> files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
		{
			Title = "Select videos to compare",
			AllowMultiple = true,
			FileTypeFilter = [new FilePickerFileType("MP4 video") { Patterns = ["*.mp4", "*.MP4"] }]
		});

		if (files.Count == 0) return;

		var skippedForLimit = false;
		foreach (IStorageFile file in files)
		{
			var path = file.Path.LocalPath;
			if (_paths.Contains(path)) continue;

			if (_paths.Count >= MaxFiles)
			{
				skippedForLimit = true;
				break;
			}

			_paths.Add(path);
			LoadSummary(path);
		}

		Render();

		if (skippedForLimit)
			await ConfirmDialog.ShowAsync(this, "Limit reached",
				$"Only added the first {MaxFiles} files - this tool compares up to {MaxFiles} at a time.",
				kind: DialogKind.Info, windowTitle: "Compare videos");
	}

	private async void LoadSummary(string path)
	{
		_summaries[path] = null;
		_errors.Remove(path);
		try
		{
			FileSummary summary = await Task.Run(() => FileSummaryReader.Read(path));
			if (!_paths.Contains(path)) return; // removed while loading
			_summaries[path] = summary;
		}
		catch (Exception ex)
		{
			if (!_paths.Contains(path)) return;
			_errors[path] = ex.Message;
		}

		Render();
	}

	private void OnRemoveFileClick(object? sender, RoutedEventArgs e)
	{
		if (sender is not Button { Tag: string path }) return;
		_paths.Remove(path);
		_summaries.Remove(path);
		_errors.Remove(path);
		Render();
	}

	private void OnClearAllClick(object? sender, RoutedEventArgs e)
	{
		_paths.Clear();
		_summaries.Clear();
		_errors.Clear();
		Render();
	}

	private void OnShowOnlyDifferencesClick(object? sender, RoutedEventArgs e)
	{
		_showOnlyDifferences = ShowOnlyDifferencesCheck.IsChecked ?? false;
		Render();
	}

	private async void OnCopyAsTextClick(object? sender, RoutedEventArgs e)
	{
		TopLevel? topLevel = GetTopLevel(this);
		if (topLevel?.Clipboard is null || _paths.Count == 0) return;

		await topLevel.Clipboard.SetTextAsync(BuildComparisonMarkdown());
		AppLogger.Notify($"Copied comparison of {_paths.Count} file(s) to clipboard as Markdown.");
	}

	private async void OnSaveToFileClick(object? sender, RoutedEventArgs e)
	{
		TopLevel? topLevel = GetTopLevel(this);
		if (topLevel is null || _paths.Count == 0) return;

		IStorageFile? file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
		{
			Title = "Save video comparison",
			SuggestedFileName = $"video-comparison-{DateTime.Now:yyyyMMdd-HHmmss}",
			DefaultExtension = "md",
			FileTypeChoices =
			[
				new FilePickerFileType("Markdown") { Patterns = ["*.md"] },
				new FilePickerFileType("Text file") { Patterns = ["*.txt"] }
			]
		});

		if (file is null) return;

		await File.WriteAllTextAsync(file.Path.LocalPath, BuildComparisonMarkdown());
		AppLogger.Notify($"Saved video comparison to {Path.GetFileName(file.Path.LocalPath)}.");
	}

	private void Render()
	{
		EmptyStateText.IsVisible = _paths.Count == 0;
		AddFilesButton.IsEnabled = _paths.Count < MaxFiles;

		CompareGrid.Children.Clear();
		CompareGrid.ColumnDefinitions.Clear();
		CompareGrid.RowDefinitions.Clear();

		if (_paths.Count == 0)
		{
			NoDifferencesText.IsVisible = false;
			return;
		}

		IReadOnlyList<RowSpec> allRows = BuildRows();
		List<(string[] Values, bool Differs)> computed = [.. allRows.Select(ComputeRow)];
		List<int> visible = VisibleRowIndices(allRows, computed);

		NoDifferencesText.IsVisible = visible.Count == 0;
		if (visible.Count == 0) return;

		var sectionId = ComputeSectionIds(allRows);

		CompareGrid.ColumnDefinitions.Add(new ColumnDefinition(190, GridUnitType.Pixel));
		foreach (var _ in _paths) CompareGrid.ColumnDefinitions.Add(new ColumnDefinition(270, GridUnitType.Pixel));

		// Grid row layout: header, then for each visible row an optional divider row right above it
		// (whenever its section differs from the previous VISIBLE row's, so filtering out a whole
		// section never leaves an orphan divider line), then the row itself, then a trailing Remove row.
		var gridRowOfContent = new int[visible.Count];
		var nextGridRow = 1;
		for (var v = 0; v < visible.Count; v++)
		{
			if (v > 0 && sectionId[visible[v]] != sectionId[visible[v - 1]]) nextGridRow++;
			gridRowOfContent[v] = nextGridRow;
			nextGridRow++;
		}

		var removeRowIndex = nextGridRow;
		for (var i = 0; i <= removeRowIndex; i++) CompareGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

		for (var col = 0; col < _paths.Count; col++)
			AddHeaderCell(col);

		for (var v = 0; v < visible.Count; v++)
		{
			if (v > 0 && sectionId[visible[v]] != sectionId[visible[v - 1]]) AddSectionDivider(gridRowOfContent[v] - 1);
			var rowIdx = visible[v];
			AddRow(gridRowOfContent[v], allRows[rowIdx].Label, computed[rowIdx].Values, computed[rowIdx].Differs, v % 2 == 1);
		}

		for (var col = 0; col < _paths.Count; col++)
			AddRemoveCell(col, removeRowIndex);
	}

	/// <summary>Row indices to actually draw - all of them normally, or only the ones where loaded values disagree when the "Show only differences" filter is on.</summary>
	private List<int> VisibleRowIndices(IReadOnlyList<RowSpec> rows, List<(string[] Values, bool Differs)> computed)
	{
		IEnumerable<int> all = Enumerable.Range(0, rows.Count);
		return [.. _showOnlyDifferences ? all.Where(i => computed[i].Differs) : all];
	}

	/// <summary>Values for this row across every added file, plus whether the loaded ones disagree - "Loading"/"Error" placeholders never count as a disagreement.</summary>
	private (string[] Values, bool Differs) ComputeRow(RowSpec row)
	{
		var values = new string[_paths.Count];
		var loadedValues = new List<string>(_paths.Count);
		for (var col = 0; col < _paths.Count; col++)
		{
			var path = _paths[col];
			if (_summaries.TryGetValue(path, out FileSummary? summary) && summary is not null)
			{
				values[col] = row.Value(summary);
				loadedValues.Add(values[col]);
			}
			else
			{
				values[col] = _errors.ContainsKey(path) ? "Error" : "Loading...";
			}
		}

		return (values, loadedValues.Distinct().Count() > 1);
	}

	private static int[] ComputeSectionIds(IReadOnlyList<RowSpec> rows)
	{
		var ids = new int[rows.Count];
		var section = 0;
		for (var i = 0; i < rows.Count; i++)
		{
			if (i > 0 && rows[i].NewSection) section++;
			ids[i] = section;
		}

		return ids;
	}

	/// <summary>
	///     One "## filename" section per file, each followed by its properties as a bullet list (only
	///     the visible ones, respecting "Show only differences") - reads naturally pasted into a Markdown
	///     note or a GitHub/GitLab comment, which a padded plain-text table doesn't.
	/// </summary>
	private string BuildComparisonMarkdown()
	{
		IReadOnlyList<RowSpec> allRows = BuildRows();
		List<(string[] Values, bool Differs)> computed = [.. allRows.Select(ComputeRow)];
		var sectionId = ComputeSectionIds(allRows);

		// Extension is dropped here (unlike in the table) - the heading below is the full path, which
		// already carries the extension, so repeating it as its own bullet would just be noise.
		List<int> visible = [.. VisibleRowIndices(allRows, computed).Where(i => allRows[i].Label != "Extension")];

		var sb = new StringBuilder();
		sb.AppendLine($"-- Exported: {DateTime.Now:yyyy-MM-dd HH:mm:ss} --");
		sb.AppendLine();

		for (var col = 0; col < _paths.Count; col++)
		{
			if (col > 0) sb.AppendLine();
			sb.AppendLine($"## {Path.GetFileName(_paths[col])}");
			sb.AppendLine();

			int? lastSection = null;
			foreach (var i in visible)
			{
				if (lastSection is not null && sectionId[i] != lastSection) sb.AppendLine();
				lastSection = sectionId[i];
				sb.AppendLine($"- {allRows[i].Label}: {computed[i].Values[col]}");
			}
		}

		return sb.ToString();
	}

	private void AddSectionDivider(int rowIndex)
	{
		var divider = new Border { Background = SectionDividerBrush, Height = 2, Margin = new Thickness(0, 6) };
		Grid.SetRow(divider, rowIndex);
		Grid.SetColumn(divider, 0);
		Grid.SetColumnSpan(divider, CompareGrid.ColumnDefinitions.Count);
		CompareGrid.Children.Add(divider);
	}

	private void AddHeaderCell(int col)
	{
		var path = _paths[col];
		var header = new TextBlock
		{
			Text = Path.GetFileNameWithoutExtension(path), FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(6)
		};

		Grid.SetRow(header, 0);
		Grid.SetColumn(header, col + 1);
		CompareGrid.Children.Add(header);
	}

	private void AddRemoveCell(int col, int rowIndex)
	{
		var path = _paths[col];
		var removeButton = new Button
		{
			Content = "Remove", Tag = path, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(6)
		};
		removeButton.Click += OnRemoveFileClick;

		Grid.SetRow(removeButton, rowIndex);
		Grid.SetColumn(removeButton, col + 1);
		CompareGrid.Children.Add(removeButton);
	}

	private void AddRow(int rowIndex, string label, string[] values, bool differs, bool banded)
	{
		if (banded)
		{
			var band = new Border { Background = RowBandBrush };
			Grid.SetRow(band, rowIndex);
			Grid.SetColumn(band, 0);
			Grid.SetColumnSpan(band, CompareGrid.ColumnDefinitions.Count);
			CompareGrid.Children.Add(band);
		}

		var labelBlock = new TextBlock { Text = label, Classes = { "rowLabel" }, Margin = new Thickness(6) };
		Grid.SetRow(labelBlock, rowIndex);
		Grid.SetColumn(labelBlock, 0);
		CompareGrid.Children.Add(labelBlock);

		for (var col = 0; col < _paths.Count; col++)
		{
			var cell = new TextBlock
			{
				Text = values[col],
				Classes = { "cellValue" },
				Margin = new Thickness(6)
			};
			if (differs) cell.Foreground = DiffBrush;
			Grid.SetRow(cell, rowIndex);
			Grid.SetColumn(cell, col + 1);
			CompareGrid.Children.Add(cell);
		}
	}

	private static IReadOnlyList<RowSpec> BuildRows()
	{
		return
		[
			new RowSpec("Extension", s => Path.GetExtension(s.InputPaths[0]).TrimStart('.').ToUpperInvariant()),
			new RowSpec("Camera", s => s.CameraModel ?? "Unknown"),
			new RowSpec("Resolution", s => $"{s.Video.Width}x{s.Video.Height}"),
			new RowSpec("Frame rate", s => $"{s.Video.Fps:0.##} fps"),
			new RowSpec("Codec", s => string.IsNullOrEmpty(s.Video.Profile) ? s.Video.CodecName : $"{s.Video.CodecName} ({s.Video.Profile})"),
			new RowSpec("Pixel format", s => s.Video.PixFmt),

			new RowSpec("Color primaries", s => s.Video.ColorPrimaries ?? "unknown", true),
			new RowSpec("Color transfer", s => s.Video.ColorTransfer ?? "unknown"),
			new RowSpec("Color matrix", s => s.Video.ColorSpace ?? "unknown"),
			new RowSpec("Color range", s => s.Video.ColorRange ?? "unknown"),

			new RowSpec("Video bitrate", s => $"{s.Video.BitRate / 1_000_000.0:0.#} Mbps", true),
			new RowSpec("Audio codec", s => s.Audio?.CodecName ?? "None"),
			new RowSpec("Audio sample rate", s => s.Audio is { } a ? $"{a.SampleRate} Hz" : "-"),
			new RowSpec("Audio channels", s => s.Audio is { } a ? $"{a.Channels}ch" : "-"),
			new RowSpec("Audio bitrate", s => s.Audio is { } a ? $"{a.BitRate / 1000.0:0} kbps" : "-"),

			new RowSpec("Duration", s => TimeSpan.FromSeconds(s.DurationSeconds).ToString(@"hh\:mm\:ss"), true),
			new RowSpec("File size", s => FormatHelper.FormatBytes(s.FileSizeBytes)),

			new RowSpec("Telemetry", s => s.HasTelemetry ? "Detected" : "Not found", true),
			new RowSpec("Max speed", s => s.Telemetry is { } t ? $"{t.MaxSpeedKmh:0.#} km/h" : "-"),
			new RowSpec("Distance", s => s.Telemetry is { } t ? $"{t.TotalDistanceMeters / 1000.0:0.00} km" : "-"),
			new RowSpec("Altitude range", s => s.Telemetry is { } t ? $"{t.MinAltitudeMeters:0}-{t.MaxAltitudeMeters:0} m" : "-"),
			new RowSpec("Max G-force", s => s.Telemetry is { } t ? $"{t.MaxGForce:0.00} G" : "-"),
			new RowSpec("Recorded at", s => s.Telemetry?.RecordedAtUtc is { } utc ? utc.ToLocalFromUtc().ToString("yyyy-MM-dd HH:mm:ss") : "Unknown")
		];
	}

	// NewSection marks the first row of a new logical group (general info / color tags / bitrate &
	// audio / size / telemetry) - Render() draws a brighter divider line above these instead of the
	// plain alternating row band, so the table reads as sections rather than one undifferentiated list.
	private sealed record RowSpec(string Label, Func<FileSummary, string> Value, bool NewSection = false);
}
