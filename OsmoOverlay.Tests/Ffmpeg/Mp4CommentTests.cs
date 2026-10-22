using System.Text;
using OsmoOverlay.Core.Ffmpeg;

namespace OsmoOverlay.Tests.Ffmpeg;

[TestClass]
public sealed class Mp4CommentTests
{
	private const string CommentItem = "©cmt";

	private static byte[] Data(string text)
	{
		return [0, 0, 0, 1, 0, 0, 0, 0, .. Encoding.UTF8.GetBytes(text)];
	}

	private static Mp4Box Item(string type, string text)
	{
		return Mp4Box.Leaf(type, [.. Mp4Box.Leaf("data", Data(text)).ToBytes()]);
	}

	private static byte[] Bytes(params Mp4Box[] boxes)
	{
		using var stream = new MemoryStream();
		foreach (Mp4Box box in boxes) box.WriteTo(stream);
		return stream.ToArray();
	}

	private static Mp4Box Meta(params Mp4Box[] items)
	{
		return Mp4Box.Leaf("meta", [0, 0, 0, 0, .. Bytes(Mp4Box.Leaf("hdlr", new byte[24]), Mp4Box.Leaf("ilst", Bytes(items)))]);
	}

	private static Mp4Box Udta(params Mp4Box[] children)
	{
		return Mp4Box.Leaf("udta", Bytes(children));
	}

	private static List<Mp4Box> Items(Mp4Box udta)
	{
		Mp4Box meta = Mp4Box.ParseChildren(udta.Payload!).Single(c => c.Type == "meta");
		Mp4Box ilst = Mp4Box.ParseChildren(meta.Payload!.AsSpan(4)).Single(c => c.Type == "ilst");
		return Mp4Box.ParseChildren(ilst.Payload!);
	}

	private static string Text(Mp4Box item)
	{
		Mp4Box data = Mp4Box.ParseChildren(item.Payload!).Single();
		return Encoding.UTF8.GetString(data.Payload!.AsSpan(8));
	}

	[TestMethod]
	public void Carry_PutsTheCommentIntoTheCamerasMeta_AndKeepsWhatItHas()
	{
		Mp4Box camera = Udta(Mp4Box.Leaf("fsid", [1, 2, 3]), Meta(Item("©too", "DJI OsmoAction6"), Item("covr", "picture")), Mp4Box.Leaf("Xtra", [9]));
		Mp4Box ffmpeg = Udta(Meta(Item("©too", "Lavf"), Item(CommentItem, "OsmoOverlay 1.0")));

		Mp4Box result = Mp4Comment.Carry(ffmpeg, camera);

		List<Mp4Box> items = Items(result);
		CollectionAssert.AreEquivalent(new[] { "©too", "covr", CommentItem }, items.Select(i => i.Type).ToArray());
		Assert.AreEqual("DJI OsmoAction6", Text(items.Single(i => i.Type == "©too")));
		Assert.AreEqual("OsmoOverlay 1.0", Text(items.Single(i => i.Type == CommentItem)));
		CollectionAssert.AreEqual(new[] { "fsid", "meta", "Xtra" }, Mp4Box.ParseChildren(result.Payload!).Select(c => c.Type).ToArray());
	}

	[TestMethod]
	public void Carry_ReplacesACommentTheCameraHad()
	{
		Mp4Box camera = Udta(Meta(Item(CommentItem, "camera's own")));
		Mp4Box ffmpeg = Udta(Meta(Item(CommentItem, "OsmoOverlay 1.0")));

		List<Mp4Box> items = Items(Mp4Comment.Carry(ffmpeg, camera));

		Assert.AreEqual("OsmoOverlay 1.0", Text(items.Single(i => i.Type == CommentItem)));
	}

	[TestMethod]
	public void Carry_ACameraWithoutMeta_TakesFfmpegsWhole()
	{
		Mp4Box camera = Udta(Mp4Box.Leaf("fsid", [1, 2, 3]));
		Mp4Box ffmpeg = Udta(Meta(Item("©too", "Lavf"), Item(CommentItem, "OsmoOverlay 1.0")));

		Mp4Box result = Mp4Comment.Carry(ffmpeg, camera);

		Assert.AreEqual("OsmoOverlay 1.0", Text(Items(result).Single(i => i.Type == CommentItem)));
		CollectionAssert.AreEqual(new[] { "fsid", "meta" }, Mp4Box.ParseChildren(result.Payload!).Select(c => c.Type).ToArray());
	}

	[TestMethod]
	public void Carry_WithoutAFfmpegComment_LeavesTheCamerasUdtaAsItIs()
	{
		Mp4Box camera = Udta(Meta(Item("©too", "DJI OsmoAction6")));

		Assert.AreSame(camera, Mp4Comment.Carry(Udta(Meta(Item("©too", "Lavf"))), camera));
		Assert.AreSame(camera, Mp4Comment.Carry(null, camera));
		Assert.AreSame(camera, Mp4Comment.Carry(Udta(Mp4Box.Leaf("fsid", [1])), camera));
	}
}
