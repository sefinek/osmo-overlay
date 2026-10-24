using System.Diagnostics;
using System.Globalization;

namespace OsmoOverlay.Core.Ffmpeg;

/// <summary>How an overlay-only render with a transparent background is written.</summary>
public enum AlphaCodec
{
	/// <summary>ProRes 4444 with a 16-bit alpha channel in a .mov - what Premiere, DaVinci Resolve and Final Cut take as a keyed layer.</summary>
	ProRes4444,

	/// <summary>VP9 with an alpha channel in a .webm - a lot smaller, for browsers and editors that read it.</summary>
	Vp9
}

/// <summary>
///     The overlay alone on a transparent background, to lay over the footage in an editor - the keyed counterpart of the green
///     screen render, without the key. Like it, it has no source picture, sound, music or camera metadata: the finished BGRA
///     frames RenderJob draws go straight to the encoder. The colors are always BT.709 - it's a graphic, not the source's footage.
/// </summary>
public static class AlphaOverlay
{
	public static string Extension(AlphaCodec format)
	{
		return format == AlphaCodec.ProRes4444 ? ".mov" : ".webm";
	}

	public static string EncoderName(AlphaCodec format)
	{
		return format == AlphaCodec.ProRes4444 ? "prores_ks" : "libvpx-vp9";
	}

	/// <summary>The format a command-line name stands for (prores, vp9), or null.</summary>
	public static AlphaCodec? Parse(string name)
	{
		return name.ToLowerInvariant() switch
		{
			"prores" or "prores4444" or "mov" => AlphaCodec.ProRes4444,
			"vp9" or "webm" => AlphaCodec.Vp9,
			_ => null
		};
	}

	/// <summary>
	///     About how large the file gets, to warn before a render fills the drive: ProRes 4444 spends roughly a third of a byte on every
	///     pixel of every frame whatever is in it, VP9 a lot less. A deliberate overestimate.
	/// </summary>
	public static long EstimateBytes(AlphaCodec format, int width, int height, long frames)
	{
		double bytesPerPixel = format == AlphaCodec.ProRes4444 ? 0.4 : 0.06;
		return (long)(width * (double)height * frames * bytesPerPixel);
	}

	/// <param name="output">The size written (OutputVideo.For) - the frames on the pipe are this big.</param>
	/// <param name="totalFrames">The frames the pipe carries; the encoder stops after them.</param>
	internal static List<string> Arguments(string outputPath, VideoInfo source, VideoInfo output, AlphaCodec format, bool overwrite,
		RenderEncodeSettings encode, long totalFrames)
	{
		(int num, int den) = FfmpegPipeline.ParseFrameRate(source.FrameRate);
		List<string> args =
		[
			"-hide_banner", overwrite ? "-y" : "-n",
			"-f", "rawvideo",
			"-pix_fmt", "bgra",
			"-s", $"{output.Width}x{output.Height}",
			"-r", $"{num}/{den}",
			"-i", "pipe:0",
			"-an",
			"-vf", $"scale=out_color_matrix=bt709:out_range=tv:threads=0,format={(format == AlphaCodec.ProRes4444 ? "yuva444p10le" : "yuva420p")}",
			"-frames:v", totalFrames.ToString(CultureInfo.InvariantCulture)
		];

		if (format == AlphaCodec.ProRes4444)
		{
			args.AddRange(["-c:v", "prores_ks", "-profile:v", "4444", "-vendor", "apl0", "-alpha_bits", "16", "-pix_fmt", "yuva444p10le"]);
			if (encode.FfmpegFastStart) args.AddRange(["-movflags", "+faststart"]);
		}
		else
		{
			// auto-alt-ref 0: VP9 drops the alpha plane of the frames it makes with the alt-ref lookahead.
			args.AddRange(["-c:v", "libvpx-vp9", "-pix_fmt", "yuva420p", "-auto-alt-ref", "0", "-b:v", "0", "-crf", "24", "-row-mt", "1", "-cpu-used", "3"]);
		}

		if (encode.Comment is { } comment) args.AddRange(["-metadata", "comment=" + comment]);
		args.AddRange(["-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709", "-color_range", "tv"]);
		args.Add(outputPath);
		return args;
	}

	public static Process Start(string outputPath, VideoInfo source, VideoInfo output, AlphaCodec format, bool overwrite, RenderEncodeSettings encode,
		long totalFrames)
	{
		return Process.Start(ProcessHelper.CreateHiddenWithStdin("ffmpeg", Arguments(outputPath, source, output, format, overwrite, encode, totalFrames)))
		       ?? throw new InvalidOperationException("Failed to start ffmpeg.");
	}
}
