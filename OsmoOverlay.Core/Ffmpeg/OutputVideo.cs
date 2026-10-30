using System.Globalization;
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

	/// <summary>The shapes (width:height) offered in Settings; null there is the source's own.</summary>
	public static readonly string[] Aspects = ["16:9", "9:16", "4:5", "1:1", "4:3"];

	// Below this a shape is the source's: the even rounding of a scaled size (2704x1520 -> 1280x720) isn't a new shape.
	private const double ShapeTolerance = 0.01;

	// H.264 needs about half as much bitrate again for HEVC's quality - so a switch to it doesn't come out visibly worse.
	private const double H264BitrateFactor = 1.5;

	/// <summary>
	///     The source as it'll be written: smaller (never larger), in another codec, or 8-bit - the bitrate shrinking with the pixel
	///     count. H.264 is always 8-bit: 10-bit H.264 plays almost nowhere and most GPUs can't encode it. Changing the size or the
	///     codec drops the source's level and tier for the encoder to pick.
	/// </summary>
	public static VideoInfo For(VideoInfo source, OverlaySettings settings)
	{
		(int width, int height) = Framed(source.Width, source.Height, settings.OutputAspect, settings.OutputPadToAspect);
		if (settings.OutputResolution is { } shortSide && shortSide < Math.Min(width, height))
			(width, height) = Scaled(width, height, shortSide);
		string codec = settings.OutputCodec is "h264" or "hevc" ? settings.OutputCodec : source.CodecName;
		bool sourceTenBit = source.PixFmt.Contains("10", StringComparison.Ordinal);
		bool eightBit = sourceTenBit && (settings.OutputEightBit || codec == "h264");
		string pixFmt = eightBit ? "yuv420p" : source.PixFmt;

		bool resized = width != source.Width || height != source.Height;
		bool recoded = codec != source.CodecName;
		if (!resized && !recoded && !eightBit) return source;

		// Black bars cost next to nothing: the bitrate follows the picture's own pixels, not the frame's.
		double pictureArea = settings.OutputPadToAspect && Reshapes(source.Width, source.Height, width, height)
			? (double)source.Width * source.Height * Math.Pow(Math.Min(width / (double)source.Width, height / (double)source.Height), 2)
			: (double)width * height;
		double bitrateScale = pictureArea / ((double)source.Width * source.Height) *
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

	/// <summary>
	///     The size of the picture in the shape `aspect` names ("9:16"), at the source's own pixel scale: the largest part of the
	///     source that has the shape (`pad` false), or the smallest frame of the shape the whole source fits in (`pad` true). The
	///     source's own size when the shape is the source's (within <see cref="ShapeTolerance" />) or isn't one of <see cref="Aspects" />.
	/// </summary>
	internal static (int Width, int Height) Framed(int width, int height, string? aspect, bool pad)
	{
		return ParseAspect(aspect) is not { } target || Math.Abs(width / (double)height / target - 1) < ShapeTolerance
			? (width, height)
			: Framed(width, height, target, pad);
	}

	/// <summary>The same for a shape given as width over height.</summary>
	internal static (int Width, int Height) Framed(int width, int height, double target, bool pad)
	{
		bool wider = width / (double)height > target;
		(int framedWidth, int framedHeight) = wider == pad
			? (width, Even(width / target))
			: (Even(height * target), height);
		return pad ? (framedWidth, framedHeight) : (Math.Min(framedWidth, width), Math.Min(framedHeight, height));
	}

	/// <summary>Whether `output` has another shape than `source` - one that needs cutting or bars, not just scaling.</summary>
	internal static bool Reshapes(VideoInfo source, VideoInfo output)
	{
		return Reshapes(source.Width, source.Height, output.Width, output.Height);
	}

	private static bool Reshapes(int sourceWidth, int sourceHeight, int width, int height)
	{
		return Math.Abs((double)width * sourceHeight / ((double)height * sourceWidth) - 1) >= ShapeTolerance;
	}

	/// <summary>A shape as the command line gives it: one of <see cref="Aspects" />, with "@bars" after it for the whole picture on black bars.</summary>
	public static bool TryParseOption(string text, out string aspect, out bool bars)
	{
		const string barsSuffix = "@bars";
		bars = text.EndsWith(barsSuffix, StringComparison.OrdinalIgnoreCase);
		aspect = bars ? text[..^barsSuffix.Length] : text;
		return Aspects.Contains(aspect);
	}

	/// <summary>The width over the height of a shape in <see cref="Aspects" />, or null for anything else.</summary>
	internal static double? ParseAspect(string? aspect)
	{
		if (aspect is null || !Aspects.Contains(aspect)) return null;

		string[] parts = aspect.Split(':');
		return int.Parse(parts[0], CultureInfo.InvariantCulture) / (double)int.Parse(parts[1], CultureInfo.InvariantCulture);
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
