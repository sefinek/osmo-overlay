using OsmoOverlay.Core.Localization;
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
		double offset = 0.0;
		foreach (string path in inputPaths)
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
		for (int i = 0; i < segments.Count; i++)
		{
			long count = segments[i].FrameCount();
			if (frame < count || i == segments.Count - 1) return (i, frame);
			frame -= count;
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
