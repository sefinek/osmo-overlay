using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Telemavix.Core.Overlay;

namespace Telemavix.Gui;

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
	private readonly DispatcherTimer _meterTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
	private long _meterLastTick;
	private bool _soundPanelShown;

	private void WireAudio()
	{
		_meterTimer.Tick += (_, _) =>
		{
			long now = System.Diagnostics.Stopwatch.GetTimestamp();
			double seconds = _meterLastTick == 0 ? 0 : System.Diagnostics.Stopwatch.GetElapsedTime(_meterLastTick, now).TotalSeconds;
			_meterLastTick = now;
			(float left, float right) = AudioPanel.IsEnabled && _previewPlayer.IsPlaying ? _previewPlayer.AudioLevel : default;
			(_soundPanelShown ? SoundMeter : AudioMeter).Update(left, right, seconds);
		};
		_meterTimer.Start();
		PreviewToolbarRow.SizeChanged += (_, _) => FitAudioPanel();
		PreviewTools.SizeChanged += (_, _) => FitAudioPanel();
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

	/// <summary>
	///     Fits the audio controls into what the row leaves besides the quality/zoom boxes and the tools (kept in the middle): as
	///     it narrows, the meter shrinks first, then the percentage goes, then the slider shortens and goes - the mute button stays.
	/// </summary>
	private void FitAudioPanel()
	{
		const double mute = 28, spacing = 6, text = 34, sliderFull = 100, sliderMin = 50, meterMax = 220, meterMin = 40, toolGap = 12;
		double free = PreviewToolbarRow.Bounds.Width - PreviewToolbarRow.ColumnDefinitions[0].ActualWidth - PreviewTools.Bounds.Width - 2 * toolGap;
		double room = free - AudioPanel.Margin.Left - mute - spacing;
		bool showText = room >= sliderFull + text + 2 * spacing;
		double afterText = room - (showText ? text + spacing : 0) - spacing;
		bool showSlider = afterText >= sliderMin;
		VolumeText.IsVisible = showText && showSlider;
		VolumeSlider.IsVisible = showSlider;
		if (showSlider) VolumeSlider.Width = Math.Min(sliderFull, afterText);

		double meterRoom = showText ? room - sliderFull - text - 2 * spacing - spacing : 0;
		AudioMeter.Width = meterRoom >= meterMin ? Math.Min(meterMax, meterRoom) : 0;
	}

	/// <summary>Swaps the overlay column for the tall level meter; the small meter above the preview steps aside meanwhile.</summary>
	private void OnToggleSoundPanelClick(object? sender, RoutedEventArgs e)
	{
		_soundPanelShown = !_soundPanelShown;
		SoundCard.IsVisible = _soundPanelShown;
		OverlayCard.IsVisible = !_soundPanelShown;
		AudioMeter.IsVisible = !_soundPanelShown;
		ToggleSoundPanelButton.Classes.Set("active", _soundPanelShown);
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
		bool silent = _audioMuted || _audioVolume <= 0;
		MuteIcon.Data = silent ? Icons.VolumeMuted : Icons.Volume;
		MuteSlash.IsVisible = silent;
		VolumeText.Text = $"{Math.Round(_audioVolume * 100):0}%";
		VolumeText.Opacity = silent ? 0.35 : 0.6;
		ToolTip.SetTip(MuteButton, _audioMuted ? Strings.Main_UnmuteM : Strings.Audio_Mute);
	}

	/// <summary>After the preview opens or closes - greyed out without an audio track or a playback device.</summary>
	private void UpdateAudioPanel()
	{
		AudioPanel.IsEnabled = _previewPlayer.HasAudio;
		ToolTip.SetTip(AudioPanel, _previewPlayer.HasAudio || _previewFrameSize is null
			? null
			: Strings.Audio_NoSound);
	}

	private void SaveAudioSettings()
	{
		OverlaySettingsStore.Save(OverlaySettingsStore.Load() with { PreviewAudioMuted = _audioMuted, PreviewAudioVolume = _audioVolume });
	}
}
