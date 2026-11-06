using System.Globalization;
using Telemavix.Core.Ffmpeg;
using Telemavix.Core.Overlay;

namespace Telemavix.Core.Telemetry.External;

/// <summary>
///     A GPS track looked at with no recording: a recording of no files, as long as the track, with the same black picture
///     the full telemetry preview shows outside a recording (ExternalTrackPlayback with a recording of 0 seconds). It only
///     plays - there is nothing to render, cut or save a project for.
/// </summary>
public static class TrackOnlyRecording
{
	public static (FileSummary Summary, ExternalTrackPlayback Playback) Create(ExternalTrack track, OverlaySettings settings, TrackOnlyFormat format)
	{
		var sync = new TrackSynchronization(new TrackSyncAnchor(0, track.StartUtc));
		return Create(ExternalTrackPreview.Prepare(track, sync, 0, settings), format);
	}

	/// <summary>The recording for a playback already prepared (ExternalTrackPreview.Prepare with no recording), so it isn't processed twice.</summary>
	public static (FileSummary Summary, ExternalTrackPlayback Playback) Create(ExternalTrackPlayback playback, TrackOnlyFormat format)
	{
		ExternalTrack track = playback.Track;
		var video = new VideoInfo("h264", "", format.Width, format.Height, format.Fps.ToString(CultureInfo.InvariantCulture), "yuv420p", null, null, null, null, 0);
		var summary = new FileSummary([], [], [], track.DeviceName, video, null, playback.DurationSeconds, 0, true, playback.RawFrames,
			playback.DerivedFrames, TelemetryProcessor.Summarize(playback.DerivedFrames), (long)Math.Ceiling(playback.DurationSeconds * format.Fps),
			track.StartUtc);
		return (summary, playback);
	}
}

/// <summary>The size and frame rate of a track viewed with no recording: asked of the user, as no file says them.</summary>
public readonly record struct TrackOnlyFormat(int Width, int Height, double Fps)
{
	public static TrackOnlyFormat Default { get; } = new(1920, 1080, 50);

	public static TrackOnlyFormat Of(VideoInfo video)
	{
		return new TrackOnlyFormat(video.Width, video.Height, video.Fps);
	}
}
