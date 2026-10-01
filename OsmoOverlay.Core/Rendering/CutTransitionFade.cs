namespace OsmoOverlay.Core;

/// <summary>
///     The fade through a color of a cut's transition for the pictures made in C# - the preview and a 360 render. The same curve as
///     ffmpeg's fade filter, which the render of a flat recording uses (FfmpegPipeline): the first frame of a fade-in is
///     fully the color, the first frame of a fade-out is untouched, so the two halves meet on a full frame of color.
/// </summary>
internal static class CutTransitionFade
{
	/// <summary>How far the picture is toward the color (0 untouched, 1 solid) at frame `k` of the piece, and whether that color is white.</summary>
	public static (double Amount, bool White) At(RenderPiece piece, long k, double fps)
	{
		double amount = 0;
		bool white = false;
		if (piece.TransitionIn is { Overlaps: false } fadeIn)
		{
			int n = fadeIn.HalfFrames(fps, piece.FrameCount);
			if (k < n)
			{
				amount = 1 - (double)k / n;
				white = fadeIn.IsWhite;
			}
		}

		if (piece.TransitionOut is { Overlaps: false } fadeOut)
		{
			int n = fadeOut.HalfFrames(fps, piece.FrameCount);
			long start = piece.FrameCount - n;
			if (k >= start && (double)(k - start) / n > amount)
			{
				amount = (double)(k - start) / n;
				white = fadeOut.IsWhite;
			}
		}

		return (amount, white);
	}

	/// <summary>Blends the picture of a BGRA frame toward black or white; the alpha channel stays.</summary>
	public static void Apply(byte[] bgra, double amount, bool white)
	{
		if (amount <= 0) return;

		int keep = (int)Math.Round((1 - Math.Min(amount, 1)) * 256);
		int toward = white ? (256 - keep) * 255 : 0;
		for (int i = 0; i + 3 < bgra.Length; i += 4)
		{
			bgra[i] = (byte)((bgra[i] * keep + toward) >> 8);
			bgra[i + 1] = (byte)((bgra[i + 1] * keep + toward) >> 8);
			bgra[i + 2] = (byte)((bgra[i + 2] * keep + toward) >> 8);
		}
	}
}
