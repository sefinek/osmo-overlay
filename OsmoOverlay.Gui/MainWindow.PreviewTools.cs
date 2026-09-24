using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OsmoOverlay.Core;

namespace OsmoOverlay.Gui;

/// <summary>Preview toolbar tools that don't edit anything: showing/hiding the overlay (H) and saving the frame on screen as a PNG.</summary>
public partial class MainWindow
{
	private bool _showOverlay = true;

	private void OnToggleOverlayClick(object? sender, RoutedEventArgs e)
	{
		ToggleOverlay();
	}

	/// <summary>The plain footage next to the HUD, without unticking widgets one by one - a view toggle, not saved.</summary>
	private void ToggleOverlay()
	{
		_showOverlay = !_showOverlay;
		ToggleOverlayButton.Classes.Set("active", _showOverlay);
		_previewPlayer.SetShowOverlay(_showOverlay);
	}

	/// <summary>The frame on screen at the recording's full resolution, with the overlay as the render draws it (or without, while hidden).</summary>
	private async void OnSnapshotClick(object? sender, RoutedEventArgs e)
	{
		if (_summary is null) return;

		TimeSpan position = TimeSpan.FromSeconds(PreviewTimeline.Value);
		var source = _summary.InputPaths[0];
		var suggestedName = $"{Path.GetFileNameWithoutExtension(source)}_{TimeText.Format(position.TotalSeconds).Replace(':', '-')}.png";

		IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
		{
			Title = "Save frame as PNG",
			SuggestedFileName = suggestedName,
			SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(Path.GetDirectoryName(source) ?? "."),
			DefaultExtension = "png",
			FileTypeChoices = [new FilePickerFileType("PNG image") { Patterns = ["*.png"] }]
		});
		if (file is null) return;

		try
		{
			SnapshotButton.IsEnabled = false;
			var png = await _previewPlayer.RenderSnapshotPngAsync(position);
			await using Stream stream = await file.OpenWriteAsync();
			await stream.WriteAsync(png);
			AppendLog($"Frame saved: {file.Path.LocalPath} ({_summary.Video.Width}x{_summary.Video.Height})");
		}
		catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
		{
			AppendLog($"Couldn't save the frame: {ex.Message}", LogLevel.Error);
		}
		finally
		{
			SnapshotButton.IsEnabled = true;
		}
	}
}
