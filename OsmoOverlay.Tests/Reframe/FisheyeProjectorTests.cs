using OsmoOverlay.Core.Reframe;

namespace OsmoOverlay.Tests.Reframe;

[TestClass]
public sealed unsafe class FisheyeProjectorTests
{
	private const int LensSize = 64;
	private static readonly DualFisheye Lenses = new(FisheyeLayout.TwoStreams, LensSize);

	/// <summary>Front lens all red, back lens all blue - which lens a pixel came from is its color.</summary>
	private static byte[] Project(ReframeView view, int width = 32, int height = 18)
	{
		var front = new byte[LensSize * LensSize * 4];
		var back = new byte[LensSize * LensSize * 4];
		for (var i = 0; i < front.Length; i += 4)
		{
			front[i + 2] = 255;
			back[i] = 255;
		}

		var output = new byte[width * height * 4];
		fixed (byte* f = front, b = back, o = output)
		{
			new FisheyeProjector().Project(new LensImage(f, LensSize * 4), new LensImage(b, LensSize * 4), Lenses, Rotation.FromView(view),
				view.FovDegrees, o, width * 4, width, height);
		}

		return output;
	}

	private static (byte B, byte G, byte R, byte A) Pixel(byte[] bgra, int x, int y, int width = 32)
	{
		var i = (y * width + x) * 4;
		return (bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]);
	}

	[TestMethod]
	public void StraightAhead_IsTheFrontLens_TurnedAround_TheBack()
	{
		Assert.AreEqual(((byte)0, (byte)0, (byte)255, (byte)255), Pixel(Project(new ReframeView()), 16, 9));
		Assert.AreEqual(((byte)255, (byte)0, (byte)0, (byte)255), Pixel(Project(new ReframeView(Yaw: 180)), 16, 9));
	}

	[TestMethod]
	public void SideOn_TheSeamRunsDownTheMiddle()
	{
		byte[] side = Project(new ReframeView(Yaw: 90, FovDegrees: 60));

		Assert.AreEqual((byte)255, Pixel(side, 2, 9).R, "left of the seam: still the front lens");
		Assert.AreEqual((byte)255, Pixel(side, 29, 9).B, "right of it: the back one");
	}
}
