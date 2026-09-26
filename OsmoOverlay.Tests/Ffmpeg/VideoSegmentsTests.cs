using System.Globalization;
using OsmoOverlay.Core.Ffmpeg;

namespace OsmoOverlay.Tests.Ffmpeg;

[TestClass]
public sealed class VideoSegmentsTests
{
	private static VideoSegment Segment(long frames)
	{
		var video = new VideoInfo("hevc", "Main 10", 3840, 2160, "60000/1001", "yuv420p10le", "bt709", "bt709", "bt709", "tv",
			90_000_000, FrameCount: frames);
		return new VideoSegment("x.mp4", new SourceInfo(video, null, true, frames * 1001 / 60000.0, 2, null), 0);
	}

	[TestMethod]
	[DataRow(0L, 0, 0L)]
	[DataRow(99L, 0, 99L)]
	[DataRow(100L, 1, 0L)]
	[DataRow(149L, 1, 49L)]
	[DataRow(150L, 2, 0L)]
	// Past the end stays in the last segment rather than throwing - callers clamp first.
	[DataRow(500L, 2, 350L)]
	public void Locate_FindsSegmentAndLocalFrame(long frame, int expectedIndex, long expectedLocal)
	{
		IReadOnlyList<VideoSegment> segments = [Segment(100), Segment(50), Segment(200)];

		Assert.AreEqual((expectedIndex, expectedLocal), VideoSegments.Locate(segments, frame));
		Assert.AreEqual(350, segments.TotalFrameCount());
	}
}

[TestClass]
public sealed class ConcatListWriterTests
{
	[TestMethod]
	public void WriteAudioOnly_SelectsTheAudioStream_AndSetsTheFirstInpoint()
	{
		CultureInfo previous = CultureInfo.CurrentCulture;
		// A comma-decimal culture must not leak into the list - ffmpeg expects "198.5", not "198,5".
		CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pl-PL");
		var list = ConcatListWriter.WriteAudioOnly([@"C:\clips\a.mp4", @"C:\clips\it's b.mp4"], "0x2", 198.5);
		try
		{
			CollectionAssert.AreEqual(new[]
			{
				"ffconcat version 1.0",
				"stream",
				"exact_stream_id 0x2",
				"file 'C:/clips/a.mp4'",
				"inpoint 198.5",
				@"file 'C:/clips/it'\''s b.mp4'"
			}, File.ReadAllLines(list));
		}
		finally
		{
			CultureInfo.CurrentCulture = previous;
			File.Delete(list);
		}
	}

	[TestMethod]
	public void DeleteStale_RemovesOnlyOldLists()
	{
		var stale = ConcatListWriter.Write(["a.mp4"]);
		var fresh = ConcatListWriter.Write(["b.mp4"]);
		File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));
		try
		{
			ConcatListWriter.DeleteStale(TimeSpan.FromDays(1));

			Assert.IsFalse(File.Exists(stale));
			Assert.IsTrue(File.Exists(fresh), "a list a running render or preview may still use must stay");
		}
		finally
		{
			File.Delete(stale);
			File.Delete(fresh);
		}
	}
}
