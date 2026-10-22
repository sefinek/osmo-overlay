namespace OsmoOverlay.Core.Preview;

/// <summary>
///     Where a video view takes played frames from - on the GUI's render thread, once per display refresh: the preview's
///     (PlaybackFrameSource) or one tile of the comparison (CompareFrameFeed). A frame handed out belongs to the caller
///     until it passes it to PlaybackFrameSource.Release.
/// </summary>
public interface IPlayedFrames
{
	bool IsPlaying { get; }

	PreviewMeasurements? Measurements { get; }

	/// <summary>The frame due at this refresh, if a new one is.</summary>
	ComposedPreviewFrame? TakeDueFrame();
}
