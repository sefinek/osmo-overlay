namespace OsmoOverlay.Core.Preview;

/// <summary>
///     How far into the pictures the comparison looks - one value for every tile, so the same part of every picture is in view.
///     Positions are fractions of a picture (0 to 1 across and down), not pixels, which is what lets tiles of different sizes
///     (or videos of different resolutions) follow each other.
/// </summary>
/// <param name="Scale">1 = the whole picture.</param>
/// <param name="CenterX">The point of the picture in the middle of the view, across (0 to 1).</param>
/// <param name="CenterY">The same, down.</param>
public readonly record struct CompareZoom(double Scale, double CenterX, double CenterY)
{
	public const double MinScale = 1;
	public const double MaxScale = 8;

	public static CompareZoom Whole => new(MinScale, 0.5, 0.5);

	/// <summary>
	///     Zoomed by a factor with the point of the picture under (anchorX, anchorY) - where the pointer is, as fractions of the
	///     view - staying under it, like zooming a map with the wheel.
	/// </summary>
	public CompareZoom ZoomAt(double factor, double anchorX, double anchorY)
	{
		double scale = Math.Clamp(Scale * factor, MinScale, MaxScale);
		double pointX = CenterX + (anchorX - 0.5) / Scale;
		double pointY = CenterY + (anchorY - 0.5) / Scale;
		return Clamped(scale, pointX - (anchorX - 0.5) / scale, pointY - (anchorY - 0.5) / scale);
	}

	/// <summary>Moved by a drag of the pointer, as fractions of the view: the picture follows it.</summary>
	public CompareZoom Pan(double deltaX, double deltaY)
	{
		return Clamped(Scale, CenterX - deltaX / Scale, CenterY - deltaY / Scale);
	}

	/// <summary>Whole picture when it is all in view - a view can't leave the picture.</summary>
	private static CompareZoom Clamped(double scale, double centerX, double centerY)
	{
		double half = 0.5 / scale;
		return new CompareZoom(scale, Math.Clamp(centerX, half, 1 - half), Math.Clamp(centerY, half, 1 - half));
	}
}
