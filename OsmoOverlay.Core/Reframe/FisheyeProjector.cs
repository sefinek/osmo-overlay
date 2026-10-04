using OsmoOverlay.Core.Cameras;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Core.Reframe;

/// <summary>One lens's picture as BGRA: LensSize x LensSize pixels from Pixels, Stride bytes a row.</summary>
public readonly unsafe struct LensImage(byte* pixels, int stride)
{
	public byte* Pixels { get; } = pixels;
	public int Stride { get; } = stride;
}

/// <summary>
///     What a 360 recording's flat picture is made of: its lenses, the view, and - when the view is leveled - the
///     per-frame rotation keeping the horizon upright. Immutable; a changed view is a new one.
/// </summary>
public sealed record Reframer(DualFisheye Lenses, HorizonLeveling? Leveling, ReframeView View)
{
	/// <summary>A recording's reframer - null for a flat one. Its telemetry (on the recording's timeline), read by its camera, levels it.</summary>
	public static Reframer? For(DualFisheye? lenses, IReadOnlyList<TelemetryFrame>? frames, ICameraFormat? camera, ReframeView view)
	{
		if (lenses is null) return null;
		return new Reframer(lenses, frames is not null && camera is not null ? HorizonLeveling.For(frames, camera) : null, view);
	}

	/// <summary>Turns a direction of the flat view into the lens direction it's taken from, at a moment of the recording.</summary>
	public Rotation RotationAt(double recordingSeconds)
	{
		var view = Rotation.FromView(View);
		return View.Level && Leveling is not null ? Leveling.At(recordingSeconds) * view : view;
	}
}

/// <summary>
///     The flat view of a 360 recording's two lenses - the preview's and the render's picture alike, so they can't
///     differ. The geometry is ffmpeg's v360 (dfisheye to flat) re-done here: its flat rays (rescale, tan of half the
///     FOV), its rotation convention (Rotation.FromView) and its dual-fisheye lookup (theta = acos|z| / pi scaled by
///     360 / lens FOV, the back lens mirrored) - so views picked before still look where they did - with the rotation
///     free to change every frame: v360 rebuilds its whole map on each change (~16 ms at 1080p), which made leveling
///     the horizon per frame 5x slower than rendering without it. Bilinear, rows in parallel. Not thread-safe (the
///     cached rays) - one caller at a time.
/// </summary>
public sealed unsafe class FisheyeProjector
{
	private float[] _rays = [];
	private (int Width, int Height, double Fov) _raysFor;

	/// <summary>The view into `destination` (BGRA, opaque), `width` x `height`, `stride` bytes a row.</summary>
	public void Project(LensImage front, LensImage back, DualFisheye lenses, Rotation rotation, double fovDegrees, byte* destination, int stride,
		int width, int height)
	{
		EnsureRays(width, height, fovDegrees);

		int size = lenses.LensSize;
		float scale = (float)(360 / lenses.LensFovDegrees);
		float m00 = (float)rotation.M00, m01 = (float)rotation.M01, m02 = (float)rotation.M02;
		float m10 = (float)rotation.M10, m11 = (float)rotation.M11, m12 = (float)rotation.M12;
		float m20 = (float)rotation.M20, m21 = (float)rotation.M21, m22 = (float)rotation.M22;
		float[] rays = _rays;
		IntPtr target = (nint)destination;

		Parallel.For(0, height, y =>
		{
			byte* row = (byte*)target + (long)y * stride;
			int ray = y * width * 3;
			for (int x = 0; x < width; x++, ray += 3, row += 4)
			{
				float rx = rays[ray], ry = rays[ray + 1], rz = rays[ray + 2];
				float dx = m00 * rx + m01 * ry + m02 * rz;
				float dy = m10 * rx + m11 * ry + m12 * rz;
				float dz = m20 * rx + m21 * ry + m22 * rz;

				float h = MathF.Sqrt(dx * dx + dy * dy);
				float inverse = h > 0 ? 1 / h : 0;
				float theta = MathF.Acos(MathF.Min(MathF.Abs(dz), 1)) / MathF.PI * scale;
				float uf = (0.5f * theta * dx * inverse + 0.5f) * (size - 1);
				float vf = (0.5f * theta * dy * inverse + 0.5f) * (size - 1);

				LensImage lens = front;
				if (dz < 0)
				{
					lens = back;
					uf = size - uf - 1;
				}

				Sample(lens, size, uf, vf, row);
			}
		});
	}

	/// <summary>Bilinear, in 8-bit fixed point, clamped to the lens's picture.</summary>
	private static void Sample(LensImage lens, int size, float uf, float vf, byte* output)
	{
		uf = Math.Clamp(uf, 0, size - 1);
		vf = Math.Clamp(vf, 0, size - 1);
		int u0 = (int)uf;
		int v0 = (int)vf;
		int u1 = Math.Min(u0 + 1, size - 1);
		int v1 = Math.Min(v0 + 1, size - 1);
		int du = (int)((uf - u0) * 256);
		int dv = (int)((vf - v0) * 256);

		byte* top = lens.Pixels + (long)v0 * lens.Stride;
		byte* bottom = lens.Pixels + (long)v1 * lens.Stride;
		byte* p00 = top + u0 * 4, p01 = top + u1 * 4, p10 = bottom + u0 * 4, p11 = bottom + u1 * 4;
		for (int c = 0; c < 3; c++)
		{
			int upper = p00[c] * (256 - du) + p01[c] * du;
			int lower = p10[c] * (256 - du) + p11[c] * du;
			output[c] = (byte)((upper * (256 - dv) + lower * dv + 32768) >> 16);
		}

		output[3] = 255;
	}

	/// <summary>v360's flat rays: x = tan(h_fov / 2) * rescale(i), y likewise from the view's aspect, z = 1, normalized.</summary>
	private void EnsureRays(int width, int height, double fovDegrees)
	{
		if (_raysFor == (width, height, fovDegrees)) return;

		double halfWidth = Math.Tan(AngleMath.DegToRad(fovDegrees / 2));
		double halfHeight = halfWidth * height / width;
		float[] rays = new float[width * height * 3];
		for (int y = 0; y < height; y++)
		{
			double ry = halfHeight * ((2.0 * y + 1) / height - 1);
			for (int x = 0; x < width; x++)
			{
				double rx = halfWidth * ((2.0 * x + 1) / width - 1);
				double length = Math.Sqrt(rx * rx + ry * ry + 1);
				int i = (y * width + x) * 3;
				rays[i] = (float)(rx / length);
				rays[i + 1] = (float)(ry / length);
				rays[i + 2] = (float)(1 / length);
			}
		}

		_rays = rays;
		_raysFor = (width, height, fovDegrees);
	}
}
