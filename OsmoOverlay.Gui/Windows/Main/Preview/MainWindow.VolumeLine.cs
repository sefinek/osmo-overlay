using OsmoOverlay.Core;

namespace OsmoOverlay.Gui;

/// <summary>
///     The sound's level over the recording (VolumeLine), drawn and dragged on the expanded timeline's audio track
///     (PreviewTimeline.Volume): the preview plays it (PreviewPlayer.SetVolumeLine) and the render applies it
///     (RenderOptions.Volume). It is part of the undo history and of the project, and belongs to the loaded recording.
/// </summary>
public partial class MainWindow
{
	private List<VolumePoint> _volumeLine = [];

	private double SourceSeconds => _summary is { Video.Fps: > 0 } summary ? SourceFrames / summary.Video.Fps : 0;

	private void WireVolumeLine()
	{
		foreach (PreviewTimeline timeline in Timelines) timeline.VolumeEdited += OnTimelineVolumeEdited;
	}

	private void OnTimelineVolumeEdited(IReadOnlyList<VolumePoint> points, bool final)
	{
		_volumeLine = [.. points];
		ShowVolumeLine();
		CommitHistory("volume");
		if (final) SealHistory();
	}

	private void ShowVolumeLine()
	{
		foreach (PreviewTimeline timeline in Timelines) timeline.Volume = _volumeLine;
		_previewPlayer.SetVolumeLine(_volumeLine);
	}

	private void SetVolumeLine(IEnumerable<VolumePoint> points)
	{
		_volumeLine = VolumeLine.Normalize(points, SourceSeconds);
		ShowVolumeLine();
	}
}
