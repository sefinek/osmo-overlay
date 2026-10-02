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
using OsmoOverlay.Core.Telemetry;
using OsmoOverlay.Core.Updates;
using OsmoOverlay.Gui.Native;

namespace OsmoOverlay.Gui;

/// <summary>
///     First run, and again from Settings: what's worth setting up before the first render. Saves straight to settings.json
///     (nothing else holds these values), so whoever opens it reads the settings again afterwards.
/// </summary>
public partial class WelcomeWindow : Window
{
	private readonly Control[] _steps;
	private readonly Control[] _arts;
	private readonly Border[] _dots;
	private int _step;

	public WelcomeWindow()
	{
		InitializeComponent();
		_steps = [WelcomeStep, ToolsStep, SpeedStep, PreferencesStep, SupportStep];
		_arts = [WelcomeArt, ToolsArt, SpeedArt, PreferencesArt, SupportArt];
		_dots =
		[
			.. _steps.Select(_ => new Border
			{
				Height = 6,
				CornerRadius = new CornerRadius(3),
				Transitions = new Transitions { new DoubleTransition { Property = Border.WidthProperty, Duration = TimeSpan.FromMilliseconds(160) } }
			})
		];
		foreach (Border dot in _dots) StepDots.Children.Add(dot);
		VersionText.Text = $"Version {AppUpdates.CurrentVersion}";

		CorrectionBox.Maximum = (decimal)SpeedCalibration.MaxPercent;

		OverlaySettings settings = OverlaySettingsStore.Load();
		CorrectionBox.Value = (decimal)SpeedCalibration.Clamp(settings.SpeedCorrectionPercent);
		SmoothGpsCheck.IsChecked = settings.SmoothGpsMotion;
		WatermarkCheck.IsChecked = settings.ShowWatermark;
		OutputFolderBox.Text = settings.DefaultOutputFolder;

		ProjectAssociationCheck.IsVisible = ProjectFileAssociation.IsSupported;
		ProjectAssociationCheck.IsChecked = ProjectFileAssociation.IsSupported && ProjectFileAssociation.IsRegistered();

		ShowTools();
		ShowStep(0);
		Closed += (_, _) =>
		{
			OverlaySettings now = OverlaySettingsStore.Load();
			if (!now.WelcomeShown) OverlaySettingsStore.Save(now with { WelcomeShown = true });
		};
	}

	private void ShowStep(int step)
	{
		_step = step;
		for (int i = 0; i < _steps.Length; i++)
		{
			_steps[i].IsVisible = i == step;
			_arts[i].IsVisible = i == step;
			_dots[i].Width = i == step ? 20 : 6;
			_dots[i].Background = i == step ? Palette.Accent : Palette.StrokeStrong;
		}

		StepLabel.Text = $"STEP {step + 1} OF {_steps.Length}";

		BackButton.IsVisible = step > 0;
		NextButton.Content = step == _steps.Length - 1 ? "Finish" : "Next";
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
		if (_step < _steps.Length - 1) ShowStep(_step + 1);
		else Save();
	}

	private void ShowTools()
	{
		bool requiredMissing = false;
		foreach (ExternalTool tool in RequiredTools.All)
		{
			bool found = DependencyChecker.IsAvailable(tool);
			requiredMissing |= !found && !tool.IsOptional;
			ToolRows.Children.Add(ToolRow(tool, found));
		}

		ToolsHint.Text = requiredMissing
			? "A required tool is missing. Install it from Settings, About tab, or the app offers to at startup."
			: "Missing optional tools can be installed from Settings, About tab.";
	}

	private static Border ToolRow(ExternalTool tool, bool found)
	{
		IBrush status = found ? Palette.Success : tool.IsOptional ? Palette.Warning : Palette.Danger;
		Grid row = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12 };

		Border badge = new()
		{
			Width = 28,
			Height = 28,
			CornerRadius = new CornerRadius(14),
			Background = Palette.Tint(status, 0.15),
			Child = new IconView
			{
				Data = found ? Icons.Check : Icons.Warning,
				Foreground = status,
				Width = 14,
				Height = 14,
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center
			}
		};
		row.Children.Add(badge);

		StackPanel name = new() { VerticalAlignment = VerticalAlignment.Center };
		name.Children.Add(new TextBlock { Text = tool.DisplayName, FontWeight = FontWeight.SemiBold });
		name.Children.Add(new TextBlock { Text = tool.IsOptional ? "Optional" : "Required", FontSize = 12, Foreground = Palette.TextMuted });
		Grid.SetColumn(name, 1);
		row.Children.Add(name);

		Border pill = new()
		{
			Padding = new Thickness(10, 3),
			CornerRadius = new CornerRadius(10),
			VerticalAlignment = VerticalAlignment.Center,
			Background = Palette.Tint(status, 0.15),
			Child = new TextBlock { Text = found ? "Found" : "Not found", FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = status }
		};
		Grid.SetColumn(pill, 2);
		row.Children.Add(pill);

		Border card = new() { Padding = new Thickness(12, 10), Child = row };
		card.Classes.Add("card");
		return card;
	}

	private void OnCalculateClick(object? sender, RoutedEventArgs e)
	{
		if (RealSpeedBox.Value is not { } real || ShownSpeedBox.Value is not { } shown) return;

		CorrectionBox.Value = (decimal)SpeedCalibration.PercentFor((double)real, (double)shown, (double)(CorrectionBox.Value ?? 0));
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
			SpeedCorrectionPercent = SpeedCalibration.Clamp((double)(CorrectionBox.Value ?? 0)),
			SmoothGpsMotion = SmoothGpsCheck.IsChecked == true,
			ShowWatermark = WatermarkCheck.IsChecked == true,
			DefaultOutputFolder = string.IsNullOrWhiteSpace(OutputFolderBox.Text) ? null : OutputFolderBox.Text,
			WelcomeShown = true
		});

		ApplyProjectAssociation();
		Close();
	}

	private void OnSkipClick(object? sender, RoutedEventArgs e)
	{
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
		catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
		{
			AppLogger.Error(ex, $"Could not change the .ovproj association: {ex.Message}");
		}
	}
}
