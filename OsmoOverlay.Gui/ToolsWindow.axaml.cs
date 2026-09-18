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

		var path = files[0].Path.LocalPath;
		var fileName = Path.GetFileName(path);

		SelectColorTagFileButton.IsEnabled = false;
		SelectColorTagFileButton.Content = "Working...";
		try
		{
			ColorTagStatus status = await Task.Run(() => ColorTagFixer.Check(path));

			if (!status.IsEligible)
			{
				await ConfirmDialog.ShowAsync(this, "Can't fix this file",
					$"\"{fileName}\" already has an explicit color tag that isn't Rec.709 - it looks like HDR " +
					"or wide-gamut content, not a plain Rec.709 export. Forcing Rec.709 over that would " +
					"mislabel the color space instead of fixing it, so this tool won't touch this file.\n\n" +
					$"Current tags:\n{DescribeTags(status)}",
					kind: DialogKind.Danger, windowTitle: "Fix color tags");
				return;
			}

			if (status.NeedsFix)
				await ConfirmDialog.AskAsync(this, "Fix this file?",
					$"\"{fileName}\" is missing its Rec.709 color tag - this is the typical Vegas Pro export " +
					"gap this tool exists for, and it's safe to fix. Rewrite the tag with a lossless stream " +
					"copy (no re-encode, no quality loss)?\n\n" +
					$"Current tags:\n{DescribeTags(status)}",
					"Fix", DialogKind.Warning, "Fix color tags",
					() => RunFixAsync(path, fileName), "Fixing...");
			else
				await ConfirmDialog.ShowAsync(this, "Nothing to do",
					$"\"{fileName}\" already has a correct Rec.709 color tag - there's nothing to fix here.\n\n" +
					$"Current tags:\n{DescribeTags(status)}",
					kind: DialogKind.Success, windowTitle: "Fix color tags");
		}
		catch (Exception ex)
		{
			await ConfirmDialog.ShowAsync(this, "Couldn't read file",
				$"Could not read this file - it may be corrupted, still being written, or not a video file at " +
				$"all.\n\nDetails: {ex.Message}",
				kind: DialogKind.Danger, windowTitle: "Fix color tags");
		}
		finally
		{
			SelectColorTagFileButton.IsEnabled = true;
			SelectColorTagFileButton.Content = "Select file...";
		}
	}

	private async Task RunFixAsync(string path, string fileName)
	{
		try
		{
			ColorTagFixResult result = await Task.Run(() => ColorTagFixer.Fix(path));
			await ConfirmDialog.ShowAsync(this, "Fixed successfully",
				$"\"{fileName}\" was fixed successfully - the color tag was rewritten to Rec.709 with a " +
				"lossless stream copy, so picture quality is untouched.\n\n" +
				$"Before:\n{DescribeTags(result.Before)}\n\n" +
				$"After:\n{DescribeTags(result.After)}\n\n" +
				$"Saved to: {Path.GetFileName(result.OutputPath)}",
				kind: DialogKind.Success, windowTitle: "Fix color tags",
				secondaryText: "Show in folder", onSecondary: () => ShowInExplorer(result.OutputPath));
		}
		catch (Exception ex)
		{
			await ConfirmDialog.ShowAsync(this, "Fix failed",
				$"Fixing \"{fileName}\" failed.\n\nDetails: {ex.Message}",
				kind: DialogKind.Danger, windowTitle: "Fix color tags");
		}
	}

	// Opens Explorer with the given file pre-selected, e.g. as proof the fix actually produced an
	// output file - UseShellExecute so this runs like a user double-clicking it, not like the
	// CreateHidden/redirected-output pattern used for ffmpeg/ffprobe/exiftool elsewhere.
	private static void ShowInExplorer(string filePath)
	{
		try
		{
			Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{filePath}\"", UseShellExecute = true });
		}
		catch
		{
			// Best-effort - the success dialog already confirms the fix, so a failure to open
			// Explorer isn't worth surfacing as an error.
		}
	}

	private static string DescribeTags(ColorTagStatus status)
	{
		return $"    primaries: {status.ColorPrimaries ?? "unknown"}\n" +
		       $"    transfer: {status.ColorTransfer ?? "unknown"}\n" +
		       $"    matrix: {status.ColorSpace ?? "unknown"}\n" +
		       $"    range: {status.ColorRange ?? "unknown"}";
	}
}
