using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Telemetry.External;
using OsmoOverlay.Dji.Drones;

namespace OsmoOverlay.Gui;

/// <summary>
///     Fetches the DJI flight logs from a phone: the user only picks the phone (or a folder), the places the app keeps its logs are looked at
///     (PhoneFlightLogs) and the log that covers the recording is offered. The logs stay in the app's own folder, so the next recording is
///     matched without asking.
/// </summary>
public partial class PhoneLogWindow : Window
{
	private readonly FileSummary? _summary;
	private readonly CancellationTokenSource _cancel = new();
	private string? _match;
	private bool _busy;

	public string? FoundPath { get; private set; }

	public event Action<string, LogLevel>? Log;

	public PhoneLogWindow() : this(null)
	{
	}

	public PhoneLogWindow(FileSummary? summary)
	{
		InitializeComponent();
		_summary = summary;
		Opened += async (_, _) => await RefreshAsync();
		Closed += (_, _) => _cancel.Cancel();
	}

	private async Task RefreshAsync()
	{
		if (!PhoneFlightLogs.CanReadDevices)
		{
			NoDevices.IsVisible = true;
			return;
		}

		SetBusy(true);
		try
		{
			IReadOnlyList<PhoneDevice> devices = await PhoneFlightLogs.ListDevicesAsync();
			Devices.ItemsSource = devices;
			Log?.Invoke(string.Format(Strings.Phone_LogDevices, devices.Count == 0 ? "0" : string.Join(", ", devices.Select(device => device.Name))), LogLevel.Info);
			NoDevices.IsVisible = devices.Count == 0;
			if (devices.Count == 1) Devices.SelectedIndex = 0;
		}
		catch (Exception ex)
		{
			AppLogger.Warn(ex, "Listing the phones failed.");
			Show(ProblemText, string.Format(Strings.Phone_Failed, ex.Message));
		}
		finally
		{
			SetBusy(false);
		}
	}

	private async void OnRefreshClick(object? sender, RoutedEventArgs e)
	{
		await RefreshAsync();
	}

	private void OnDeviceSelected(object? sender, SelectionChangedEventArgs e)
	{
		ActionButton.IsEnabled = !_busy && (_match is not null || Devices.SelectedItem is PhoneDevice);
	}

	private async void OnActionClick(object? sender, RoutedEventArgs e)
	{
		if (_match is not null)
		{
			FoundPath = _match;
			Close();
			return;
		}

		if (Devices.SelectedItem is not PhoneDevice device) return;

		var progress = new Progress<PhoneProgress>(OnProgress);
		await RunAsync(device.Name, () => PhoneFlightLogs.CopyFromAsync(device, progress, _cancel.Token));
	}

	private async void OnFolderClick(object? sender, RoutedEventArgs e)
	{
		IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Strings.Phone_Folder });
		if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } path) return;

		var progress = new Progress<PhoneProgress>(OnProgress);
		await RunAsync(path, () => Task.Run(() => PhoneFlightLogs.ImportFromFolder(path, progress, _cancel.Token)));
	}

	private async Task RunAsync(string source, Func<Task<PhoneCopyResult>> copy)
	{
		SetBusy(true);
		Show(ProblemText, "");
		Show(VerdictText, "");
		Show(StatusText, Strings.Phone_Searching);
		Log?.Invoke(string.Format(Strings.Phone_LogSource, source), LogLevel.Info);
		Log?.Invoke(Strings.Phone_Searching, LogLevel.Info);
		try
		{
			PhoneCopyResult result = await copy();
			if (_cancel.IsCancellationRequested) return;

			_match = _summary is null ? null : ExternalTrackSource.FindFor(_summary.CameraFormat, _summary.InputPaths[0], _summary.ContainerRecordingStartUtc)?.Path;
			OpenLogsButton.IsVisible = Directory.Exists(PhoneFlightLogs.CacheFolder);
			string copied = string.Format(Strings.Phone_Done, result.Copied, result.AlreadyKept);
			Log?.Invoke(copied, LogLevel.Info);
			Log?.Invoke(string.Format(Strings.Phone_Places, result.PlacesChecked, result.Places.Count, result.Found), LogLevel.Info);
			if (_summary is null)
			{
				Show(StatusText, copied);
			}
			else if (_match is null)
			{
				Show(VerdictText, Strings.Phone_NoMatch);
				Show(StatusText, Strings.Phone_NoMatchHint + "\n" + copied + "\n" + PlacesText(result));
				Log?.Invoke(Strings.Phone_NoMatch, LogLevel.Warn);
			}
			else
			{
				MatchFile.Text = Path.GetFileName(_match);
				MatchStats.Text = copied;
				MatchPlaces.Text = PlacesText(result);
				PlacesList.ItemsSource = result.Places.OrderByDescending(place => place.Logs).Select(place => new PhoneLogPlaceRow(place.Path, place.Logs > 0 ? place.Logs.ToString() : Strings.Phone_PlaceNone, place.Logs > 0 ? 1 : 0.5)).ToList();
				Show(StatusText, "");
				Log?.Invoke(Strings.Phone_Match + ": " + Path.GetFileName(_match), LogLevel.Info);
			}

			if (_match is not null)
			{
				StepsText.IsVisible = DevicesCard.IsVisible = FolderButton.IsVisible = false;
				MatchPanel.IsVisible = ShowButton.IsVisible = true;
				ActionButton.Content = Strings.Phone_Use;
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
		{
			AppLogger.Warn(ex, "Fetching the flight logs failed.");
			Show(StatusText, "");
			Show(ProblemText, string.Format(Strings.Phone_Failed, ex.Message));
			Log?.Invoke(string.Format(Strings.Phone_Failed, ex.Message), LogLevel.Warn);
		}
		finally
		{
			if (!_cancel.IsCancellationRequested) SetBusy(false);
		}
	}

	private void OnOpenLogsClick(object? sender, RoutedEventArgs e)
	{
		ExplorerHelper.OpenFolder(PhoneFlightLogs.CacheFolder);
	}

	private void OnShowClick(object? sender, RoutedEventArgs e)
	{
		if (_match is not null) ExplorerHelper.ShowInFolder(_match);
	}

	private void OnProgress(PhoneProgress step)
	{
		if (step.File is { } file)
		{
			string copying = string.Format(Strings.Phone_Copying, file);
			StatusText.Text = copying;
			Log?.Invoke(copying, LogLevel.Info);
		}
		else if (step.Place is { } place)
		{
			Log?.Invoke(step.Logs is { } logs ? string.Format(Strings.Phone_LogPlace, place, logs) : string.Format(Strings.Phone_LogPlaceMissing, place), LogLevel.Info);
		}
	}

	private void OnCloseClick(object? sender, RoutedEventArgs e)
	{
		Close();
	}

	private void SetBusy(bool busy)
	{
		_busy = busy;
		Busy.IsVisible = busy;
		RefreshButton.IsEnabled = FolderButton.IsEnabled = Devices.IsEnabled = !busy;
		ActionButton.IsEnabled = !busy && (_match is not null || Devices.SelectedItem is PhoneDevice);
	}

	private static string PlacesText(PhoneCopyResult result)
	{
		return string.Format(Strings.Phone_Places, result.PlacesChecked, result.Places.Count(place => place.Logs > 0), result.Found);
	}

	private static void Show(TextBlock block, string text)
	{
		block.Text = text;
		block.IsVisible = text.Length > 0;
	}
}

public sealed record PhoneLogPlaceRow(string Path, string Count, double Opacity);
