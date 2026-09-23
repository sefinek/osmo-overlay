namespace OsmoOverlay.Core.Ffmpeg;

/// <summary>
///     One physical file in a stitched multi-file recording (DJI Osmo Action auto-splits long
///     recordings into consecutive files). StartOffsetSeconds is this segment's position on the
///     combined, virtual timeline - segment 0 starts at 0, segment 1 at segment 0's duration, etc.
/// </summary>
public sealed record VideoSegment(string InputPath, SourceInfo Source, double StartOffsetSeconds);

public static class VideoSegments
{
	public static IReadOnlyList<VideoSegment> ProbeAll(IReadOnlyList<string> inputPaths)
	{
		var segments = new List<VideoSegment>(inputPaths.Count);
		var offset = 0.0;
		foreach (var path in inputPaths)
		{
			SourceInfo source = SourceProbe.Probe(path);
			segments.Add(new VideoSegment(path, source, offset));
			offset += source.DurationSeconds;
		}

		return segments;
	}

	public static double TotalDurationSeconds(this IReadOnlyList<VideoSegment> segments)
	{
		VideoSegment last = segments[^1];
		return last.StartOffsetSeconds + last.Source.DurationSeconds;
	}

	/// <summary>Exact total when every segment reports its frame count, otherwise estimated from each segment's duration.</summary>
	public static long TotalFrameCount(this IReadOnlyList<VideoSegment> segments)
	{
		return segments.Sum(FrameCount);
	}

	public static long FrameCount(this VideoSegment segment)
	{
		return segment.Source.Video.FrameCount ?? (long)Math.Ceiling(segment.Source.DurationSeconds * segment.Source.Video.Fps);
	}

	/// <summary>Which segment frame `frame` of the combined timeline falls in, and its frame index within that segment.</summary>
	public static (int Index, long LocalFrame) Locate(IReadOnlyList<VideoSegment> segments, long frame)
	{
		for (var i = 0; i < segments.Count; i++)
		{
			var count = segments[i].FrameCount();
			if (frame < count || i == segments.Count - 1) return (i, frame);
			frame -= count;
		}

		throw new ArgumentException("No segments.", nameof(segments));
	}

	public static bool AllHaveDjmdTrack(this IReadOnlyList<VideoSegment> segments)
	{
		return segments.All(s => s.Source.HasDjmdTrack);
	}

	/// <summary>
	///     ffmpeg's concat demuxer does not re-encode, so segments must already share the same
	///     decode parameters - a mismatch here would otherwise surface as a corrupt or garbled render
	///     instead of a clear error. Telemetry presence must also be all-or-nothing: a mix can't be
	///     stitched into one continuous telemetry-driven overlay.
	/// </summary>
	public static void Validate(IReadOnlyList<VideoSegment> segments)
	{
		VideoSegment first = segments[0];
		var hasDjmd = first.Source.HasDjmdTrack;

		foreach (VideoSegment segment in segments.Skip(1))
		{
			if (segment.Source.HasDjmdTrack != hasDjmd)
				throw new InvalidOperationException(
					$"{segment.InputPath} {(segment.Source.HasDjmdTrack ? "has" : "has no")} a 'djmd' telemetry " +
					$"stream, but {first.InputPath} {(hasDjmd ? "does" : "doesn't")} - all segments must either " +
					"all have telemetry or all lack it.");

			VideoInfo a = first.Source.Video;
			VideoInfo b = segment.Source.Video;
			if (a.Width != b.Width || a.Height != b.Height || a.FrameRate != b.FrameRate || a.PixFmt != b.PixFmt)
				throw new InvalidOperationException(
					$"{segment.InputPath} ({b.Width}x{b.Height}, {b.FrameRate} fps, {b.PixFmt}) doesn't match " +
					$"{first.InputPath} ({a.Width}x{a.Height}, {a.FrameRate} fps, {a.PixFmt}) - segments must share " +
					"the same resolution, frame rate and pixel format to be stitched together.");
		}
	}
}
