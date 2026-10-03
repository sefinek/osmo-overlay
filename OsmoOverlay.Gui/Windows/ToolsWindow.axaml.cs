using System.Diagnostics;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Localization;
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

	/// <summary>Opens the performance test over the given window - MainWindow's, which knows the loaded recording.</summary>
	public Func<Window, Task>? OpenBenchmark { get; init; }

	private async void OnBenchmarkClick(object? sender, RoutedEventArgs e)
	{
		if (OpenBenchmark is not null) await OpenBenchmark(this);
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
			AppLogger.Notify(string.Format(Strings.Tools_OpenDataFolderFailed, ex.Message));
		}
	}

	private void OnClearCacheClick(object? sender, RoutedEventArgs e)
	{
		int deleted = FileSummaryReader.ClearCache();
		AppLogger.Notify(Plural.Format(Strings.Tools_CacheCleared, deleted));
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
			Title = Strings.Tools_PickRenderedMp4,
			AllowMultiple = false,
			FileTypeFilter = [new FilePickerFileType(Strings.Common_Mp4Video) { Patterns = ["*.mp4", "*.MP4"] }]
		});

		if (files.Count == 0) return;

		string path = files[0].Path.LocalPath;
		string fileName = Path.GetFileName(path);

		SelectColorTagFileButton.IsEnabled = false;
		SelectColorTagFileButton.Content = Strings.Common_Working;
		try
		{
			ColorTagStatus status = await Task.Run(() => ColorTagFixer.Check(path));

			if (!status.IsEligible)
			{
				await ConfirmDialog.ShowAsync(this, Strings.Tools_CantFixTitle,
					string.Format(Strings.Tools_CantFixMessage, fileName, DescribeTags(status)),
					kind: DialogKind.Danger, windowTitle: Strings.Tools_FixColorTags);
				return;
			}

			if (status.NeedsFix)
			{
				await AskAndFixAsync(path, fileName, status);
			}
			else
			{
				await ConfirmDialog.ShowAsync(this, Strings.Tools_NothingToDoTitle,
					string.Format(Strings.Tools_NothingToDoMessage, fileName, DescribeTags(status)),
					kind: DialogKind.Success, windowTitle: Strings.Tools_FixColorTags);
			}
		}
		catch (Exception ex)
		{
			await ConfirmDialog.ShowAsync(this, Strings.Tools_CantReadTitle,
				string.Format(Strings.Tools_CantReadMessage, ex.Message),
				kind: DialogKind.Danger, windowTitle: Strings.Tools_FixColorTags);
		}
		finally
		{
			SelectColorTagFileButton.IsEnabled = true;
			SelectColorTagFileButton.Content = Strings.Tools_SelectFile;
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

		await ConfirmDialog.AskAsync(this, Strings.Tools_FixAskTitle,
			string.Format(Strings.Tools_FixAskMessage, fileName, DescribeTags(status)),
			Strings.Tools_Fix, DialogKind.Warning, Strings.Tools_FixColorTags,
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
			Strings.Tools_Fixing);

		if (fixResult is { } result)
		{
			await ConfirmDialog.ShowAsync(this, Strings.Tools_FixedTitle,
				string.Format(Strings.Tools_FixedMessage, fileName, DescribeTags(result.Before), DescribeTags(result.After),
					Path.GetFileName(result.OutputPath)),
				kind: DialogKind.Success, windowTitle: Strings.Tools_FixColorTags,
				secondaryText: Strings.Common_ShowInFolder, onSecondary: () => ExplorerHelper.ShowInFolder(result.OutputPath),
				extraText: Strings.Tools_CompareFiles, onExtra: () => OpenCompareWindow(path, result.OutputPath));
		}
		else if (fixError is not null)
		{
			await ConfirmDialog.ShowAsync(this, Strings.Tools_FixFailedTitle,
				string.Format(Strings.Tools_FixFailedMessage, fileName, fixError.Message),
				kind: DialogKind.Danger, windowTitle: Strings.Tools_FixColorTags);
		}
	}

	private async void OnSelectStripFileClick(object? sender, RoutedEventArgs e)
	{
		TopLevel? topLevel = GetTopLevel(this);
		if (topLevel is null) return;

		IReadOnlyList<IStorageFile> files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
		{
			Title = Strings.Tools_PickVideoToStrip,
			AllowMultiple = false,
			FileTypeFilter = [new FilePickerFileType(Strings.Tools_Mp4MovVideo) { Patterns = ["*.mp4", "*.MP4", "*.mov", "*.MOV"] }]
		});
		if (files.Count == 0) return;

		string inputPath = files[0].Path.LocalPath;
		IStorageFile? target = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
		{
			Title = Strings.Tools_SaveCleanedCopyAs,
			SuggestedFileName = SuggestCleanFileName(inputPath),
			SuggestedStartLocation = await topLevel.StorageProvider.TryGetFolderFromPathAsync(
				Path.GetDirectoryName(inputPath) ?? ""),
			DefaultExtension = "mp4",
			FileTypeChoices = [new FilePickerFileType(Strings.Common_Mp4Video) { Patterns = ["*.mp4"] }]
		});
		if (target is null) return;

		string outputPath = target.Path.LocalPath;
		SelectStripFileButton.IsEnabled = false;
		SelectStripFileButton.Content = Strings.Common_Working;
		try
		{
			MetadataStripResult result = await Task.Run(() => MetadataStripper.Strip(inputPath, outputPath));

			string removed = result.Removed.Count > 0
				? string.Join("\n", result.Removed.Select(r => $"    - {r}"))
				: "    " + Strings.Tools_NothingIdentifyingFound;
			await ConfirmDialog.ShowAsync(this, Strings.Tools_MetadataRemovedTitle,
				string.Format(Strings.Tools_MetadataRemovedMessage, Path.GetFileName(outputPath), removed),
				kind: DialogKind.Success, windowTitle: Strings.Tools_RemoveMetadata,
				secondaryText: Strings.Common_ShowInFolder, onSecondary: () => ExplorerHelper.ShowInFolder(outputPath),
				extraText: Strings.Tools_CompareFiles, onExtra: () => OpenCompareWindow(inputPath, outputPath));
		}
		catch (Exception ex)
		{
			await ConfirmDialog.ShowAsync(this, Strings.Tools_StripFailedTitle,
				string.Format(Strings.Tools_StripFailedMessage, ex.Message),
				kind: DialogKind.Danger, windowTitle: Strings.Tools_RemoveMetadata);
		}
		finally
		{
			SelectStripFileButton.IsEnabled = true;
			SelectStripFileButton.Content = Strings.Tools_SelectFile;
		}
	}

	/// <summary>
	///     "clean.mp4" instead of "{name}_clean.mp4" when the original name looks like it carries a date
	///     (DJI_20260916100634_0003_D) - the suggested name shouldn't undo the point of the tool.
	/// </summary>
	private static string SuggestCleanFileName(string inputPath)
	{
		string name = Path.GetFileNameWithoutExtension(inputPath);
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
			Title = Strings.Tools_PickCameraAudio,
			AllowMultiple = true,
			FileTypeFilter = [new FilePickerFileType(Strings.Tools_CameraAudio) { Patterns = ["*.aac", "*.AAC"] }]
		});
		if (files.Count == 0) return;

		List<(string Input, string Output)> jobs =
			[.. files.Select(f => f.Path.LocalPath).Select(p => (p, CameraAudioConverter.OutputPathFor(p, format)))];

		List<string> existing = [.. jobs.Where(j => File.Exists(j.Output)).Select(j => Path.GetFileName(j.Output))];
		if (existing.Count > 0 && !await ConfirmDialog.AskAsync(this, Strings.Tools_ReplaceExistingTitle,
			    string.Format(Strings.Tools_ReplaceExistingMessage, string.Join("\n", existing.Select(n => $"    {n}"))),
			    Strings.Tools_Replace, DialogKind.Warning, Strings.Tools_CameraMicrophoneAudio))
			return;

		Button[] buttons = [ConvertAudioWavButton, ConvertAudioM4aButton];
		foreach (Button b in buttons) b.IsEnabled = false;
		Button active = format == CameraAudioFormat.Wav ? ConvertAudioWavButton : ConvertAudioM4aButton;
		object? idleContent = active.Content;

		List<string> done = [];
		try
		{
			foreach ((string input, string output) in jobs)
			{
				active.Content = jobs.Count > 1
					? string.Format(Strings.Tools_ConvertingOf, done.Count + 1, jobs.Count)
					: Strings.Tools_Converting;
				await Task.Run(() => CameraAudioConverter.Convert(input, output, format));
				done.Add(output);
			}

			await ConfirmDialog.ShowAsync(this, Strings.Tools_ConvertedTitle,
				string.Format(Strings.Tools_ConvertedMessage, string.Join("\n", done.Select(o => $"    {Path.GetFileName(o)}"))),
				kind: DialogKind.Success, windowTitle: Strings.Tools_CameraMicrophoneAudio,
				secondaryText: Strings.Common_ShowInFolder, onSecondary: () => ExplorerHelper.ShowInFolder(done[^1]));
		}
		catch (Exception ex)
		{
			string converted = done.Count > 0
				? string.Format(Strings.Tools_ConvertedBeforeError, string.Join("\n", done.Select(o => $"    {Path.GetFileName(o)}"))) + "\n\n"
				: "";
			await ConfirmDialog.ShowAsync(this, Strings.Tools_ConversionFailedTitle,
				converted + string.Format(Strings.Tools_ConversionFailedMessage, ex.Message),
				kind: DialogKind.Danger, windowTitle: Strings.Tools_CameraMicrophoneAudio);
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
		return $"    primaries: {status.ColorPrimaries ?? Strings.Common_Unknown}\n" +
		       $"    transfer: {status.ColorTransfer ?? Strings.Common_Unknown}\n" +
		       $"    matrix: {status.ColorSpace ?? Strings.Common_Unknown}\n" +
		       $"    range: {status.ColorRange ?? Strings.Common_Unknown}";
	}
}
