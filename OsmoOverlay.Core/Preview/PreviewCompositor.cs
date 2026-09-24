using System.Runtime.InteropServices;
using SkiaSharp;

namespace OsmoOverlay.Core.Preview;

public static class PreviewCompositor
{
	/// <summary>
	///     Writes into <paramref name="result" />, which must be exactly canvasWidth * canvasHeight * 4 bytes.
	///     A null overlay leaves the plain video.
	/// </summary>
	public static void Compose(byte[] result, int canvasWidth, int canvasHeight, byte[] videoBgra, int videoStride,
		byte[]? overlayBgra, int overlayWidth, int overlayHeight)
	{
		var canvasInfo = new SKImageInfo(canvasWidth, canvasHeight, SKColorType.Bgra8888, SKAlphaType.Opaque);
		var rowBytes = canvasInfo.RowBytes;

		// The video frame is the opaque base layer anyway - copying it straight into the result and drawing
		// the overlay on top in place saves the intermediate native bitmap and its copy back out.
		if (result.Length != canvasInfo.BytesSize)
			throw new ArgumentException($"Expected a {canvasInfo.BytesSize}-byte buffer, got {result.Length}.", nameof(result));

		if (videoStride == rowBytes)
			Buffer.BlockCopy(videoBgra, 0, result, 0, result.Length);
		else
			for (var y = 0; y < canvasHeight; y++)
				Buffer.BlockCopy(videoBgra, y * videoStride, result, y * rowBytes, rowBytes);

		if (overlayBgra is null) return;

		GCHandle resultPin = GCHandle.Alloc(result, GCHandleType.Pinned);
		GCHandle overlayPin = GCHandle.Alloc(overlayBgra, GCHandleType.Pinned);
		try
		{
			using var bitmap = new SKBitmap();
			bitmap.InstallPixels(canvasInfo, resultPin.AddrOfPinnedObject(), rowBytes);
			using var canvas = new SKCanvas(bitmap);

			// Premultiplied, straight from OverlayRenderer.RenderInto(premultiplied: true) - no conversion needed.
			var overlayInfo = new SKImageInfo(overlayWidth, overlayHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
			using var overlayPixmap = new SKPixmap(overlayInfo, overlayPin.AddrOfPinnedObject(), overlayInfo.RowBytes);
			using SKImage overlayImage = SKImage.FromPixels(overlayPixmap);
			if (overlayWidth == canvasWidth && overlayHeight == canvasHeight)
				canvas.DrawImage(overlayImage, 0, 0, SKSamplingOptions.Default);
			else
				canvas.DrawImage(overlayImage, new SKRect(0, 0, canvasWidth, canvasHeight), SKSamplingOptions.Default);
		}
		finally
		{
			overlayPin.Free();
			resultPin.Free();
		}
	}

	/// <summary>An opaque BGRA frame (Compose's result) as a lossless PNG.</summary>
	public static byte[] EncodePng(byte[] bgra, int width, int height)
	{
		var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
		GCHandle pin = GCHandle.Alloc(bgra, GCHandleType.Pinned);
		try
		{
			using var pixmap = new SKPixmap(info, pin.AddrOfPinnedObject(), info.RowBytes);
			using SKData png = pixmap.Encode(SKEncodedImageFormat.Png, 100)
			                   ?? throw new InvalidOperationException("The frame couldn't be encoded as PNG.");
			return png.ToArray();
		}
		finally
		{
			pin.Free();
		}
	}
}
