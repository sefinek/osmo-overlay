namespace OsmoOverlay.Core;

/// <summary>
///     The overlapping transitions for the pictures made in C# (a 360 render): the end of the part before the cut and the
///     start of the part after it combined into one frame, the way ffmpeg's xfade does it for a flat recording - the
///     same directions, and `progress` = frame number in the overlap / its length, so the first frame is the earlier part
///     alone and the next frame after the overlap is the later one alone.
/// </summary>
internal static class CutTransitionBlend
{
	/// <summary>Combines `b` (the later part) into `a` (the earlier one), which receives the result. `scratch` is as long as the frames.</summary>
	public static void Blend(byte[] a, byte[] b, byte[] scratch, int width, int height, CutTransitionKind kind, double progress)
	{
		progress = Math.Clamp(progress, 0, 1);
		int stride = width * 4;
		int z = Math.Min(width, (int)(width * progress));

		switch (kind)
		{
			case CutTransitionKind.WipeLeft:
				for (int y = 0; y < height; y++)
					Buffer.BlockCopy(b, y * stride + (width - z) * 4, a, y * stride + (width - z) * 4, z * 4);
				break;
			case CutTransitionKind.WipeRight:
				for (int y = 0; y < height; y++)
					Buffer.BlockCopy(b, y * stride, a, y * stride, z * 4);
				break;
			case CutTransitionKind.SlideLeft:
				Buffer.BlockCopy(a, 0, scratch, 0, a.Length);
				for (int y = 0; y < height; y++)
				{
					int row = y * stride;
					Buffer.BlockCopy(scratch, row + z * 4, a, row, (width - z) * 4);
					Buffer.BlockCopy(b, row, a, row + (width - z) * 4, z * 4);
				}

				break;
			case CutTransitionKind.SlideRight:
				Buffer.BlockCopy(a, 0, scratch, 0, a.Length);
				for (int y = 0; y < height; y++)
				{
					int row = y * stride;
					Buffer.BlockCopy(b, row + (width - z) * 4, a, row, z * 4);
					Buffer.BlockCopy(scratch, row, a, row + z * 4, (width - z) * 4);
				}

				break;
			default:
				Crossfade(a, b, progress);
				break;
		}
	}

	private static void Crossfade(byte[] a, byte[] b, double progress)
	{
		int toward = (int)Math.Round(progress * 256);
		int keep = 256 - toward;
		for (int i = 0; i < a.Length; i++)
			a[i] = (byte)((a[i] * keep + b[i] * toward) >> 8);
	}
}
