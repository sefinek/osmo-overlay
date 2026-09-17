using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Ffmpeg;

namespace OsmoOverlay.Gui;

public partial class ToolsWindow : Window
{
	// Same "%LocalAppData%\OsmoOverlay" root that FileSummaryCache/OverlayPresetStore/MapTileFetcher/
	// NLog each independently combine for their own subfolder.
	private static readonly string DataDir = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OsmoOverlay");

	private string? _colorTagFilePath;

	public ToolsWindow()
	{
		InitializeComponent();
	}

	private void OnOpenDataFolderClick(object? sender, RoutedEventArgs e)
	{
		try
		{
			Directory.CreateDirectory(DataDir);
			// UseShellExecute so this opens in Explorer (or the platform's file manager) rather than
			// the CreateHidden/redirected-output pattern used elsewhere for ffmpeg/ffprobe/exiftool.
			Process.Start(new ProcessStartInfo(DataDir) { UseShellExecute = true });
		}
		catch (Exception ex)
		{
			CacheStatusText.Text = $"Could not open the data folder: {ex.Message}";
		}
	}

	private void OnClearCacheClick(object? sender, RoutedEventArgs e)
	{
		var deleted = FileSummaryReader.ClearCache();
		CacheStatusText.Text = $"Cleared {deleted} cached file(s).";
	}

	private void OnCloseClick(object? sender, RoutedEventArgs e)
	{
		Close();
	}

	private async void OnSelectColorTagFileClick(object? sender, RoutedEventArgs e)
	{
		TopLevel? topLevel = GetTopLevel(this);
		if (topLevel is null) return;

		IReadOnlyList<IStorageFile> files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
		{
			Title = "Select a rendered MP4 to check",
			AllowMultiple = false,
			FileTypeFilter = [new FilePickerFileType("MP4 video") { Patterns = ["*.mp4", "*.MP4"] }]
		});

		if (files.Count == 0) return;

		_colorTagFilePath = files[0].Path.LocalPath;
		ColorTagFilePathText.Text = Path.GetFileName(_colorTagFilePath);
		FixColorTagsButton.IsEnabled = false;

		try
		{
			var fileName = Path.GetFileName(_colorTagFilePath);
			ColorTagStatus status = await Task.Run(() => ColorTagFixer.Check(_colorTagFilePath));

			if (!status.IsEligible)
			{
				FixColorTagsButton.IsEnabled = false;
				await ConfirmDialog.ShowAsync(this, "Fix color tags",
					$"\"{fileName}\" already has an explicit color tag that isn't Rec.709 - it looks like HDR " +
					"or wide-gamut content, not a plain Rec.709 export. Forcing Rec.709 over that would " +
					"mislabel the color space instead of fixing it, so this tool won't touch this file.\n\n" +
					$"Current tags: {DescribeTags(status)}",
					kind: DialogKind.Danger);
				return;
			}

			if (status.NeedsFix)
			{
				FixColorTagsButton.IsEnabled = true;
				await ConfirmDialog.ShowAsync(this, "Fix color tags",
					$"\"{fileName}\" is missing its Rec.709 color tag - this is the typical Vegas Pro export " +
					"gap this tool exists for, and it's safe to fix. Click \"Fix\" to rewrite the tag with a " +
					"lossless stream copy (no re-encode, no quality loss).\n\n" +
					$"Current tags: {DescribeTags(status)}",
					kind: DialogKind.Warning);
			}
			else
			{
				FixColorTagsButton.IsEnabled = false;
				await ConfirmDialog.ShowAsync(this, "Fix color tags",
					$"\"{fileName}\" already has a correct Rec.709 color tag - there's nothing to fix here.\n\n" +
					$"Current tags: {DescribeTags(status)}",
					kind: DialogKind.Success);
			}
		}
		catch (Exception ex)
		{
			FixColorTagsButton.IsEnabled = false;
			await ConfirmDialog.ShowAsync(this, "Fix color tags",
				$"Could not read this file - it may be corrupted, still being written, or not a video file at " +
				$"all.\n\nDetails: {ex.Message}",
				kind: DialogKind.Danger);
		}
	}

	private async void OnFixColorTagsClick(object? sender, RoutedEventArgs e)
	{
		if (_colorTagFilePath is null) return;

		FixColorTagsButton.IsEnabled = false;
		var fileName = Path.GetFileName(_colorTagFilePath);

		try
		{
			var path = _colorTagFilePath;
			ColorTagFixResult result = await Task.Run(() => ColorTagFixer.Fix(path));
			await ConfirmDialog.ShowAsync(this, "Fix color tags",
				$"\"{fileName}\" was fixed successfully - the color tag was rewritten to Rec.709 with a " +
				"lossless stream copy, so picture quality is untouched.\n\n" +
				$"Before: {DescribeTags(result.Before)}\n" +
				$"After:    {DescribeTags(result.After)}\n\n" +
				$"Saved to: {Path.GetFileName(result.OutputPath)}",
				kind: DialogKind.Success);
		}
		catch (Exception ex)
		{
			await ConfirmDialog.ShowAsync(this, "Fix color tags",
				$"Fixing \"{fileName}\" failed.\n\nDetails: {ex.Message}",
				kind: DialogKind.Danger);
			FixColorTagsButton.IsEnabled = true;
		}
	}

	private static string DescribeTags(ColorTagStatus status)
	{
		return $"color primaries = {status.ColorPrimaries ?? "unknown"}, transfer curve = {status.ColorTransfer ?? "unknown"}, " +
		       $"matrix = {status.ColorSpace ?? "unknown"}, range = {status.ColorRange ?? "unknown"}";
	}
}
