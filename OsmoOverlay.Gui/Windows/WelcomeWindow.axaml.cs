using System.Security;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using OsmoOverlay.Core.Dependencies;
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
	private static readonly string[] StepNames = ["Welcome", "Your camera", "Tools", "Speed calibration", "Rendering and files", "All set"];
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
		_steps = [WelcomeStep, CameraStep, ToolsStep, SpeedStep, PreferencesStep, FinishStep];
		_arts = [WelcomeArt, CameraArt, ToolsArt, SpeedArt, PreferencesArt, FinishArt];
		_dots = [.. _steps.Select((_, i) => StepDot(i))];
		VersionText.Text = $"Version {AppUpdates.CurrentVersion}";

		OverlaySettings settings = OverlaySettingsStore.Load();
		SpeedEditor.Load(settings.SpeedCorrectionPercent, recordingCruisingSpeedKmh);
		SmoothGpsCheck.IsChecked = settings.SmoothGpsMotion;
		WatermarkCheck.IsChecked = settings.ShowWatermark;
		OutputFolderBox.Text = settings.DefaultOutputFolder;

		ProjectAssociationCheck.IsVisible = ProjectFileAssociation.IsSupported;
		ProjectAssociationCheck.IsChecked = ProjectFileAssociation.IsSupported && ProjectFileAssociation.IsRegistered();

		ShowTools();
		ShowStep(0);
		AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Bubble, true);
		AddHandler(KeyDownEvent, OnWindowArrowKeyDown, RoutingStrategies.Tunnel);
		Closed += (_, _) =>
		{
			OverlaySettings now = OverlaySettingsStore.Load();
			if (!now.WelcomeShown) OverlaySettingsStore.Save(now with { WelcomeShown = true });
		};
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

		StepLabel.Text = $"STEP {step + 1} OF {_steps.Length}";
		if (IsLastStep) ShowSummary();

		SkipButton.IsVisible = !IsLastStep;
		BackButton.IsVisible = step > 0;
		NextButton.Content = IsLastStep ? "Finish" : "Next";
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
			? "FFmpeg is needed for the preview and every render. Install it here, or later from Settings > About."
			: "Optional tools can also be installed later from Settings > About.";

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
		name.Children.Add(new TextBlock { Text = tool.IsOptional ? "Optional" : "Required", FontSize = 12, Foreground = Palette.TextMuted });
		Grid.SetColumn(name, 1);
		row.Children.Add(name);

		Control side;
		status = null;
		if (!found && canInstall)
		{
			Button install = new() { Content = "Install", VerticalAlignment = VerticalAlignment.Center };
			if (!tool.IsOptional) install.Classes.Add("accent");
			install.Classes.Add("wizard");
			install.Click += async (_, _) => await InstallAsync(tool, install);
			side = install;
		}
		else
		{
			TextBlock text = new() { Text = found ? "Found" : "Not found", FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = color };
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
		button.Content = "Installing...";
		ToolsHint.Text = $"Installing {tool.DisplayName}. This can take a minute - the main window's log shows the progress.";
		AppLogger.Notify($"Installing {tool.DisplayName}...");

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
			return;
		}

		button.Content = "Retry";
		button.IsEnabled = true;
		ToolsHint.Text = result.Message;
	}

	private void ShowSummary()
	{
		double correction = SpeedEditor.Percent;
		List<(string Label, string Value)> rows =
		[
			("Speed correction", correction > 0 ? $"+{correction:0.0}%" : "None"),
			("GPS smoothing", SmoothGpsCheck.IsChecked == true ? "On" : "Off"),
			("Watermark", WatermarkCheck.IsChecked == true ? "On" : "Off"),
			("Output folder", string.IsNullOrWhiteSpace(OutputFolderBox.Text) ? "Next to the source file" : OutputFolderBox.Text)
		];
		if (ProjectFileAssociation.IsSupported)
			rows.Add((".ovproj files", ProjectAssociationCheck.IsChecked == true ? "Open with OsmoOverlay" : "Not associated"));

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
			Title = "Default output folder",
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
			AppLogger.Error(ex, $"Could not change the .ovproj association: {ex.Message}");
		}
	}
}
