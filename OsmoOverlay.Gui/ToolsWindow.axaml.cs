using System.Diagnostics;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Logging;

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
			Process.Start(new ProcessStartInfo(DataDir) { UseShellExecute = true })?.Dispose();
		}
		catch (Exception ex)
		{
			AppLogger.Notify($"Could not open the data folder: {ex.Message}");
		}
	}

	private void OnClearCacheClick(object? sender, RoutedEventArgs e)
	{
		var deleted = FileSummaryReader.ClearCache();
		AppLogger.Notify($"Cleared {deleted} cached file(s)");
	}

	private void OnCloseClick(object? sender, RoutedEventArgs e)
	{
		Close();
	}

	private void OnCompareVideosClick(object? sender, RoutedEventArgs e)
	{
		new CompareVideosWindow().Show(this);
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
				await AskAndFixAsync(path, fileName, status);
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

	/// <summary>
	///     onConfirm here only runs the fix itself (captured into fixResult/fixError) and does NOT show
	///     the outcome dialog - AskAsync only closes the "Fix this file?" dialog once onConfirm's task
	///     completes, so showing the outcome dialog from inside onConfirm would leave this one sitting
	///     open behind it (still saying "Fixing...") until the outcome dialog is dismissed too.
	/// </summary>
	private async Task AskAndFixAsync(string path, string fileName, ColorTagStatus status)
	{
		ColorTagFixResult? fixResult = null;
		Exception? fixError = null;

		await ConfirmDialog.AskAsync(this, "Fix this file?",
			$"\"{fileName}\" is missing its Rec.709 color tag - this is the typical Vegas Pro export " +
			"gap this tool exists for, and it's safe to fix. Rewrite the tag with a lossless stream " +
			"copy (no re-encode, no quality loss)?\n\n" +
			$"Current tags:\n{DescribeTags(status)}",
			"Fix", DialogKind.Warning, "Fix color tags",
			async () =>
			{
				try
				{
					fixResult = await Task.Run(() => ColorTagFixer.Fix(path));
				}
				catch (Exception ex)
				{
					fixError = ex;
				}
			},
			"Fixing...");

		if (fixResult is { } result)
			await ConfirmDialog.ShowAsync(this, "Fixed successfully",
				$"\"{fileName}\" was fixed successfully - the color tag was rewritten to Rec.709 with a " +
				"lossless stream copy, so picture quality is untouched.\n\n" +
				$"Before:\n{DescribeTags(result.Before)}\n\n" +
				$"After:\n{DescribeTags(result.After)}\n\n" +
				$"Saved to: {Path.GetFileName(result.OutputPath)}",
				kind: DialogKind.Success, windowTitle: "Fix color tags",
				secondaryText: "Show in folder", onSecondary: () => ExplorerHelper.ShowInFolder(result.OutputPath),
				extraText: "Compare files", onExtra: () => OpenCompareWindow(path, result.OutputPath));
		else if (fixError is not null)
			await ConfirmDialog.ShowAsync(this, "Fix failed",
				$"Fixing \"{fileName}\" failed.\n\nDetails: {fixError.Message}",
				kind: DialogKind.Danger, windowTitle: "Fix color tags");
	}

	private async void OnSelectStripFileClick(object? sender, RoutedEventArgs e)
	{
		TopLevel? topLevel = GetTopLevel(this);
		if (topLevel is null) return;

		IReadOnlyList<IStorageFile> files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
		{
			Title = "Select a video to remove metadata from",
			AllowMultiple = false,
			FileTypeFilter = [new FilePickerFileType("MP4/MOV video") { Patterns = ["*.mp4", "*.MP4", "*.mov", "*.MOV"] }]
		});
		if (files.Count == 0) return;

		var inputPath = files[0].Path.LocalPath;
		IStorageFile? target = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
		{
			Title = "Save the cleaned copy as",
			SuggestedFileName = SuggestCleanFileName(inputPath),
			SuggestedStartLocation = await topLevel.StorageProvider.TryGetFolderFromPathAsync(
				Path.GetDirectoryName(inputPath) ?? ""),
			DefaultExtension = "mp4",
			FileTypeChoices = [new FilePickerFileType("MP4 video") { Patterns = ["*.mp4"] }]
		});
		if (target is null) return;

		var outputPath = target.Path.LocalPath;
		SelectStripFileButton.IsEnabled = false;
		SelectStripFileButton.Content = "Working...";
		try
		{
			MetadataStripResult result = await Task.Run(() => MetadataStripper.Strip(inputPath, outputPath));

			var removed = result.Removed.Count > 0
				? string.Join("\n", result.Removed.Select(r => $"    - {r}"))
				: "    (nothing identifying was found)";
			await ConfirmDialog.ShowAsync(this, "Metadata removed",
				$"Saved a clean copy as \"{Path.GetFileName(outputPath)}\" and verified it: only picture and sound are " +
				"left, bit-for-bit identical, with the same color profile and rotation.\n\n" +
				$"Removed:\n{removed}\n\n" +
				"The file name itself isn't metadata - camera file names like DJI_20260916100634 contain the recording " +
				"date and time, so rename it before sharing if that matters.",
				kind: DialogKind.Success, windowTitle: "Remove metadata",
				secondaryText: "Show in folder", onSecondary: () => ExplorerHelper.ShowInFolder(outputPath),
				extraText: "Compare files", onExtra: () => OpenCompareWindow(inputPath, outputPath));
		}
		catch (Exception ex)
		{
			await ConfirmDialog.ShowAsync(this, "Couldn't remove metadata",
				$"No file was saved.\n\nDetails: {ex.Message}",
				kind: DialogKind.Danger, windowTitle: "Remove metadata");
		}
		finally
		{
			SelectStripFileButton.IsEnabled = true;
			SelectStripFileButton.Content = "Select file...";
		}
	}

	/// <summary>
	///     "clean.mp4" instead of "{name}_clean.mp4" when the original name looks like it carries a date
	///     (DJI_20260916100634_0003_D) - the suggested name shouldn't undo the point of the tool.
	/// </summary>
	private static string SuggestCleanFileName(string inputPath)
	{
		var name = Path.GetFileNameWithoutExtension(inputPath);
		return Regex.IsMatch(name, @"\d{8}") ? "clean.mp4" : $"{name}_clean.mp4";
	}

	private void OnConvertAudioWavClick(object? sender, RoutedEventArgs e)
	{
		_ = ConvertCameraAudioAsync(CameraAudioFormat.Wav);
	}

	private void OnConvertAudioM4aClick(object? sender, RoutedEventArgs e)
	{
		_ = ConvertCameraAudioAsync(CameraAudioFormat.M4a);
	}

	private async Task ConvertCameraAudioAsync(CameraAudioFormat format)
	{
		TopLevel? topLevel = GetTopLevel(this);
		if (topLevel is null) return;

		IReadOnlyList<IStorageFile> files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
		{
			Title = "Select the camera's .AAC audio file(s)",
			AllowMultiple = true,
			FileTypeFilter = [new FilePickerFileType("Camera audio") { Patterns = ["*.aac", "*.AAC"] }]
		});
		if (files.Count == 0) return;

		List<(string Input, string Output)> jobs =
			[.. files.Select(f => f.Path.LocalPath).Select(p => (p, CameraAudioConverter.OutputPathFor(p, format)))];

		List<string> existing = [.. jobs.Where(j => File.Exists(j.Output)).Select(j => Path.GetFileName(j.Output))];
		if (existing.Count > 0 && !await ConfirmDialog.AskAsync(this, "Replace existing files?",
			    $"These already exist and will be replaced:\n{string.Join("\n", existing.Select(n => $"    {n}"))}",
			    "Replace", DialogKind.Warning, "Camera microphone audio"))
			return;

		Button[] buttons = [ConvertAudioWavButton, ConvertAudioM4aButton];
		foreach (Button b in buttons) b.IsEnabled = false;
		Button active = format == CameraAudioFormat.Wav ? ConvertAudioWavButton : ConvertAudioM4aButton;
		var idleContent = active.Content;

		List<string> done = [];
		try
		{
			foreach (var (input, output) in jobs)
			{
				active.Content = jobs.Count > 1 ? $"Converting {done.Count + 1}/{jobs.Count}..." : "Converting...";
				await Task.Run(() => CameraAudioConverter.Convert(input, output, format));
				done.Add(output);
			}

			await ConfirmDialog.ShowAsync(this, "Converted",
				"Saved and verified - decodes to exactly the same samples as the original:\n" +
				string.Join("\n", done.Select(o => $"    {Path.GetFileName(o)}")) + "\n\n" +
				"It has the same length as the video's own audio track and starts with it, so placing it at the " +
				"start of the matching MP4 on the timeline lines it up.",
				kind: DialogKind.Success, windowTitle: "Camera microphone audio",
				secondaryText: "Show in folder", onSecondary: () => ExplorerHelper.ShowInFolder(done[^1]));
		}
		catch (Exception ex)
		{
			var converted = done.Count > 0 ? $"Converted before the error:\n{string.Join("\n", done.Select(o => $"    {Path.GetFileName(o)}"))}\n\n" : "";
			await ConfirmDialog.ShowAsync(this, "Conversion failed",
				$"{converted}Nothing was saved for the file that failed.\n\nDetails: {ex.Message}",
				kind: DialogKind.Danger, windowTitle: "Camera microphone audio");
		}
		finally
		{
			active.Content = idleContent;
			foreach (Button b in buttons) b.IsEnabled = true;
		}
	}

	private void OpenCompareWindow(params string[] paths)
	{
		new CompareVideosWindow(paths).Show(this);
	}

	private static string DescribeTags(ColorTagStatus status)
	{
		return $"    primaries: {status.ColorPrimaries ?? "unknown"}\n" +
		       $"    transfer: {status.ColorTransfer ?? "unknown"}\n" +
		       $"    matrix: {status.ColorSpace ?? "unknown"}\n" +
		       $"    range: {status.ColorRange ?? "unknown"}";
	}
}
