using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Core.Ffmpeg;

/// <summary>
///     The video stream a render writes. By default the source's own, unchanged (output parity); OverlaySettings.OutputResolution,
///     OutputCodec and OutputEightBit change it for sharing. Everything downstream - the encoder picked, its options, the
///     overlay's size - reads this, not the source.
/// </summary>
public static class OutputVideo
{
	/// <summary>The short sides offered in Settings.</summary>
	public static readonly int[] Resolutions = [2160, 1440, 1080, 720];

	// H.264 needs about half as much bitrate again for HEVC's quality - so a switch to it doesn't come out visibly worse.
	private const double H264BitrateFactor = 1.5;

	/// <summary>
	///     The source as it'll be written: smaller (never larger), in another codec, or 8-bit - the bitrate shrinking with the pixel
	///     count. H.264 is always 8-bit: 10-bit H.264 plays almost nowhere and most GPUs can't encode it. Changing the size or the
	///     codec drops the source's level and tier for the encoder to pick.
	/// </summary>
	public static VideoInfo For(VideoInfo source, OverlaySettings settings)
	{
		(int width, int height) = settings.OutputResolution is { } shortSide && shortSide < Math.Min(source.Width, source.Height)
			? Scaled(source.Width, source.Height, shortSide)
			: (source.Width, source.Height);
		string codec = settings.OutputCodec is "h264" or "hevc" ? settings.OutputCodec : source.CodecName;
		bool sourceTenBit = source.PixFmt.Contains("10", StringComparison.Ordinal);
		bool eightBit = sourceTenBit && (settings.OutputEightBit || codec == "h264");
		string pixFmt = eightBit ? "yuv420p" : source.PixFmt;

		bool resized = width != source.Width || height != source.Height;
		bool recoded = codec != source.CodecName;
		if (!resized && !recoded && !eightBit) return source;

		double bitrateScale = (double)width * height / ((double)source.Width * source.Height) *
		                      (codec == "h264" && source.CodecName != "h264" ? H264BitrateFactor : 1);
		return source with
		{
			CodecName = codec,
			Width = width,
			Height = height,
			PixFmt = pixFmt,
			BitRate = (long)Math.Round(source.BitRate * bitrateScale),
			Level = resized || recoded ? 0 : source.Level,
			HighTier = resized || recoded ? null : source.HighTier
		};
	}

	/// <summary>The source's size with its short side at `shortSide`, both sides even (as 4:2:0 needs).</summary>
	internal static (int Width, int Height) Scaled(int width, int height, int shortSide)
	{
		return width >= height
			? (Even(width * (double)shortSide / height), Even(shortSide))
			: (Even(shortSide), Even(height * (double)shortSide / width));
	}

	private static int Even(double value)
	{
		return Math.Max(2, (int)Math.Round(value / 2) * 2);
	}
}
