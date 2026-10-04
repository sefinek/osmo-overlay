using System.Globalization;
using OsmoOverlay.Cameras.Dji;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Cameras;
using OsmoOverlay.Core.Ffmpeg;

namespace OsmoOverlay.Tests.Ffmpeg;

[TestClass]
public sealed class VideoSegmentsTests
{
	private static VideoSegment Segment(long frames)
	{
		var video = new VideoInfo("hevc", "Main 10", 3840, 2160, "60000/1001", "yuv420p10le", "bt709", "bt709", "bt709", "tv",
			90_000_000, FrameCount: frames);
		return new VideoSegment("x.mp4", new SourceInfo(video, null, frames * 1001 / 60000.0, null, new CameraRecording(new DjiOsmoFormat(), 2)), 0);
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

	[TestMethod]
	public void Join_PlacesEachFileAfterThePreviousFilesFrames_NotTheirContainerDuration()
	{
		// A real Osmo Action 6 split: the container runs on 26 ms past the last frame, to where the audio ends.
		SourceInfo first = Segment(89234).Source with { DurationSeconds = 1488.746667 };
		SourceInfo second = Segment(89230).Source with { DurationSeconds = 1488.661333 };

		IReadOnlyList<VideoSegment> segments = VideoSegments.Join(["a.mp4", "b.mp4"], [first, second]);

		Assert.AreEqual(89234 * 1001 / 60000.0, segments[1].StartOffsetSeconds, 1e-9);
		Assert.AreEqual((89234 + 89230) * 1001 / 60000.0, segments.TotalDurationSeconds(), 1e-9);
	}

	/// <summary>The three files of a real Osmo Action 6 recording: frames, and container durations (where each file's sound ends).</summary>
	private static IReadOnlyList<VideoSegment> RealSplit()
	{
		return VideoSegments.Join(["a.mp4", "b.mp4", "c.mp4"],
		[
			Segment(89234).Source with { DurationSeconds = 1488.746667 },
			Segment(89230).Source with { DurationSeconds = 1488.661333 },
			Segment(49041).Source with { DurationSeconds = 818.16735 }
		]);
	}

	[TestMethod]
	public void LocateAudio_RunsOnByTheContainerDurations()
	{
		IReadOnlyList<VideoSegment> segments = RealSplit();

		Assert.AreEqual((0, 10.0), VideoSegments.LocateAudio(segments, 10));
		// The second file's picture starts at 1488.7206, its sound 26 ms later - until then it's the first file's.
		(int index, double local) = VideoSegments.LocateAudio(segments, segments[1].StartOffsetSeconds);
		Assert.AreEqual(0, index);
		Assert.AreEqual(segments[1].StartOffsetSeconds, local, 1e-9);
		(index, local) = VideoSegments.LocateAudio(segments, 2000);
		Assert.AreEqual(1, index);
		Assert.AreEqual(2000 - 1488.746667, local, 1e-9);
		(index, local) = VideoSegments.LocateAudio(segments, 3000);
		Assert.AreEqual(2, index);
		Assert.AreEqual(3000 - 1488.746667 - 1488.661333, local, 1e-9);
	}

	[TestMethod]
	public void SoundFor_TheFirstFile_GoesWithThePicture()
	{
		IReadOnlyList<VideoSegment> segments = RealSplit();
		var piece = new RenderPiece(600, 300);

		FfmpegPipeline.SoundSpan sound = FfmpegPipeline.SoundFor(segments, piece, 0, 0, 600 * 1001 / 60000.0, 60000, 1001);

		Assert.IsTrue(sound.WithPicture);
	}

	[TestMethod]
	public void SoundFor_ALaterFile_StartsAtItsOwnSpot()
	{
		IReadOnlyList<VideoSegment> segments = RealSplit();
		long start = 89234 + 6000;
		var piece = new RenderPiece(start, 89230 - 6000 + 600);

		FfmpegPipeline.SoundSpan sound = FfmpegPipeline.SoundFor(segments, piece, 1, 2, 6000 * 1001 / 60000.0, 60000, 1001);

		Assert.IsFalse(sound.WithPicture);
		Assert.AreEqual((1, 2), (sound.First, sound.Last));
		Assert.AreEqual(start * 1001 / 60000.0 - 1488.746667, sound.StartSeconds, 1e-9);
		Assert.AreEqual(6000 * 1001 / 60000.0 - 0.026100, sound.StartSeconds, 1e-6, "26.1 ms before the picture's time in the file");
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
		string list = ConcatListWriter.WriteAudioOnly([@"C:\clips\a.mp4", @"C:\clips\it's b.mp4"], "0x2", 198.5);
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
		string stale = ConcatListWriter.Write(["a.mp4"]);
		string fresh = ConcatListWriter.Write(["b.mp4"]);
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
