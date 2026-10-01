using OsmoOverlay.Core;
using OsmoOverlay.Core.Ffmpeg;

namespace OsmoOverlay.Gui;

/// <summary>How the time readout next to the compact timeline shows the position and the recording's end.</summary>
public enum PreviewTimeFormat
{
	Seconds,
	Milliseconds,
	Timecode,
	CameraTimecode
}

internal static class PreviewTimeFormats
{
	/// <summary>In the order the readout cycles through on a click; shared by the toolbar flyout and Settings.</summary>
	public static readonly IReadOnlyList<ChoiceOption<PreviewTimeFormat>> Options =
	[
		new("Minutes and seconds", PreviewTimeFormat.Seconds),
		new("With milliseconds", PreviewTimeFormat.Milliseconds),
		new("Timecode", PreviewTimeFormat.Timecode),
		new("Camera timecode", PreviewTimeFormat.CameraTimecode)
	];

	public static PreviewTimeFormat Parse(string? value)
	{
		return Enum.TryParse(value, out PreviewTimeFormat format) && Enum.IsDefined(format) ? format : PreviewTimeFormat.Seconds;
	}

	public static PreviewTimeFormat Next(PreviewTimeFormat format)
	{
		return (PreviewTimeFormat)(((int)format + 1) % Options.Count);
	}

	/// <summary>
	///     `frame` is the frame at `time` (PreviewFrames.IndexAt); the end of the recording is passed as its frame count,
	///     so it reads the way an NLE shows an out point. Camera timecode counts on from the first file's - without one it
	///     falls back to the timecode from zero.
	/// </summary>
	public static string Format(PreviewTimeFormat format, TimeSpan time, long frame, double fps, string? cameraTimecode)
	{
		switch (format)
		{
			case PreviewTimeFormat.Milliseconds:
				return TimeText.Format(time.TotalSeconds);
			case PreviewTimeFormat.CameraTimecode when cameraTimecode is not null && SmpteTimecode.AddFrames(cameraTimecode, frame, fps) is { } timecode:
				return timecode;
			case PreviewTimeFormat.Timecode or PreviewTimeFormat.CameraTimecode when SmpteTimecode.FromFrame(frame, fps) is { } timecode:
				return timecode;
			default:
				return time.ToString(time.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss");
		}
	}
}
