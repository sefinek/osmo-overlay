using System.Numerics;
using System.Runtime.InteropServices;

namespace OsmoOverlay.Core.Overlay;

/// <summary>In-place premultiplied -> straight alpha conversion for BGRA frames (see OverlayRenderer.RenderInto).</summary>
internal static class BgraAlpha
{
	// 16.16 fixed-point 255/a, so the per-pixel work is a multiply and a shift instead of three divisions.
	private static readonly uint[] Reciprocal = BuildReciprocal();

	private static uint[] BuildReciprocal()
	{
		var table = new uint[256];
		for (var a = 1; a < 256; a++) table[a] = (uint)((255 << 16) / a);
		return table;
	}

	public static void Unpremultiply(byte[] bgra, int byteCount, int width)
	{
		var rowBytes = width * 4;
		var rows = byteCount / rowBytes;
		// Row bands in parallel - a 4K frame is ~8M pixels, and each band touches its own rows only.
		var bands = Math.Min(Environment.ProcessorCount, 16);
		var rowsPerBand = (rows + bands - 1) / bands;
		Parallel.For(0, bands, band =>
		{
			var firstRow = band * rowsPerBand;
			var rowCount = Math.Min(rowsPerBand, rows - firstRow);
			if (rowCount > 0)
				UnpremultiplyRange(MemoryMarshal.Cast<byte, uint>(bgra.AsSpan(firstRow * rowBytes, rowCount * rowBytes)));
		});
	}

	private static void UnpremultiplyRange(Span<uint> pixels)
	{
		var i = 0;
		// Whole vectors of fully transparent pixels (the vast majority of an overlay frame) are skipped
		// without touching them one by one.
		if (Vector.IsHardwareAccelerated)
		{
			var alphaMask = new Vector<uint>(0xFF000000);
			ReadOnlySpan<Vector<uint>> vectors = MemoryMarshal.Cast<uint, Vector<uint>>(pixels);
			for (var v = 0; v < vectors.Length; v++)
			{
				if ((vectors[v] & alphaMask) == Vector<uint>.Zero) continue;
				var start = v * Vector<uint>.Count;
				UnpremultiplyScalar(pixels.Slice(start, Vector<uint>.Count));
			}

			i = vectors.Length * Vector<uint>.Count;
		}

		UnpremultiplyScalar(pixels[i..]);
	}

	private static void UnpremultiplyScalar(Span<uint> pixels)
	{
		for (var i = 0; i < pixels.Length; i++)
		{
			var px = pixels[i];
			var a = px >> 24;
			// Fully transparent or fully opaque pixels are already identical in both representations -
			// which is nearly the entire overlay.
			if (a is 0 or 255) continue;

			var r = Reciprocal[a];
			var b = ((px & 0xFF) * r + 0x8000) >> 16;
			var g = (((px >> 8) & 0xFF) * r + 0x8000) >> 16;
			var red = (((px >> 16) & 0xFF) * r + 0x8000) >> 16;
			pixels[i] = (a << 24) | (Math.Min(red, 255u) << 16) | (Math.Min(g, 255u) << 8) | Math.Min(b, 255u);
		}
	}
}
