namespace OsmoOverlay.Core.Ffmpeg;

/// <summary>
///     One physical file in a stitched multi-file recording (DJI Osmo Action auto-splits long
///     recordings into consecutive files). StartOffsetSeconds is where its first video frame lands on the
///     combined timeline: the earlier files' frames / fps - not their container duration, which runs on to
///     where the audio ends (26 ms past the last frame on a real Osmo Action 6 split), so it would put each
///     later file's telemetry and preview frames a frame or two behind the render's (VideoSegments.Locate).
/// </summary>
public sealed record VideoSegment(string InputPath, SourceInfo Source, double StartOffsetSeconds);

public static class VideoSegments
{
	public static IReadOnlyList<VideoSegment> ProbeAll(IReadOnlyList<string> inputPaths)
	{
		return Join(inputPaths, [.. inputPaths.Select((path, i) => SourceProbe.Probe(path, encoderDetails: i == 0))]);
	}

	internal static IReadOnlyList<VideoSegment> Join(IReadOnlyList<string> inputPaths, IReadOnlyList<SourceInfo> sources)
	{
		var segments = new List<VideoSegment>(inputPaths.Count);
		double offset = 0.0;
		for (int i = 0; i < inputPaths.Count; i++)
		{
			segments.Add(new VideoSegment(inputPaths[i], sources[i], offset));
			offset += VideoDurationSeconds(sources[i]);
		}

		return segments;
	}

	/// <summary>The combined video's length - its frames, not the containers' durations (see VideoSegment).</summary>
	public static double TotalDurationSeconds(this IReadOnlyList<VideoSegment> segments)
	{
		VideoSegment last = segments[^1];
		return last.StartOffsetSeconds + VideoDurationSeconds(last.Source);
	}

	/// <summary>Exact total when every segment reports its frame count, otherwise estimated from each segment's duration.</summary>
	public static long TotalFrameCount(this IReadOnlyList<VideoSegment> segments)
	{
		return segments.Sum(FrameCount);
	}

	public static long FrameCount(this VideoSegment segment)
	{
		return FrameCount(segment.Source);
	}

	private static long FrameCount(SourceInfo source)
	{
		return source.Video.FrameCount ?? (long)Math.Ceiling(source.DurationSeconds * source.Video.Fps);
	}

	private static double VideoDurationSeconds(SourceInfo source)
	{
		return FrameCount(source) / source.Video.Fps;
	}

	/// <summary>Which segment frame `frame` of the combined timeline falls in, and its frame index within that segment.</summary>
	public static (int Index, long LocalFrame) Locate(IReadOnlyList<VideoSegment> segments, long frame)
	{
		for (int i = 0; i < segments.Count; i++)
		{
			long count = segments[i].FrameCount();
			if (frame < count || i == segments.Count - 1) return (i, frame);
			frame -= count;
		}

		throw new ArgumentException("No segments.", nameof(segments));
	}

	/// <summary>
	///     Where a time on the combined timeline is in the files' sound: the file and the time in it. The sound runs on across
	///     the files by their container durations - a camera split cuts its audio a little after the picture (+26 ms on a
	///     real Osmo Action 6 split; the picture's own clock in djmd steps one frame across it, and no sound repeats), so a
	///     later file's sound starts that much later than its picture. A full render's concat input and the preview
	///     (PlaybackSegment) join it the same way; a time in the second file onwards is thus not the picture's time in it.
	/// </summary>
	public static (int Index, double LocalSeconds) LocateAudio(IReadOnlyList<VideoSegment> segments, double seconds)
	{
		double start = 0;
		for (int i = 0; i < segments.Count; i++)
		{
			double duration = segments[i].Source.DurationSeconds;
			if (seconds < start + duration || i == segments.Count - 1) return (i, Math.Max(0, seconds - start));
			start += duration;
		}

		throw new ArgumentException("No segments.", nameof(segments));
	}

	public static bool AllHaveTelemetry(this IReadOnlyList<VideoSegment> segments)
	{
		return segments.All(s => s.Source.Camera is not null);
	}

	/// <summary>
	///     ffmpeg's concat demuxer does not re-encode, so segments must already share the same
	///     decode parameters - a mismatch here would otherwise surface as a corrupt or garbled render
	///     instead of a clear error. They must also come from the same camera (or all from one no registered camera
	///     knows): a mix can't be stitched into one continuous telemetry-driven overlay. Files a user picked that don't go
	///     together are an expected outcome, so it's a message for them, not an exception - null when they can be joined.
	/// </summary>
	public static string? FindMismatch(IReadOnlyList<VideoSegment> segments)
	{
		VideoSegment first = segments[0];
		string firstName = Path.GetFileName(first.InputPath);

		foreach (VideoSegment segment in segments.Skip(1))
		{
			string name = Path.GetFileName(segment.InputPath);
			if (segment.Source.Camera?.Format.Id != first.Source.Camera?.Format.Id)
			{
				return string.Format(CoreStrings.Join_DifferentCamera, name, Describe(segment.Source), firstName, Describe(first.Source));
			}

			if (segment.Source.Fisheye != first.Source.Fisheye)
				return string.Format(CoreStrings.Join_DifferentLens, name, firstName);

			VideoInfo a = first.Source.Video;
			VideoInfo b = segment.Source.Video;
			if (a.Width != b.Width || a.Height != b.Height || a.FrameRate != b.FrameRate || a.PixFmt != b.PixFmt)
			{
				return string.Format(CoreStrings.Join_DifferentFormat, name, $"{b.Width}x{b.Height}, {b.FrameRate} fps, {b.PixFmt}", firstName,
					$"{a.Width}x{a.Height}, {a.FrameRate} fps, {a.PixFmt}");
			}
		}

		return null;
	}

	private static string Describe(SourceInfo source)
	{
		return source.Camera?.Format.DisplayName ?? CoreStrings.Join_UnknownCamera;
	}
}
