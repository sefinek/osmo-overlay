using SkiaSharp;

namespace OsmoOverlay.Core.Preview;

public static class PreviewCompositor
{
	public static byte[] Compose(int canvasWidth, int canvasHeight, byte[] videoBgra, int videoStride,
		byte[] overlayBgra, int overlayWidth, int overlayHeight)
	{
		var canvasInfo = new SKImageInfo(canvasWidth, canvasHeight, SKColorType.Bgra8888, SKAlphaType.Opaque);
		using var bitmap = new SKBitmap(canvasInfo);
		using var canvas = new SKCanvas(bitmap);

		var videoInfo = new SKImageInfo(canvasWidth, canvasHeight, SKColorType.Bgra8888, SKAlphaType.Opaque);
		using SKImage videoImage = SKImage.FromPixelCopy(videoInfo, videoBgra, videoStride);
		canvas.DrawImage(videoImage, 0, 0, SKSamplingOptions.Default);

		var overlayInfo = new SKImageInfo(overlayWidth, overlayHeight, SKColorType.Bgra8888, SKAlphaType.Unpremul);
		using SKImage overlayImage = SKImage.FromPixelCopy(overlayInfo, overlayBgra);
		if (overlayWidth == canvasWidth && overlayHeight == canvasHeight)
			canvas.DrawImage(overlayImage, 0, 0, SKSamplingOptions.Default);
		else
			canvas.DrawImage(overlayImage, new SKRect(0, 0, canvasWidth, canvasHeight), SKSamplingOptions.Default);

		return bitmap.Bytes;
	}
}
