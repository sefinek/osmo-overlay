using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Localization;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Gui;

/// <summary>
///     The performance test (RenderBenchmark) on the loaded recording: the computer it ran on and how busy it was, what each
///     stage of a render manages here (median of the runs, their range, notes), the slowest stage, and the settings that would
///     be faster - each applied with a click (saved at once, like the welcome window's). The encoder preset is only shown: it
///     changes the file's quality, so it's never suggested. "Copy report" puts all of it on the clipboard as Markdown.
/// </summary>
public partial class BenchmarkWindow : Window
{
	private readonly BenchmarkInput? _input;
	private readonly long _totalFrames;
	private readonly Func<bool> _isRendering = () => false;
	private CancellationTokenSource? _cts;
	private BenchmarkResult? _result;

	public BenchmarkWindow()
	{
		InitializeComponent();
	}

	/// <param name="totalFrames">The recording's frames, for the estimated render time - 0 without one.</param>
	public BenchmarkWindow(BenchmarkInput input, long totalFrames, Func<bool> isRendering) : this()
	{
		_input = input;
		_totalFrames = totalFrames;
		_isRendering = isRendering;
		SourceText.Text = DescribeSource(input);
	}

	/// <summary>What the test will run on: a sample it cuts out of the loaded recording, or one it generates.</summary>
	private static string DescribeSource(BenchmarkInput input)
	{
		return input.RecordingPath is { } path
			? string.Format(Strings.Benchmark_OnRecording, RenderBenchmark.SampleSeconds, Path.GetFileName(path))
			: Strings.Benchmark_OnSynthetic;
	}

	/// <summary>Developer tools: a result as the window shows it, without running anything.</summary>
	public void ShowSample(BenchmarkResult result, long totalFrames)
	{
		StartButton.IsEnabled = false;
		SourceText.Text = "Sample result from the developer tools - nothing was measured.";
		ShowResult(result, totalFrames, false);
	}

	protected override void OnClosing(WindowClosingEventArgs e)
	{
		_cts?.Cancel();
		base.OnClosing(e);
	}

	private async void OnStartClick(object? sender, RoutedEventArgs e)
	{
		if (_cts is { } running)
		{
			running.Cancel();
			return;
		}

		if (_input is null) return;
		if (_isRendering())
		{
			StatusText.Text = Strings.Benchmark_WhileRendering;
			return;
		}

		var cts = new CancellationTokenSource();
		_cts = cts;
		StartButton.Content = Strings.Benchmark_Stop;
		Progress.IsVisible = true;
		Progress.IsIndeterminate = true;
		ResultsPanel.IsVisible = false;
		CopyReportButton.IsEnabled = false;
		try
		{
			BenchmarkResult result = await Task.Run(() =>
				RenderBenchmark.RunAsync(_input, progress => Dispatcher.UIThread.Post(() => ShowProgress(progress)), cts.Token));
			StatusText.Text = string.Format(Strings.Benchmark_Done, result.Duration.TotalSeconds.ToString("0"));
			ShowResult(result, _totalFrames, true);
		}
		catch (OperationCanceledException)
		{
			StatusText.Text = Strings.Benchmark_Stopped;
		}
		catch (Exception ex)
		{
			AppLogger.Warn(ex, "Performance test failed");
			StatusText.Text = string.Format(Strings.Benchmark_Failed, ex.Message);
		}
		finally
		{
			_cts = null;
			cts.Dispose();
			StartButton.Content = Strings.Benchmark_Start;
			Progress.IsVisible = false;
		}
	}

	private void ShowProgress(BenchmarkProgress progress)
	{
		if (_cts is null) return;

		Progress.IsIndeterminate = progress.Total == 0;
		Progress.Maximum = Math.Max(progress.Total, 1);
		Progress.Value = progress.Done;
		string stage = StageText(progress.Stage);
		StatusText.Text = progress.Total == 0 ? stage : string.Format(Strings.Benchmark_StageProgress, stage, progress.Done + 1, progress.Total);
	}

	private static string StageText(BenchmarkStage stage)
	{
		return stage switch
		{
			BenchmarkStage.Decode => Strings.Benchmark_StageDecode,
			BenchmarkStage.Overlay => Strings.Benchmark_StageOverlay,
			BenchmarkStage.Pipe => Strings.Benchmark_StagePipe,
			BenchmarkStage.Encode => Strings.Benchmark_StageEncode,
			BenchmarkStage.Graph => Strings.Benchmark_StageGraph,
			BenchmarkStage.Render => Strings.Benchmark_StageRender,
			_ => Strings.Benchmark_StagePreparing
		};
	}

	private void ShowResult(BenchmarkResult result, long totalFrames, bool canApply)
	{
		_result = result;
		OverlaySettings settings = OverlaySettingsStore.Load();

		LoadWarningText.IsVisible = result.Load.IsBusy;
		LoadWarningText.Text = string.Format(Strings.Benchmark_LoadWarning, Percent(result.Load.Cpu), Percent(result.Load.Gpu));
		UnstableWarningText.IsVisible = result.IsUnstable;
		WarningCard.IsVisible = LoadWarningText.IsVisible || UnstableWarningText.IsVisible;

		SystemGrid.Children.Clear();
		SystemGrid.RowDefinitions.Clear();
		foreach ((string label, string value) in SystemRows(result))
		{
			int row = AddRow(SystemGrid);
			AddCell(SystemGrid, new TextBlock { Text = label, Classes = { "resultLabel" } }, row, 0);
			AddCell(SystemGrid, new TextBlock { Text = value, Classes = { "resultValue" } }, row, 1);
		}

		ResultsGrid.Children.Clear();
		ResultsGrid.RowDefinitions.Clear();
		int header = AddRow(ResultsGrid);
		string[] headers = [Strings.Benchmark_ColumnStage, Strings.Benchmark_ColumnResult, Strings.Benchmark_ColumnRange, Strings.Benchmark_ColumnNotes];
		for (int column = 0; column < headers.Length; column++)
			AddCell(ResultsGrid, new TextBlock { Text = headers[column], Classes = { "columnHeader" } }, header, column);

		foreach (ResultRow item in ResultRows(result, settings))
		{
			int row = AddRow(ResultsGrid);
			AddCell(ResultsGrid, new TextBlock { Text = item.Stage, Classes = { "resultLabel" } }, row, 0);
			AddCell(ResultsGrid, new TextBlock { Text = item.Result, Classes = { "resultValue" } }, row, 1);
			AddCell(ResultsGrid, new TextBlock { Text = item.Range, Classes = { "resultLabel" } }, row, 2);
			AddCell(ResultsGrid, new TextBlock { Text = item.Notes, Classes = { "resultLabel" }, TextWrapping = TextWrapping.Wrap }, row, 3);
		}

		BottleneckText.Text = BottleneckDescription(result, settings);
		string? estimate = EstimateDescription(result, settings, totalFrames);
		EstimateText.IsVisible = estimate is not null;
		EstimateText.Text = estimate;

		ShowAdvice(RenderBenchmark.Advise(result, settings), SkippedAdvice(result), canApply);
		ResultsPanel.IsVisible = true;
		CopyReportButton.IsEnabled = true;
		CopyReportButton.Content = Strings.Benchmark_CopyReport;
	}

	private sealed record ResultRow(string Stage, string Result, string Range, string Notes);

	private static List<(string Label, string Value)> SystemRows(BenchmarkResult result)
	{
		BenchmarkSystem system = result.System;
		List<(string, string)> rows =
		[
			(Strings.Benchmark_Os, system.Os),
			(Strings.Benchmark_Cpu, $"{system.Cpu ?? Strings.Benchmark_Unknown}, {Plural.Format(Strings.Benchmark_Threads, system.Threads)}"),
			(Strings.Benchmark_Memory, FormatHelper.FormatBytes(system.MemoryBytes)),
			(Strings.Benchmark_Gpu, system.Gpus.Count > 0 ? string.Join("; ", system.Gpus) : Strings.Benchmark_Unknown),
			("FFmpeg", system.Ffmpeg ?? Strings.Benchmark_Unknown),
			("OsmoOverlay", system.App),
			(Strings.Benchmark_Load, string.Format(Strings.Benchmark_LoadValue, Percent(result.Load.Cpu), Percent(result.Load.Gpu))),
			(Strings.Benchmark_Recording, Describe(result.Source) + " (" + (result.FromRecording
				? string.Format(Strings.Benchmark_RecordingSample, RenderBenchmark.SampleSeconds)
				: Strings.Benchmark_GeneratedSample) + ")")
		];
		if (result.Output != result.Source) rows.Add((Strings.Benchmark_Output, Describe(result.Output)));
		return rows;
	}

	private static List<ResultRow> ResultRows(BenchmarkResult result, OverlaySettings settings)
	{
		List<ResultRow> rows =
		[
			Speed(Strings.Benchmark_DecodeGpu, result.HardwareDecode, result.Fps, settings.HardwareDecoding),
			Speed(Strings.Benchmark_DecodeCpu, result.SoftwareDecode, result.Fps, !settings.HardwareDecoding,
				result.FromRecording ? null : Strings.Benchmark_SampleComesOutHigh),
			Overlay(string.Format(Strings.Benchmark_OverlayRender, result.Output.Width, result.Output.Height), result.Overlay, result.OverlayPlain),
			Overlay(string.Format(Strings.Benchmark_OverlayPreview, result.PreviewWidth, result.PreviewHeight), result.PreviewOverlay,
				result.PreviewOverlayPlain),
			Speed(string.Format(Strings.Benchmark_Pipe, result.Output.Width, result.Output.Height), result.Pipe, result.Fps, false)
		];

		EncoderSpeed? inUse = RenderBenchmark.EncoderInUse(result, settings);
		foreach (EncoderSpeed encoder in result.Encoders)
		{
			rows.Add(Speed(string.Format(Strings.Benchmark_Encoding, $"{encoder.Encoder} {(encoder.Preset is { } preset ? FfmpegPipeline.PresetName(encoder.Encoder, preset) : null)}".Trim()), encoder.Speed, result.Fps,
				encoder == inUse, encoder.LimitedByDecoding ? Strings.Benchmark_LimitedByDecoding : null));
		}

		rows.Add(Speed(Strings.Benchmark_Graph, result.Graph, result.Fps, true));

		if (result.HardwareRender is not null || result.SoftwareRender is not null)
		{
			rows.Add(Speed(Strings.Benchmark_RenderGpu, result.HardwareRender, result.Fps, settings.HardwareDecoding, Strings.Benchmark_SampleRoute));
			rows.Add(Speed(Strings.Benchmark_RenderCpu, result.SoftwareRender, result.Fps, !settings.HardwareDecoding, Strings.Benchmark_SampleRoute));
		}

		return rows;
	}

	private static ResultRow Speed(string stage, BenchmarkStat? stat, double sourceFps, bool inUse, string? note = null)
	{
		if (stat is null) return new ResultRow(stage, Strings.Benchmark_StepFailed, "", "");

		List<string> notes = [];
		if (inUse) notes.Add(Strings.Benchmark_InUse);
		if (note is not null) notes.Add(note);
		if (sourceFps > 0) notes.Add(string.Format(Strings.Benchmark_Realtime, (stat.Median / sourceFps).ToString("0.0#")));
		if (stat.IsUnstable) notes.Add(Strings.Benchmark_Unstable);
		string range = stat.Runs > 1
			? string.Format(Strings.Benchmark_FpsRange, stat.Min.ToString("0"), stat.Max.ToString("0"), (stat.Spread / 2 * 100).ToString("0"))
			: "";
		return new ResultRow(stage, string.Format(Strings.Benchmark_Fps, stat.Median.ToString("0")), range, string.Join(", ", notes));
	}

	private static ResultRow Overlay(string stage, FrameTimes times, FrameTimes plain)
	{
		return new ResultRow(stage, string.Format(Strings.Benchmark_OverlayValue, times.MedianMs.ToString("0.0"), times.Fps.ToString("0")),
			string.Format(Strings.Benchmark_P95, times.P95Ms.ToString("0.0")), string.Format(Strings.Benchmark_WithoutShadows, plain.MedianMs.ToString("0.0")));
	}

	private static string BottleneckDescription(BenchmarkResult result, OverlaySettings settings)
	{
		if (RenderBenchmark.Bottleneck(result, settings) is not { } bottleneck) return Strings.Benchmark_StepFailed;

		string stage = bottleneck.Stage switch
		{
			BenchmarkStage.Decode => Strings.Benchmark_NameDecode,
			BenchmarkStage.Overlay => Strings.Benchmark_NameOverlay,
			BenchmarkStage.Pipe => Strings.Benchmark_NamePipe,
			BenchmarkStage.Graph => Strings.Benchmark_NameGraph,
			_ => Strings.Benchmark_NameEncode
		};
		return bottleneck is { RenderFps: { } render, Efficiency: { } efficiency }
			? string.Format(Strings.Benchmark_BottleneckWithRender, stage, bottleneck.Fps.ToString("0"), render.ToString("0"), (efficiency * 100).ToString("0"))
			: string.Format(Strings.Benchmark_BottleneckOnly, stage, bottleneck.Fps.ToString("0"));
	}

	private static string? EstimateDescription(BenchmarkResult result, OverlaySettings settings, long totalFrames)
	{
		double? renderFps = (settings.HardwareDecoding ? result.HardwareRender : result.SoftwareRender)?.Median;
		return totalFrames > 0 && renderFps > 0
			? string.Format(Strings.Benchmark_Estimate, TimeSpan.FromSeconds(totalFrames / renderFps.Value).ToString(@"hh\:mm\:ss"))
			: null;
	}

	private static string Describe(VideoInfo video)
	{
		string codec = video.CodecName switch
		{
			"hevc" => "HEVC",
			"h264" => "H.264",
			_ => video.CodecName.ToUpperInvariant()
		};
		int depth = video.PixFmt.Contains("10", StringComparison.Ordinal) ? 10 : 8;
		return string.Format(Strings.Benchmark_VideoValue, codec, video.Width, video.Height, video.Fps.ToString("0.##"), depth,
			(video.BitRate / 1_000_000.0).ToString("0"));
	}

	private static string Percent(double? share)
	{
		return share is { } value ? $"{value * 100:0}%" : Strings.Benchmark_Unknown;
	}

	private static int AddRow(Grid grid)
	{
		grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
		return grid.RowDefinitions.Count - 1;
	}

	private static void AddCell(Grid grid, Control cell, int row, int column)
	{
		Grid.SetRow(cell, row);
		Grid.SetColumn(cell, column);
		grid.Children.Add(cell);
	}

	/// <summary>Why decoding wasn't compared (RenderBenchmark.Advise) - null when it was.</summary>
	private static string? SkippedAdvice(BenchmarkResult result)
	{
		if (result.Load.IsBusy) return Strings.Benchmark_AdviceSkippedBusy;
		return result.FromRecording ? null : Strings.Benchmark_AdviceSkippedSample;
	}

	private void ShowAdvice(List<BenchmarkAdvice> advice, string? skipped, bool canApply)
	{
		AdvicePanel.Children.Clear();
		if (skipped is not null) AdvicePanel.Children.Add(new TextBlock { Text = skipped, Classes = { "warningText" } });
		if (advice.Count == 0)
		{
			if (skipped is null) AdvicePanel.Children.Add(new TextBlock { Text = Strings.Benchmark_NoAdvice, Classes = { "hint" } });
			return;
		}

		foreach (BenchmarkAdvice item in advice)
		{
			var apply = new Button { Content = Strings.Benchmark_Apply, IsEnabled = canApply, VerticalAlignment = VerticalAlignment.Center };
			apply.Click += (_, _) =>
			{
				OverlaySettings settings = OverlaySettingsStore.Load();
				OverlaySettingsStore.Save(Apply(item.Kind, settings));
				AppLogger.Info($"Performance test: applied {item.Kind}");
				apply.Content = Strings.Benchmark_Applied;
				apply.IsEnabled = false;
			};

			var text = new TextBlock { Text = AdviceText(item), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
			var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 16 };
			row.Children.Add(text);
			Grid.SetColumn(apply, 1);
			row.Children.Add(apply);
			AdvicePanel.Children.Add(new Border { Classes = { "card" }, Padding = new Thickness(16, 10), Child = row });
		}
	}

	private static string AdviceText(BenchmarkAdvice advice)
	{
		return advice.Kind switch
		{
			BenchmarkAdviceKind.SoftwareDecode => string.Format(Strings.Benchmark_AdviceSoftwareDecode, (advice.Gain * 100).ToString("0")),
			BenchmarkAdviceKind.HardwareDecode => string.Format(Strings.Benchmark_AdviceHardwareDecode, (advice.Gain * 100).ToString("0")),
			BenchmarkAdviceKind.PreviewShadowsOff => string.Format(Strings.Benchmark_AdvicePreviewShadowsOff, advice.Gain.ToString("0.0")),
			_ => Strings.Benchmark_AdvicePreviewShadowsOn
		};
	}

	private static OverlaySettings Apply(BenchmarkAdviceKind kind, OverlaySettings settings)
	{
		return kind switch
		{
			BenchmarkAdviceKind.SoftwareDecode => settings with { HardwareDecoding = false },
			BenchmarkAdviceKind.HardwareDecode => settings with { HardwareDecoding = true },
			BenchmarkAdviceKind.PreviewShadowsOff => settings with { PreviewShadows = false },
			_ => settings with { PreviewShadows = true }
		};
	}

	/// <summary>Everything the window shows as Markdown - for a bug report or comparing two computers.</summary>
	private string BuildReport(BenchmarkResult result)
	{
		OverlaySettings settings = OverlaySettingsStore.Load();
		var text = new StringBuilder();
		text.AppendLine($"# {Strings.Benchmark_Title} - OsmoOverlay {result.System.App}");
		text.AppendLine();
		text.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
		text.AppendLine();
		text.AppendLine($"## {Strings.Benchmark_Computer}");
		text.AppendLine();
		foreach ((string label, string value) in SystemRows(result)) text.AppendLine($"- {label}: {value}");
		if (result.Load.IsBusy) text.AppendLine().AppendLine($"> {string.Format(Strings.Benchmark_LoadWarning, Percent(result.Load.Cpu), Percent(result.Load.Gpu))}");
		if (result.IsUnstable) text.AppendLine().AppendLine($"> {Strings.Benchmark_UnstableWarning}");
		text.AppendLine();
		text.AppendLine($"## {Strings.Benchmark_Results}");
		text.AppendLine();
		text.AppendLine(
			$"| {Strings.Benchmark_ColumnStage} | {Strings.Benchmark_ColumnResult} | {Strings.Benchmark_ColumnRange} | {Strings.Benchmark_ColumnNotes} |");
		text.AppendLine("|---|---|---|---|");
		foreach (ResultRow row in ResultRows(result, settings)) text.AppendLine($"| {row.Stage} | {row.Result} | {row.Range} | {row.Notes} |");
		text.AppendLine();
		text.AppendLine($"## {Strings.Benchmark_SlowestStage}");
		text.AppendLine();
		text.AppendLine(BottleneckDescription(result, settings));
		if (EstimateDescription(result, settings, _totalFrames) is { } estimate) text.AppendLine().AppendLine(estimate);
		text.AppendLine();
		text.AppendLine($"## {Strings.Benchmark_Suggestions}");
		text.AppendLine();
		List<BenchmarkAdvice> advice = RenderBenchmark.Advise(result, settings);
		if (SkippedAdvice(result) is { } skipped) text.AppendLine(skipped).AppendLine();
		else if (advice.Count == 0) text.AppendLine(Strings.Benchmark_NoAdvice);
		foreach (BenchmarkAdvice item in advice) text.AppendLine($"- {AdviceText(item)}");
		return text.ToString();
	}

	private async void OnCopyReportClick(object? sender, RoutedEventArgs e)
	{
		if (_result is null || GetTopLevel(this)?.Clipboard is not { } clipboard) return;

		await clipboard.SetTextAsync(BuildReport(_result));
		CopyReportButton.Content = Strings.Benchmark_Copied;
	}

	private void OnCloseClick(object? sender, RoutedEventArgs e)
	{
		Close();
	}
}
