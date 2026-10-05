using System.Security;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Dependencies;
using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Localization;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Mapping;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Updates;
using OsmoOverlay.Gui.Native;
using SkiaSharp;

namespace OsmoOverlay.Gui;

/// <summary>
///     First run, and again from Settings: what's worth setting up before the first render. Saves straight to settings.json
///     (nothing else holds these values), so whoever opens it reads the settings again afterwards.
/// </summary>
public partial class WelcomeWindow : Window
{
	private static readonly string[] StepNames =
	[
		Strings.Welcome_StepWelcome, Strings.Welcome_SupportedCameras, Strings.Common_Tools, Strings.Welcome_YourComputer, Strings.Welcome_MapTitle,
		Strings.Common_SpeedCalibration, Strings.Welcome_RenderingAndFiles, Strings.Welcome_StepAllSet
	];
	private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

	private readonly Control[] _steps;
	private readonly Control[] _arts;
	private readonly Border[] _dots;
	private readonly bool _offerOpenRecording;
	// Warsaw's Old Town and Castle Square, a few tiles shown near their own size: the satellite a level further out so
	// whole blocks show, the streets a level closer so their names read as in the widget.
	private static readonly (double Lat, double Lon)[] MapPreviewArea = [(52.2477, 21.0103), (52.2491, 21.0157)];
	private const int SatellitePreviewZoom = 16;
	private const int StreetsPreviewZoom = 17;
	private readonly Dictionary<string, string>? _mapApiKeys;
	private readonly string? _defaultMapProvider;
	private string? _mapPreviewKey;
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
		_steps = [WelcomeStep, CameraStep, ToolsStep, HardwareStep, MapStep, SpeedStep, PreferencesStep, FinishStep];
		_arts = [WelcomeArt, CameraArt, ToolsArt, HardwareArt, MapArt, SpeedArt, PreferencesArt, FinishArt];
		_dots = [.. _steps.Select((_, i) => StepDot(i))];
		VersionText.Text = string.Format(Strings.Welcome_Version, AppUpdates.CurrentVersion);

		OverlaySettings settings = OverlaySettingsStore.Load();
		SpeedEditor.Load(settings.SpeedCorrectionPercent, recordingCruisingSpeedKmh);
		SmoothGpsCheck.IsChecked = settings.SmoothGpsMotion;
		WatermarkCheck.IsChecked = settings.ShowWatermark;
		OutputFolderBox.Text = settings.DefaultOutputFolder;
		EsriKeyBox.Text = new MapSources(settings.MapApiKeys).ApiKey(MapProviders.EsriKeyGroup);
		ShowMapKeyStatus();
		_mapApiKeys = settings.MapApiKeys;
		_defaultMapProvider = settings.DefaultMapProvider;
		ShowMapStyle();

		ProjectAssociationCheck.IsVisible = ProjectFileAssociation.IsSupported;
		ProjectAssociationCheck.IsChecked = ProjectFileAssociation.IsSupported && ProjectFileAssociation.IsRegistered();

		ShowTools();
		ShowHardware();
		ShowStep(0);
		AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Bubble, true);
		AddHandler(KeyDownEvent, OnWindowArrowKeyDown, RoutingStrategies.Tunnel);
		Opened += async (_, _) => await CheckConnectionAsync();
		Closed += (_, _) =>
		{
			_connectionLifetime.Cancel();
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

	/// <summary>
	///     The active preset's Map widget's own provider (MapWidgetElement.MapProviderId), null when it follows the default
	///     style. Its own provider leaves both styles unticked, saying which it is; ticking one gives the widget back to it.
	/// </summary>
	public string? MapWidgetProvider
	{
		get;
		init
		{
			field = value;
			ShowMapStyle();
		}
	}

	/// <summary>Gives every Map widget of the active preset back to the default style - a style was ticked here.</summary>
	public Action? ClearMapWidgetProvider { get; init; }

	private bool MapWidgetHasOwnProvider =>
		MapWidgetProvider is { } own && own is not (MapProviders.AutoSatelliteId or MapProviders.StreetsAutoId);

	/// <summary>The style the Map widget really follows: its own best-available pick, else the default; none when it has its own provider.</summary>
	private void ShowMapStyle()
	{
		string style = MapWidgetProvider is { } picked && picked is MapProviders.AutoSatelliteId or MapProviders.StreetsAutoId
			? picked
			: _defaultMapProvider ?? MapProviders.AutoSatelliteId;
		bool own = MapWidgetHasOwnProvider;
		SatelliteChoice.IsChecked = !own && style == MapProviders.AutoSatelliteId;
		StreetsChoice.IsChecked = !own && style == MapProviders.StreetsAutoId;
		MapOwnProviderHint.IsVisible = own;
		if (own)
		{
			string name = MapProviders.BuiltIn.FirstOrDefault(p => p.Id == MapWidgetProvider)?.Name ?? Strings.MapSource_Custom;
			MapOwnProviderHint.Text = string.Format(Strings.Welcome_MapOwnProvider, name);
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

	private void ShowStep(int step)
	{
		_step = step;
		for (int i = 0; i < _steps.Length; i++)
		{
			bool current = i == step;
			_steps[i].IsVisible = current;
			_steps[i].Opacity = current ? 1 : 0;
			_steps[i].IsHitTestVisible = current;
			_steps[i].IsEnabled = current;
			_arts[i].Opacity = current ? 1 : 0;
			_dots[i].Width = current ? 20 : 6;
			_dots[i].Background = current ? Palette.Accent : Palette.StrokeStrong;
		}

		StepScrollViewer.Offset = default;
		StepLabel.Text = string.Format(Strings.Welcome_StepOf, step + 1, _steps.Length);
		if (_steps[step] == MapStep) ShowMapPreviews();
		if (_steps[step] == PreferencesStep) _ = UpdateOutputDiskSpaceAsync();
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

	private void OnGetKeyPressed(object? sender, PointerPressedEventArgs e)
	{
		FlyoutBase.ShowAttachedFlyout(ArcGisLink);
	}

	private void OnMapPreviewPressed(object? sender, PointerPressedEventArgs e)
	{
		bool streets = (sender as Control)?.Tag as string == "Streets";
		SatelliteChoice.IsChecked = !streets;
		StreetsChoice.IsChecked = streets;
	}

	private void OnEsriKeyTextChanged(object? sender, TextChangedEventArgs e)
	{
		ShowMapKeyStatus();
	}

	private void ShowMapKeyStatus()
	{
		bool hasKey = !string.IsNullOrWhiteSpace(EsriKeyBox.Text);
		MapRuleText.Text = hasKey ? Strings.Welcome_MapRuleWithKey : Strings.Welcome_MapRuleWithoutKey;
		MapRuleIcon.Data = hasKey ? Icons.Check : Icons.Warning;
		MapRuleIcon.Foreground = hasKey ? Palette.Success : Palette.Warning;
	}

	private void OnEsriKeyLostFocus(object? sender, RoutedEventArgs e)
	{
		ShowMapPreviews();
	}

	/// <summary>
	///     Both styles as they'd look with the key in the box (or without one), fetched like a render's map. Again only when the
	///     key changed or a fetch failed; a slower, older fetch is dropped.
	/// </summary>
	private async void ShowMapPreviews()
	{
		string key = EsriKeyBox.Text?.Trim() ?? "";
		bool showLabels = OverlaySettingsStore.Load().ShowEsriMapLabels;
		string previewKey = key + "|" + showLabels;
		if (previewKey == _mapPreviewKey) return;
		_mapPreviewKey = previewKey;

		Dictionary<string, string> keys = _mapApiKeys is { } existing ? new Dictionary<string, string>(existing) : [];
		if (key.Length == 0) keys.Remove(MapProviders.EsriKeyGroup);
		else keys[MapProviders.EsriKeyGroup] = key;
		var sources = new MapSources(keys, ShowEsriLabels: showLabels);

		bool[] shown = await Task.WhenAll(
			ShowMapPreviewAsync(SatellitePreview, SatellitePreviewStatus, SatellitePreviewCredit, sources, MapProviders.AutoSatelliteId, SatellitePreviewZoom, previewKey),
			ShowMapPreviewAsync(StreetsPreview, StreetsPreviewStatus, StreetsPreviewCredit, sources, MapProviders.StreetsAutoId, StreetsPreviewZoom, previewKey));
		// Coming back to the step tries a failed one again.
		if (previewKey == _mapPreviewKey && !shown.All(ok => ok)) _mapPreviewKey = null;
	}

	private async Task<bool> ShowMapPreviewAsync(Image image, TextBlock status, TextBlock credit, MapSources sources, string providerId, int zoom, string key)
	{
		image.Source = null;
		credit.IsVisible = false;
		status.Text = Strings.Welcome_MapPreviewLoading;
		Bitmap? preview = await MapPreviewAsync(sources.UrlTemplate(providerId), zoom, sources.CacheMaxAge(providerId), sources.LabelsUrlTemplate(providerId));
		if (key != _mapPreviewKey) return true;

		image.Source = preview;
		credit.Text = sources.Attribution(providerId);
		credit.IsVisible = preview is not null;
		status.Text = preview is null ? Strings.Welcome_MapPreviewFailed : "";
		return preview is not null;
	}

	private static async Task<Bitmap?> MapPreviewAsync(string urlTemplate, int zoom, TimeSpan? cacheMaxAge, string? labelsUrlTemplate)
	{
		try
		{
			using RouteMapMosaic? mosaic = await Task.Run(() => RouteMapMosaic.BuildAsync(MapPreviewArea, urlTemplate, zoom, 0, CancellationToken.None,
				targetAspectRatio: 2.0, cacheMaxAge: cacheMaxAge, labelsUrlTemplate: labelsUrlTemplate));
			if (mosaic is null) return null;

			using SKData png = mosaic.Image.Encode(SKEncodedImageFormat.Png, 100);
			using var stream = new MemoryStream(png.ToArray());
			return new Bitmap(stream);
		}
		catch (Exception ex)
		{
			AppLogger.Info($"Map preview failed: {ex.Message}");
			return null;
		}
	}

	private void ShowSummary()
	{
		double correction = SpeedEditor.Percent;
		List<(string Label, string Value)> rows =
		[
			(Strings.Welcome_SummarySpeedCorrection, correction > 0 ? $"+{correction:0.0}%" : Strings.Main_None),
			(Strings.Welcome_SummaryGpsSmoothing, SmoothGpsCheck.IsChecked == true ? Strings.Common_On : Strings.Main_Off),
			(Strings.Welcome_MapTitle, string.Format(Strings.Welcome_SummaryMapValue,
				StreetsChoice.IsChecked == true ? Strings.Welcome_MapStreets
				: SatelliteChoice.IsChecked == true ? Strings.Welcome_MapSatellite
				: Strings.Welcome_SummaryMapOwn,
				string.IsNullOrWhiteSpace(EsriKeyBox.Text) ? Strings.Welcome_SummaryMapNoKey : Strings.Welcome_SummaryMapEsriKey)),
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

	private async void OnCalibrationPreviewClick(object? sender, RoutedEventArgs e) =>
		await SpeedCalibrationPreviewWindow.ShowForEditorAsync(this, SpeedEditor);

	private async void OnBrowseOutputClick(object? sender, RoutedEventArgs e)
	{
		IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
		{
			Title = Strings.Welcome_DefaultOutputFolder,
			AllowMultiple = false
		});

		if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
		{
			OutputFolderBox.Text = path;
			await UpdateOutputDiskSpaceAsync();
		}
	}

	private void OnClearOutputClick(object? sender, RoutedEventArgs e)
	{
		OutputFolderBox.Text = null;
		OutputDiskPanel.IsVisible = false;
	}

	private async Task UpdateOutputDiskSpaceAsync()
	{
		string? folder = OutputFolderBox.Text;
		OutputDiskPanel.IsVisible = false;
		if (string.IsNullOrWhiteSpace(folder)) return;

		long? available = await Task.Run(() => RenderDiskSpace.AvailableBytes(Path.Combine(folder, ".")));
		if (OutputFolderBox.Text != folder) return;

		OutputDiskSpaceText.Text = available is { } bytes
			? string.Format(Strings.Welcome_OutputDiskFree, FormatHelper.FormatBytes(bytes))
			: Strings.Welcome_OutputDiskUnknown;
		long recommended = RenderDiskSpace.RequiredBytes(HardwareReport.HourOf4KBytes);
		OutputDiskWarningText.IsVisible = available is { } free && free < recommended;
		OutputDiskWarningText.Text = string.Format(Strings.Welcome_OutputDiskLow, FormatHelper.FormatBytes(recommended));
		OutputDiskPanel.IsVisible = true;
	}

	private void Save()
	{
		OverlaySettings settings = OverlaySettingsStore.Load();
		Dictionary<string, string> keys = settings.MapApiKeys is { } existing ? new Dictionary<string, string>(existing) : [];
		string esriKey = EsriKeyBox.Text?.Trim() ?? "";
		if (esriKey.Length == 0) keys.Remove(MapProviders.EsriKeyGroup);
		else keys[MapProviders.EsriKeyGroup] = esriKey;

		OverlaySettingsStore.Save(settings with
		{
			MapApiKeys = keys.Count == 0 ? null : keys,
			DefaultMapProvider = StreetsChoice.IsChecked == true ? MapProviders.StreetsAutoId
			: SatelliteChoice.IsChecked == true ? null
			: settings.DefaultMapProvider,
			SpeedCorrectionPercent = SpeedEditor.Percent,
			SmoothGpsMotion = SmoothGpsCheck.IsChecked == true,
			ShowWatermark = WatermarkCheck.IsChecked == true,
			DefaultOutputFolder = string.IsNullOrWhiteSpace(OutputFolderBox.Text) ? null : OutputFolderBox.Text,
			WelcomeShown = true
		});

		ApplyProjectAssociation();
		if (MapWidgetHasOwnProvider && (SatelliteChoice.IsChecked == true || StreetsChoice.IsChecked == true)) ClearMapWidgetProvider?.Invoke();
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
