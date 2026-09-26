using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Gui;

/// <summary>
///     The preview's sound, right of the toolbar above it: muted by default, the mute state and the volume remembered
///     (OverlaySettings). The slider fires on every pixel of a drag, so its changes are saved after a short pause.
/// </summary>
public partial class MainWindow
{
	private readonly DispatcherTimer _volumeSaveDelay = new() { Interval = TimeSpan.FromMilliseconds(500) };
	private bool _audioMuted;
	private double _audioVolume;
	private bool _suppressVolumeEvent;

	private void WireAudio()
	{
		_volumeSaveDelay.Tick += (_, _) =>
		{
			_volumeSaveDelay.Stop();
			SaveAudioSettings();
		};
		Closed += (_, _) =>
		{
			if (_volumeSaveDelay.IsEnabled) SaveAudioSettings();
		};

		_suppressVolumeEvent = true;
		VolumeSlider.Value = _audioVolume;
		_suppressVolumeEvent = false;
		ApplyAudio();
	}

	private void OnMuteClick(object? sender, RoutedEventArgs e)
	{
		ToggleMute();
	}

	private void ToggleMute()
	{
		if (!AudioPanel.IsEnabled) return;

		_audioMuted = !_audioMuted;
		ApplyAudio();
		SaveAudioSettings();
	}

	private void OnVolumeChanged(object? sender, RangeBaseValueChangedEventArgs e)
	{
		if (_suppressVolumeEvent) return;

		_audioVolume = e.NewValue;
		// Turning the volume up means wanting to hear it.
		if (_audioMuted && _audioVolume > 0) _audioMuted = false;
		ApplyAudio();
		_volumeSaveDelay.Stop();
		_volumeSaveDelay.Start();
	}

	private void ApplyAudio()
	{
		_previewPlayer.SetAudioVolume(_audioVolume, _audioMuted);
		MuteIcon.Data = _audioMuted || _audioVolume <= 0 ? Icons.VolumeMuted : Icons.Volume;
		ToolTip.SetTip(MuteButton, _audioMuted ? "Unmute (M)" : "Mute (M)");
	}

	/// <summary>After the preview opens or closes - greyed out without an audio track or a playback device.</summary>
	private void UpdateAudioPanel()
	{
		AudioPanel.IsEnabled = _previewPlayer.HasAudio;
		ToolTip.SetTip(AudioPanel, _previewPlayer.HasAudio || _previewFrameSize is null
			? null
			: "No sound: this recording has no audio track, or there's no playback device");
	}

	private void SaveAudioSettings()
	{
		OverlaySettingsStore.Save(OverlaySettingsStore.Load() with { PreviewAudioMuted = _audioMuted, PreviewAudioVolume = _audioVolume });
	}
}
