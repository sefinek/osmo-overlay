namespace OsmoOverlay.Core.Ffmpeg;

internal static class HevcEncodingLimits
{
	// Main/Main 10 bitrate limits in bits/s. Camera headers can declare Main Tier above its limit.
	private static readonly (int Level, long Main, long High)[] Levels =
	[
		(30, 128_000, 0),
		(60, 1_500_000, 0),
		(63, 3_000_000, 0),
		(90, 6_000_000, 0),
		(93, 10_000_000, 0),
		(120, 12_000_000, 30_000_000),
		(123, 20_000_000, 50_000_000),
		(150, 25_000_000, 100_000_000),
		(153, 40_000_000, 160_000_000),
		(156, 60_000_000, 240_000_000),
		(180, 60_000_000, 240_000_000),
		(183, 120_000_000, 480_000_000),
		(186, 240_000_000, 800_000_000)
	];

	internal static VideoInfo For(VideoInfo video, long bitRate)
	{
		if (video.Level <= 0) return video;

		foreach (var level in Levels)
		{
			if (level.Level < video.Level) continue;

			bool high = video.HighTier == true || bitRate > level.Main;
			if (bitRate <= (high ? level.High : level.Main))
				return video with { Level = level.Level, HighTier = high };
		}

		return video with { Level = 0, HighTier = true };
	}
}
