using OsmoOverlay.Core;
using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Gui;

public partial class MainWindow
{
	/// <summary>
	///     After the export options changed in Settings: another output codec or depth needs its encoder picked again
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
	}
}
