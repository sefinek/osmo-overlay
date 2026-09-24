using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using OsmoOverlay.Core.Preview;

namespace OsmoOverlay.Gui;

/// <summary>
///     The status strip under the transport row, the way an NLE shows it: the recording's and the preview's size and
///     frame rate, the frame at the cursor, and the preview's size on screen - with, while playing, the frames a second
///     that actually reach the display (VideoView counts them where they're drawn, not where they're decoded).
/// </summary>
public partial class MainWindow
{
	// Below this share of what the playback speed calls for (PreviewPlayer.PlaybackFrameRate), the frame rate goes amber.
	private const double SlowPlaybackShare = 0.9;

	// The frame rate changes a few times a second rather than with every frame, so it can be read.
	private static readonly TimeSpan PlaybackFpsRefresh = TimeSpan.FromMilliseconds(250);

	private double _playbackFps = double.NaN;
	private long _playbackFpsShownAt;

	/// <summary>On open/close: the parts that stay the same for the loaded recording.</summary>
	private void UpdatePreviewStatus()
	{
		PreviewStatusBar.IsVisible = _summary is not null && _previewFrameSize is not null;
		if (_summary is null || _previewFrameSize is not { } frameSize) return;

		StatusSourceText.Text = string.Create(CultureInfo.InvariantCulture,
			$"{_summary.Video.Width}x{_summary.Video.Height}; {_summary.Video.Fps:0.000}p");
		StatusPreviewText.Text = $"{frameSize.Width}x{frameSize.Height}";
		UpdateDisplayStatus();
	}

	private void UpdateFrameStatus(TimeSpan position)
	{
		if (_summary is null) return;

		var frame = Math.Min(PreviewFrames.IndexAt(position.TotalSeconds, _summary.Video.Fps), Math.Max(0, SourceFrames - 1));
		StatusFrameText.Text = frame.ToString("N0", CultureInfo.InvariantCulture);
	}

	private void ShowPlaybackFps(double framesPerSecond)
	{
		if (_playbackFpsShownAt != 0 && Stopwatch.GetElapsedTime(_playbackFpsShownAt) < PlaybackFpsRefresh) return;

		_playbackFps = framesPerSecond;
		_playbackFpsShownAt = Stopwatch.GetTimestamp();
		UpdateDisplayStatus();
	}

	/// <summary>A playback started or stopped - the frame rate is about the one playing, if any.</summary>
	private void ResetPlaybackFps()
	{
		_playbackFps = double.NaN;
		_playbackFpsShownAt = 0;
		UpdateDisplayStatus();
	}

	/// <summary>The preview's size in physical pixels, as laid out (zoom included), plus the frame rate while playing.</summary>
	private void UpdateDisplayStatus()
	{
		if (GetPreviewTransform() is not { } t)
		{
			StatusDisplayText.Text = "";
			return;
		}

		var size = $"{Math.Round(t.RenderedWidth * RenderScaling)}x{Math.Round(t.RenderedHeight * RenderScaling)}";
		var measured = _previewPlayer.IsPlaying && double.IsFinite(_playbackFps);
		StatusDisplayText.Text = measured ? string.Create(CultureInfo.InvariantCulture, $"{size}; {_playbackFps:0.000}") : size;

		if (measured && _playbackFps < _previewPlayer.PlaybackFrameRate * SlowPlaybackShare)
			StatusDisplayText.Foreground = Palette.Warning;
		else
			StatusDisplayText.ClearValue(TextBlock.ForegroundProperty);
	}
}
