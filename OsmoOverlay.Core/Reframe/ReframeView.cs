using System.Globalization;

namespace OsmoOverlay.Core.Reframe;

/// <summary>How a 360 camera's two fisheye lenses sit in the file.</summary>
public enum FisheyeLayout
{
	/// <summary>A video stream per lens (Insta360 .insv) - the second one is the front lens.</summary>
	TwoStreams,

	/// <summary>Both lenses side by side in one picture, twice as wide as high (Insta360 .lrv) - the left one is the front lens.</summary>
	SideBySide
}

/// <summary>
///     A 360 recording's lenses (SourceInfo.Fisheye). LensSize is one lens's square picture in pixels; LensFovDegrees how
///     much of the sphere each lens covers - 190 lines the seam up on an Insta360 X4 (185 doubles the picture along it,
///     195 and up leave a gap). Both lenses' pictures are upright, no flip needed - verified on a real X4 .insv and .lrv.
/// </summary>
public sealed record DualFisheye(FisheyeLayout Layout, int LensSize, double LensFovDegrees = 190);

/// <summary>
///     Where a flat view looks into a 360 recording, in degrees: Yaw turns right, Pitch up, Roll clockwise, FovDegrees is
///     the view's width - applied like ffmpeg's v360 does (yaw, then pitch, then roll). With Level the view is taken from a
///     world kept upright by the camera's accelerometer (HorizonLeveling), so pitch and roll are against the real horizon.
/// </summary>
public sealed record ReframeView(double Yaw = 0, double Pitch = 0, double Roll = 0, double FovDegrees = 100, bool Level = true)
{
	public const double MinFov = 30;
	public const double MaxFov = 150;

	/// <summary>Angles wrapped to -180..180, the pitch to -90..90 and the FOV kept in range.</summary>
	public ReframeView Normalized()
	{
		return this with { Yaw = Wrap(Yaw), Pitch = Math.Clamp(Pitch, -90, 90), Roll = Wrap(Roll), FovDegrees = Math.Clamp(FovDegrees, MinFov, MaxFov) };
	}

	/// <summary>"yaw,pitch,roll[,fov]" in degrees - the CLI's --view.</summary>
	public static bool TryParse(string text, out ReframeView view)
	{
		view = new ReframeView();
		var parts = text.Split(',', StringSplitOptions.TrimEntries);
		if (parts.Length is < 3 or > 4) return false;

		var values = new double[4];
		values[3] = view.FovDegrees;
		for (var i = 0; i < parts.Length; i++)
			if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
				return false;

		view = new ReframeView(values[0], values[1], values[2], values[3]).Normalized();
		return true;
	}

	/// <summary>The --view argument this view parses back from (Level is a flag of its own).</summary>
	public string ToArgument()
	{
		return string.Create(CultureInfo.InvariantCulture, $"{Yaw:0.#},{Pitch:0.#},{Roll:0.#},{FovDegrees:0.#}");
	}

	private static double Wrap(double degrees)
	{
		var wrapped = (degrees + 180) % 360;
		return (wrapped < 0 ? wrapped + 360 : wrapped) - 180;
	}
}

public static class Reframing
{
	/// <summary>16:9 as wide as one lens - every output pixel then has about a lens pixel behind it at a ~100 degree view.</summary>
	public static (int Width, int Height) OutputSize(DualFisheye lenses)
	{
		var width = lenses.LensSize / 2 * 2;
		return (width, (int)Math.Round(width * 9 / 16.0 / 2) * 2);
	}
}
