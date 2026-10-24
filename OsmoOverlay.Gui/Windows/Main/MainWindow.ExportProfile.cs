using Avalonia.Controls;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Gui;

/// <summary>
///     The export profile picker in the Output card (ExportProfile): choosing one writes its size, codec, depth and bitrate into the
///     settings, the same fields Settings, Rendering shows - so the card, the encoder and the disk check all follow, and the choice
///     is part of the settings preset like any other change there. Options that fit no profile show as "Custom".
/// </summary>
public partial class MainWindow
{
	private bool _syncingProfile;

	private void RefreshExportProfiles()
	{
		ExportProfile? current = ExportProfile.Of(OverlaySettingsStore.Load());
		List<string> names = [.. ExportProfile.All.Select(p => p.Name), Strings.Main_ExportProfileCustom];

		_syncingProfile = true;
		ExportProfileCombo.ItemsSource = names;
		ExportProfileCombo.SelectedIndex = current is null ? names.Count - 1 : ExportProfile.All.ToList().IndexOf(current);
		_syncingProfile = false;
	}

	/// <summary>Not while a render or the queue runs: the Output card would show a plan the running render isn't following.</summary>
	private void UpdateExportProfileEnabled()
	{
		ExportProfileCombo.IsEnabled = _phase is not (UiPhase.Rendering or UiPhase.LoadingSummary) && !_queue.IsRunning;
	}

	private async void OnExportProfileSelectionChanged(object? sender, SelectionChangedEventArgs e)
	{
		int index = ExportProfileCombo.SelectedIndex;
		if (_syncingProfile || index < 0 || index >= ExportProfile.All.Count) return;

		OverlaySettings before = OverlaySettingsStore.Load();
		OverlaySettings updated = ExportProfile.All[index].ApplyTo(before);
		if (updated == before) return;

		OverlaySettingsStore.Save(updated);
		AppendLog(string.Format(Strings.Main_ExportProfileApplied, ExportProfile.All[index].Name));
		await ApplyOutputSettingsAsync(before, updated);
	}

	/// <summary>
	///     After the export options changed (Settings or a profile): another output codec or depth needs its encoder picked again
	///     (H.264 isn't HEVC's, 10-bit NVENC isn't on every GPU), and the Output card shows what will be written.
	/// </summary>
	private async Task ApplyOutputSettingsAsync(OverlaySettings before, OverlaySettings updated)
	{
		if (_summary is { } summary && _phase == UiPhase.SummaryReady)
		{
			VideoInfo output = OutputVideo.For(summary.Video, updated);
			VideoInfo previous = OutputVideo.For(summary.Video, before);
			if (output.CodecName != previous.CodecName || output.PixFmt != previous.PixFmt)
				_detectedEncoder = await Task.Run(() => FfmpegPipeline.SelectVideoEncoder(output));
			PopulateOutputInfo(summary, _detectedEncoder);
		}

		RefreshExportProfiles();
	}
}
