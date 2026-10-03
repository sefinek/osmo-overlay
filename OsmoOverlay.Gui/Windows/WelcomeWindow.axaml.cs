using System.Security;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Dependencies;
using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Localization;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Updates;
using OsmoOverlay.Gui.Native;

namespace OsmoOverlay.Gui;

/// <summary>
///     First run, and again from Settings: what's worth setting up before the first render. Saves straight to settings.json
///     (nothing else holds these values), so whoever opens it reads the settings again afterwards.
/// </summary>
public partial class WelcomeWindow : Window
{
	private static readonly string[] StepNames =
	[
		Strings.Welcome_StepWelcome, Strings.Welcome_SupportedCameras, Strings.Common_Tools, Strings.Welcome_YourComputer, Strings.Common_SpeedCalibration,
		Strings.Welcome_RenderingAndFiles, Strings.Welcome_StepAllSet
	];
	private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

	private readonly Control[] _steps;
	private readonly Control[] _arts;
	private readonly Border[] _dots;
	private readonly bool _offerOpenRecording;
	private int _step;

	public WelcomeWindow() : this(false)
	{
	}

	/// <param name="offerOpenRecording">The last step offers to open a recording right away (OpenRecordingRequested) - first run only.</param>
	/// <param name="recordingCruisingSpeedKmh">The loaded recording's measured cruising speed, for the speed calibration; null when none is loaded.</param>
	public WelcomeWindow(bool offerOpenRecording, double? recordingCruisingSpeedKmh = null)
	{
		InitializeComponent();
		_offerOpenRecording = offerOpenRecording;
		_steps = [WelcomeStep, CameraStep, ToolsStep, HardwareStep, SpeedStep, PreferencesStep, FinishStep];
		_arts = [WelcomeArt, CameraArt, ToolsArt, HardwareArt, SpeedArt, PreferencesArt, FinishArt];
		_dots = [.. _steps.Select((_, i) => StepDot(i))];
		VersionText.Text = string.Format(Strings.Welcome_Version, AppUpdates.CurrentVersion);

		OverlaySettings settings = OverlaySettingsStore.Load();
		SpeedEditor.Load(settings.SpeedCorrectionPercent, recordingCruisingSpeedKmh);
		SmoothGpsCheck.IsChecked = settings.SmoothGpsMotion;
		WatermarkCheck.IsChecked = settings.ShowWatermark;
		OutputFolderBox.Text = settings.DefaultOutputFolder;

		ProjectAssociationCheck.IsVisible = ProjectFileAssociation.IsSupported;
		ProjectAssociationCheck.IsChecked = ProjectFileAssociation.IsSupported && ProjectFileAssociation.IsRegistered();

		ShowTools();
		ShowHardware();
		ShowStep(0);
		AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Bubble, true);
		AddHandler(KeyDownEvent, OnWindowArrowKeyDown, RoutingStrategies.Tunnel);
		Closed += (_, _) =>
		{
			OverlaySettings now = OverlaySettingsStore.Load();
			if (!now.WelcomeShown) OverlaySettingsStore.Save(now with { WelcomeShown = true });
		};
	}

	/// <summary>Opens the performance test over the given window - MainWindow's, which knows the loaded recording. Null hides the button.</summary>
	public Func<Window, Task>? OpenBenchmark
	{
		get;
		init
		{
			field = value;
			BenchmarkButton.IsVisible = value is not null;
		}
	}

	/// <summary>Finished with "Open a recording": the caller opens the file picker once this window is closed.</summary>
	public bool OpenRecordingRequested { get; private set; }

	private bool IsLastStep => _step == _steps.Length - 1;

	/// <summary>A step indicator in the side panel: a dot, a pill for the current step, and a way to jump straight to any step.</summary>
	private Border StepDot(int index)
	{
		Border dot = new()
		{
			Height = 6,
			CornerRadius = new CornerRadius(3),
			Transitions = new Transitions { new DoubleTransition { Property = WidthProperty, Duration = TimeSpan.FromMilliseconds(160) } }
		};
		Border hitArea = new() { Background = Brushes.Transparent, Padding = new Thickness(3, 8), Cursor = HandCursor, Child = dot };
		ToolTip.SetTip(hitArea, StepNames[index]);
		hitArea.PointerPressed += (_, _) => ShowStep(index);
		StepDots.Children.Add(hitArea);
		return dot;
	}

	/// <summary>Every step stays laid out on top of the others, so switching only cross-fades (the Opacity transition) and nothing jumps.</summary>
	private void ShowStep(int step)
	{
		_step = step;
		for (int i = 0; i < _steps.Length; i++)
		{
			bool current = i == step;
			_steps[i].Opacity = current ? 1 : 0;
			_steps[i].IsHitTestVisible = current;
			_steps[i].IsEnabled = current;
			_arts[i].Opacity = current ? 1 : 0;
			_dots[i].Width = current ? 20 : 6;
			_dots[i].Background = current ? Palette.Accent : Palette.StrokeStrong;
		}

		StepLabel.Text = string.Format(Strings.Welcome_StepOf, step + 1, _steps.Length);
		if (IsLastStep) ShowSummary();

		SkipButton.IsVisible = !IsLastStep;
		BackButton.IsVisible = step > 0;
		NextButton.Content = IsLastStep ? Strings.Welcome_Finish : Strings.Welcome_Next;
		NextButton.Classes.Set("accent", !(IsLastStep && _offerOpenRecording));
		OpenRecordingButton.IsVisible = IsLastStep && _offerOpenRecording;
	}

	private void GoForward()
	{
		if (!IsLastStep) ShowStep(_step + 1);
		else if (_offerOpenRecording) OpenRecording();
		else Save();
	}

	// Bubbling with handled events too: a NumericUpDown commits its text on Enter (and marks it handled) before this runs.
	private void OnWindowKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.KeyModifiers != KeyModifiers.None) return;

		bool typing = FocusManager?.GetFocusedElement() is TextBox;
		switch (e.Key)
		{
			case Key.Escape:
				Close();
				break;
			case Key.Enter when typing && _steps[_step] == SpeedStep:
				SpeedEditor.ApplyCalculation();
				break;
			case Key.Enter when !typing && !e.Handled:
				GoForward();
				break;
			default:
				return;
		}

		e.Handled = true;
	}

	// Tunneling: arrow keys otherwise move the focus between buttons (directional navigation) before a bubbling handler sees them.
	private void OnWindowArrowKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.KeyModifiers != KeyModifiers.None || FocusManager?.GetFocusedElement() is TextBox) return;

		if (e.Key == Key.Left && _step > 0) ShowStep(_step - 1);
		else if (e.Key == Key.Right && !IsLastStep) ShowStep(_step + 1);
		else return;

		e.Handled = true;
	}

	private void OnLinkButtonClick(object? sender, RoutedEventArgs e)
	{
		if ((sender as Control)?.Tag is string url) AppUpdateFlow.OpenInBrowser(url);
	}

	private void OnLinkPressed(object? sender, PointerPressedEventArgs e)
	{
		if ((sender as Control)?.Tag is string url) AppUpdateFlow.OpenInBrowser(url);
	}

	private void OnBackClick(object? sender, RoutedEventArgs e)
	{
		ShowStep(_step - 1);
	}

	private void OnNextClick(object? sender, RoutedEventArgs e)
	{
		if (IsLastStep) Save();
		else ShowStep(_step + 1);
	}

	private void OnOpenRecordingClick(object? sender, RoutedEventArgs e)
	{
		OpenRecording();
	}

	private void OpenRecording()
	{
		OpenRecordingRequested = true;
		Save();
	}

	private void OnSkipClick(object? sender, RoutedEventArgs e)
	{
		Close();
	}

	// Found tools show up at once; their versions follow when the tools have answered.
	private async void ShowTools()
	{
		ToolRows.Children.Clear();
		bool requiredMissing = false;
		bool canInstall = DependencyInstaller.CanAttemptAutoInstall();
		List<(ExternalTool Tool, TextBlock Status)> found = [];
		foreach (ExternalTool tool in RequiredTools.All)
		{
			bool available = DependencyChecker.IsAvailable(tool);
			requiredMissing |= !available && !tool.IsOptional;
			ToolRows.Children.Add(ToolRow(tool, available, canInstall, out TextBlock? status));
			if (status is not null) found.Add((tool, status));
		}

		ToolsHint.Text = requiredMissing
			? Strings.Welcome_FfmpegNeeded
			: Strings.Welcome_OptionalToolsLater;

		foreach ((ExternalTool tool, TextBlock status) in found)
		{
			if (await InstalledVersionAsync(tool) is { } version) status.Text = version;
		}
	}

	private static async Task<string?> InstalledVersionAsync(ExternalTool tool)
	{
		try
		{
			using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
			return await DependencyVersionChecker.GetInstalledVersionAsync(tool, timeout.Token);
		}
		catch (Exception ex)
		{
			AppLogger.Info($"Could not read the {tool.DisplayName} version: {ex.Message}");
			return null;
		}
	}

	/// <param name="status">The found tool's status text (its version goes there once known); null when the tool is missing.</param>
	private Border ToolRow(ExternalTool tool, bool found, bool canInstall, out TextBlock? status)
	{
		IBrush color = found ? Palette.Success : tool.IsOptional ? Palette.Warning : Palette.Danger;
		Grid row = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12 };

		row.Children.Add(new Border
		{
			Width = 28,
			Height = 28,
			CornerRadius = new CornerRadius(14),
			Background = Palette.Tint(color, 0.15),
			Child = new IconView
			{
				Data = found ? Icons.Check : Icons.Warning,
				Foreground = color,
				Width = 14,
				Height = 14,
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center
			}
		});

		StackPanel name = new() { VerticalAlignment = VerticalAlignment.Center };
		name.Children.Add(new TextBlock { Text = tool.DisplayName, FontWeight = FontWeight.SemiBold });
		name.Children.Add(new TextBlock
		{
			Text = tool.IsOptional ? Strings.Welcome_Optional : Strings.Welcome_Required, FontSize = 12, Foreground = Palette.TextMuted
		});
		Grid.SetColumn(name, 1);
		row.Children.Add(name);

		Control side;
		status = null;
		if (!found && canInstall)
		{
			Button install = new() { Content = Strings.DependencyPrompt_Install, VerticalAlignment = VerticalAlignment.Center };
			if (!tool.IsOptional) install.Classes.Add("accent");
			install.Classes.Add("wizard");
			install.Click += async (_, _) => await InstallAsync(tool, install);
			side = install;
		}
		else
		{
			TextBlock text = new()
			{
				Text = found ? Strings.Welcome_Found : Strings.Summary_TelemetryNotFound, FontSize = 12, FontWeight = FontWeight.SemiBold,
				Foreground = color
			};
			if (found) status = text;
			side = new Border
			{
				Padding = new Thickness(10, 3),
				CornerRadius = new CornerRadius(10),
				VerticalAlignment = VerticalAlignment.Center,
				Background = Palette.Tint(color, 0.15),
				Child = text
			};
		}

		Grid.SetColumn(side, 2);
		row.Children.Add(side);

		Border card = new() { Padding = new Thickness(12, 10), Child = row };
		card.Classes.Add("card");
		return card;
	}

	// AppLogger.Notify, as Settings' About tab does, so the package manager's output lands in the main window's log.
	private async Task InstallAsync(ExternalTool tool, Button button)
	{
		button.IsEnabled = false;
		button.Content = Strings.Welcome_Installing;
		ToolsHint.Text = string.Format(Strings.Welcome_InstallingHint, tool.DisplayName);
		AppLogger.Notify(string.Format(Strings.Welcome_InstallingTool, tool.DisplayName));

		InstallResult result;
		try
		{
			result = await DependencyInstaller.InstallAsync(tool, AppLogger.Notify, CancellationToken.None);
		}
		catch (Exception ex)
		{
			result = new InstallResult(false, ex.Message);
		}

		AppLogger.Notify(result.Message);
		if (result.Success)
		{
			ShowTools();
			// FFmpeg is what the encoder and decoder checks run.
			ShowHardware();
			return;
		}

		button.Content = Strings.Welcome_Retry;
		button.IsEnabled = true;
		ToolsHint.Text = result.Message;
	}

	// Rows show "Checking..." until HardwareCheck has answered (the encoder and decoder runs take a second or two).
	private async void ShowHardware()
	{
		HardwareRows.Children.Clear();
		HardwareRows.Children.Add(new TextBlock { Text = Strings.Welcome_Checking, Classes = { "rowText" } });
		HardwareHint.Text = "";

		HardwareReport report;
		try
		{
			report = await HardwareCheck.RunAsync(OverlaySettingsStore.Load().DefaultOutputFolder, CancellationToken.None);
		}
		catch (Exception ex)
		{
			AppLogger.Warn(ex, "Hardware check failed");
			HardwareRows.Children.Clear();
			HardwareHint.Text = ex.Message;
			return;
		}

		AppLogger.Info($"Hardware check: {report}, overall {report.Overall}");
		BenchmarkSystem system = report.System;
		HardwareRows.Children.Clear();
		AddHardwareRow(Strings.Welcome_CheckSystem, report.SystemStatus, system.Os, Strings.Welcome_CheckSystemProblem);
		AddHardwareRow(Strings.Welcome_CheckProcessor, report.ProcessorStatus,
			$"{system.Cpu ?? Strings.Benchmark_Unknown}, {Plural.Format(Strings.Benchmark_Threads, system.Threads)}",
			report.ProcessorStatus == CheckStatus.Problem ? Strings.Welcome_CheckProcessorProblem : Strings.Welcome_CheckProcessorWarning);
		AddHardwareRow(Strings.Welcome_CheckMemory, report.MemoryStatus, FormatHelper.FormatBytes(system.MemoryBytes),
			report.MemoryStatus == CheckStatus.Problem ? Strings.Welcome_CheckMemoryProblem : Strings.Welcome_CheckMemoryWarning);
		AddHardwareRow(Strings.Welcome_CheckEncoder, report.EncoderStatus, report.Encoder switch
		{
			null => Strings.Welcome_CheckNeedsFfmpeg,
			{ } encoder when FfmpegPipeline.IsGpuEncoder(encoder) =>
				string.Format(Strings.Welcome_CheckEncoderOk, report.EncoderCard is { } card ? $"{encoder} ({card})" : encoder),
			{ } encoder => string.Format(Strings.Welcome_CheckEncoderWarning, encoder)
		}, null);
		AddHardwareRow(Strings.Welcome_CheckDecoder, report.DecoderStatus, report.HardwareDecode switch
		{
			null => Strings.Welcome_CheckNeedsFfmpeg,
			true => string.Format(Strings.Welcome_CheckDecoderOk, report.HardwareDecodeMethod),
			false => Strings.Welcome_CheckDecoderWarning
		}, null);
		AddHardwareRow(Strings.Welcome_CheckDisk, report.DiskStatus,
			report.FreeBytes is { } free ? string.Format(Strings.Welcome_CheckDiskValue, FormatHelper.FormatBytes(free), report.OutputFolder) : report.OutputFolder,
			report.DiskStatus == CheckStatus.Problem
				? Strings.Welcome_CheckDiskProblem
				: string.Format(Strings.Welcome_CheckDiskWarning, FormatHelper.FormatBytes(HardwareReport.HourOf4KBytes)));

		bool needsFfmpeg = report.Encoder is null || report.HardwareDecode is null;
		HardwareHint.Text = report.Overall switch
		{
			CheckStatus.Problem => Strings.Welcome_HardwareProblem,
			CheckStatus.Warning => Strings.Welcome_HardwareWarning,
			_ when needsFfmpeg => Strings.Welcome_HardwareUnknown,
			_ => Strings.Welcome_HardwareOk
		};
	}

	/// <param name="note">Said below the value when the check isn't OK - null when the value says it already.</param>
	private void AddHardwareRow(string name, CheckStatus status, string value, string? note)
	{
		IBrush color = status switch
		{
			CheckStatus.Ok => Palette.Success,
			CheckStatus.Warning => Palette.Warning,
			CheckStatus.Problem => Palette.Danger,
			_ => Palette.TextMuted
		};
		Grid row = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12 };
		row.Children.Add(new Border
		{
			Width = 24,
			Height = 24,
			CornerRadius = new CornerRadius(12),
			Background = Palette.Tint(color, 0.15),
			VerticalAlignment = VerticalAlignment.Center,
			Child = new IconView
			{
				Data = status == CheckStatus.Ok ? Icons.Check : Icons.Warning,
				Foreground = color,
				Width = 12,
				Height = 12,
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center
			}
		});

		StackPanel text = new() { VerticalAlignment = VerticalAlignment.Center };
		text.Children.Add(new TextBlock { Text = name, FontWeight = FontWeight.SemiBold, FontSize = 13 });
		text.Children.Add(new TextBlock { Text = value, Classes = { "rowText" } });
		if (note is not null && status is CheckStatus.Warning or CheckStatus.Problem)
			text.Children.Add(new TextBlock { Text = note, Classes = { "rowText" }, Foreground = color });
		Grid.SetColumn(text, 1);
		row.Children.Add(text);

		Border pill = new()
		{
			Padding = new Thickness(10, 3),
			CornerRadius = new CornerRadius(10),
			VerticalAlignment = VerticalAlignment.Center,
			Background = Palette.Tint(color, 0.15),
			Child = new TextBlock
			{
				Text = status switch
				{
					CheckStatus.Ok => Strings.Welcome_CheckOk,
					CheckStatus.Warning => Strings.Welcome_CheckWarning,
					CheckStatus.Problem => Strings.Welcome_CheckProblem,
					_ => Strings.Welcome_CheckUnknown
				},
				FontSize = 12,
				FontWeight = FontWeight.SemiBold,
				Foreground = color
			}
		};
		Grid.SetColumn(pill, 2);
		row.Children.Add(pill);

		Border card = new() { Padding = new Thickness(12, 8), Child = row };
		card.Classes.Add("card");
		HardwareRows.Children.Add(card);
	}

	private async void OnBenchmarkClick(object? sender, RoutedEventArgs e)
	{
		if (OpenBenchmark is not null) await OpenBenchmark(this);
	}

	private void ShowSummary()
	{
		double correction = SpeedEditor.Percent;
		List<(string Label, string Value)> rows =
		[
			(Strings.Welcome_SummarySpeedCorrection, correction > 0 ? $"+{correction:0.0}%" : Strings.Main_None),
			(Strings.Welcome_SummaryGpsSmoothing, SmoothGpsCheck.IsChecked == true ? Strings.Common_On : Strings.Main_Off),
			(Strings.Welcome_SummaryWatermark, WatermarkCheck.IsChecked == true ? Strings.Common_On : Strings.Main_Off),
			(Strings.Welcome_SummaryOutputFolder,
				string.IsNullOrWhiteSpace(OutputFolderBox.Text) ? Strings.Welcome_NextToTheSourceFile : OutputFolderBox.Text)
		];
		if (ProjectFileAssociation.IsSupported)
		{
			rows.Add((Strings.Welcome_SummaryOvprojFiles,
				ProjectAssociationCheck.IsChecked == true ? Strings.Welcome_SummaryOpenWithApp : Strings.Welcome_SummaryNotAssociated));
		}

		SummaryGrid.RowDefinitions.Clear();
		SummaryGrid.Children.Clear();
		for (int i = 0; i < rows.Count; i++)
		{
			SummaryGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

			TextBlock label = new() { Text = rows[i].Label, FontSize = 13, Foreground = Palette.TextMuted };
			Grid.SetRow(label, i);
			SummaryGrid.Children.Add(label);

			TextBlock value = new() { Text = rows[i].Value, FontSize = 13, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
			ToolTip.SetTip(value, rows[i].Value);
			Grid.SetRow(value, i);
			Grid.SetColumn(value, 1);
			SummaryGrid.Children.Add(value);
		}
	}

	private async void OnBrowseOutputClick(object? sender, RoutedEventArgs e)
	{
		IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
		{
			Title = Strings.Welcome_DefaultOutputFolder,
			AllowMultiple = false
		});

		if (folders.Count > 0) OutputFolderBox.Text = folders[0].Path.LocalPath;
	}

	private void OnClearOutputClick(object? sender, RoutedEventArgs e)
	{
		OutputFolderBox.Text = null;
	}

	private void Save()
	{
		OverlaySettingsStore.Save(OverlaySettingsStore.Load() with
		{
			SpeedCorrectionPercent = SpeedEditor.Percent,
			SmoothGpsMotion = SmoothGpsCheck.IsChecked == true,
			ShowWatermark = WatermarkCheck.IsChecked == true,
			DefaultOutputFolder = string.IsNullOrWhiteSpace(OutputFolderBox.Text) ? null : OutputFolderBox.Text,
			WelcomeShown = true
		});

		ApplyProjectAssociation();
		Close();
	}

	private void ApplyProjectAssociation()
	{
		if (!ProjectFileAssociation.IsSupported) return;

		bool wanted = ProjectAssociationCheck.IsChecked == true;
		if (wanted == ProjectFileAssociation.IsRegistered()) return;

		try
		{
			if (wanted) ProjectFileAssociation.Register();
			else ProjectFileAssociation.Unregister();
		}
		catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
		{
			AppLogger.Error(ex, string.Format(Strings.Settings_AssociationFailed, ex.Message));
		}
	}
}
