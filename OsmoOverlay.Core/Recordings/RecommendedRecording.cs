namespace OsmoOverlay.Core;

/// <summary>How a recording's setting stands against what is recommended: right, workable but not ideal, or something that will hurt the result.</summary>
public enum CheckLevel
{
	Good,
	Warning,
	Bad
}

/// <summary>
///     The settings a camera is recommended to record with, and how each setting of a real file is judged against them. Only a hint
///     shown next to the recording's details - nothing is blocked. Good is what is recommended; Warning is workable but loses
///     something (a lower resolution or frame rate, 8-bit colour, a lower bitrate); Bad is far enough from it to cost visible quality.
///     Fps is the preferred frame rate (50: no flicker under 50 Hz mains lighting), AlternativeFps is accepted as well (59.94).
/// </summary>
public sealed record RecommendedRecording(
	int Width,
	int Height,
	double Fps,
	double AlternativeFps,
	long MinVideoBitrate,
	long MinAudioBitrate,
	bool FullRangeColor = false,
	bool TenBitRecommended = true)
{
	/// <summary>Full HD is the least that still reads as sharp on a 4K recommendation.</summary>
	private const int MinWarningWidth = 1920;

	private const int MinWarningHeight = 1080;

	/// <summary>Below this a frame rate no longer looks like motion to the overlay's gauges either.</summary>
	private const double MinWarningFps = 24;

	/// <summary>A bitrate this far below the recommended one is a warning, further below is bad.</summary>
	private const double WarningBitrateShare = 0.6;

	public CheckLevel Resolution(int width, int height)
	{
		if (width >= Width && height >= Height) return CheckLevel.Good;

		return width >= MinWarningWidth && height >= MinWarningHeight ? CheckLevel.Warning : CheckLevel.Bad;
	}

	public bool AcceptsFps(double fps)
	{
		return Math.Abs(fps - Fps) < 0.5 || Math.Abs(fps - AlternativeFps) < 0.5;
	}

	public CheckLevel FrameRate(double fps)
	{
		if (AcceptsFps(fps)) return CheckLevel.Good;

		return fps >= MinWarningFps ? CheckLevel.Warning : CheckLevel.Bad;
	}

	/// <summary>
	///     HEVC with 10-bit colour is recommended (TenBitRecommended; where a camera can't record 10-bit at the recommended frame rate
	///     8-bit HEVC is as good); otherwise 8-bit HEVC and H.264 work, and anything else isn't what these cameras write.
	/// </summary>
	public CheckLevel Codec(string codecName, string pixelFormat)
	{
		bool hevc = codecName.Equals("hevc", StringComparison.OrdinalIgnoreCase);
		if (hevc && (!TenBitRecommended || pixelFormat.Contains("10le", StringComparison.OrdinalIgnoreCase))) return CheckLevel.Good;

		return hevc || codecName.Equals("h264", StringComparison.OrdinalIgnoreCase) ? CheckLevel.Warning : CheckLevel.Bad;
	}

	/// <summary>
	///     Rec.709 primaries, transfer and matrix, limited range (full range too where the camera writes it - FullRangeColor). Other
	///     or missing tags are a warning, not worse: they only change how a player guesses the colours.
	/// </summary>
	public CheckLevel Color(string? primaries, string? transfer, string? colorSpace, string? range)
	{
		bool rec709 = primaries == "bt709" && transfer == "bt709" && colorSpace == "bt709";
		bool rangeOk = range == "tv" || (FullRangeColor && range == "pc");
		return rec709 && rangeOk ? CheckLevel.Good : CheckLevel.Warning;
	}

	public CheckLevel VideoBitrate(long bitsPerSecond)
	{
		return Bitrate(bitsPerSecond, MinVideoBitrate);
	}

	public CheckLevel AudioBitrate(long bitsPerSecond)
	{
		return Bitrate(bitsPerSecond, MinAudioBitrate);
	}

	private static CheckLevel Bitrate(long bitsPerSecond, long minimum)
	{
		if (bitsPerSecond >= minimum) return CheckLevel.Good;

		return bitsPerSecond >= minimum * WarningBitrateShare ? CheckLevel.Warning : CheckLevel.Bad;
	}
}
