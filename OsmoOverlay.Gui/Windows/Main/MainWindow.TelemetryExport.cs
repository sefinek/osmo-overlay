using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Gui;

/// <summary>Saving the recording's processed telemetry (TelemetryExport) as a CSV table or a GPX track.</summary>
public partial class MainWindow
{
	private static readonly FilePickerFileType CsvFileType = new(Strings.Telemetry_FileTypeCsv) { Patterns = [$"*{TelemetryExport.CsvExtension}"] };
	private static readonly FilePickerFileType GpxFileType = new(Strings.Telemetry_FileTypeGpx) { Patterns = [$"*{TelemetryExport.GpxExtension}"] };

	private async void OnExportTelemetryClick(object? sender, RoutedEventArgs e)
	{
		if (_summary is not { TelemetryFrames: { Count: > 0 } raw } summary) return;

		IStorageFile? file = await StorageProvider.SaveRememberedAsync("telemetry-export", new FilePickerSaveOptions
		{
			Title = Strings.Telemetry_ExportTitle,
			SuggestedFileName = Path.GetFileNameWithoutExtension(summary.InputPaths[0]),
			DefaultExtension = TelemetryExport.CsvExtension.TrimStart('.'),
			FileTypeChoices = [CsvFileType, GpxFileType]
		});
		if (file is null) return;

		string path = file.Path.LocalPath;
		if (!TelemetryExport.IsSupported(path)) path += TelemetryExport.CsvExtension;
		bool track = path.EndsWith(TelemetryExport.GpxExtension, StringComparison.OrdinalIgnoreCase);

		// As the render processes it: the speed calibration and the smoothing from the settings.
		OverlaySettings settings = OverlaySettingsStore.Load();
		try
		{
			await Task.Run(() =>
			{
				List<DerivedFrame> frames = TelemetryProcessor.Process(raw, summary.CameraFormat, settings.SmoothGpsMotion, settings.SpeedCorrectionPercent,
					settings.OrientationSource);
				TelemetryExport.Write(path, frames, track ? 1 : 0, summary.ContainerRecordingStartUtc);
			});
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			AppLogger.Error(ex, string.Format(Strings.Telemetry_ExportFailed, ex.Message));
			await ConfirmDialog.ShowAsync(this, Strings.Telemetry_ExportFailedTitle, ex.Message, kind: DialogKind.Warning);
			return;
		}

		AppendLog(string.Format(Strings.Telemetry_Exported, path));
	}
}
