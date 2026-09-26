using OsmoOverlay.Core.Logging;
using SkiaSharp;

namespace OsmoOverlay.Core.Overlay;

/// <summary>
///     Image: a picture file drawn at its own pixel size (at the 4K reference, like every widget's size). Decoded once per
///     path for the renderer's lifetime; one that can't be read draws nothing - never a placeholder, which would end up
///     burned into the render - and is logged once.
/// </summary>
public sealed partial class OverlayRenderer
{
	private readonly Dictionary<string, SKImage?> _images = new(StringComparer.OrdinalIgnoreCase);

	private readonly SKSamplingOptions _imageSampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

	private void DrawImage(SKCanvas canvas, ImageElement element)
	{
		if (string.IsNullOrWhiteSpace(element.ImagePath) || LoadImage(element.ImagePath) is not { } image) return;

		var opacity = Math.Clamp(element.Opacity, 0f, 1f);
		if (opacity <= 0f) return;

		canvas.DrawImage(image, 0, 0, _imageSampling, opacity < 1f ? AlphaPaint(opacity) : null);
	}

	private SKImage? LoadImage(string path)
	{
		if (_images.TryGetValue(path, out SKImage? cached)) return cached;

		// A new file picked: the ones no widget shows any more go - an 8192 px picture is 256 MB decoded.
		HashSet<string> used = new(Layout.OfType<ImageElement>().Select(i => i.ImagePath ?? ""), StringComparer.OrdinalIgnoreCase);
		foreach (var stale in _images.Keys.Where(k => !used.Contains(k)).ToList())
		{
			_images[stale]?.Dispose();
			_images.Remove(stale);
		}

		SKImage? image = null;
		try
		{
			if (OverlayElementBounds.ImageSize(path) is null)
				throw new InvalidDataException($"not a readable image up to {OverlayElementBounds.MaxImageDimension} px a side");

			using SKBitmap? bitmap = SKBitmap.Decode(path);
			image = bitmap is null ? null : SKImage.FromBitmap(bitmap);
			if (image is null) throw new InvalidDataException("the file couldn't be decoded");
		}
		catch (Exception ex)
		{
			AppLogger.Warn($"Image widget: can't use '{path}' - {ex.Message}");
		}

		_images[path] = image;
		return image;
	}

	private void DisposeImages()
	{
		foreach (SKImage? image in _images.Values) image?.Dispose();
		_images.Clear();
	}
}
