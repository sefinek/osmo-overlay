namespace OsmoOverlay.Core.Ffmpeg;

/// <summary>
///     Keeps the render's own comment (ffmpeg's "comment" tag, the "©cmt" item of moov/udta/meta/ilst) when a camera's
///     udta replaces ffmpeg's: the camera's meta carries its thumbnails and name, and nothing of ffmpeg's would survive.
/// </summary>
internal static class Mp4Comment
{
	private const string CommentItem = "©cmt";
	private const int FullBoxHeader = 4;

	/// <summary>
	///     `cameraUdta` with `ffmpegUdta`'s comment put into its metadata (replacing a comment of its own); `cameraUdta` itself
	///     when ffmpeg wrote no comment. A camera udta without a meta box takes ffmpeg's whole.
	/// </summary>
	public static Mp4Box Carry(Mp4Box? ffmpegUdta, Mp4Box cameraUdta)
	{
		if (ffmpegUdta?.Payload is not { } ffmpegPayload || cameraUdta.Payload is not { } cameraPayload) return cameraUdta;

		Mp4Box? ffmpegMeta = Mp4Box.ParseChildren(ffmpegPayload).FirstOrDefault(c => c.Type == "meta");
		if (ffmpegMeta is null || Items(ffmpegMeta)?.FirstOrDefault(i => i.Type == CommentItem) is not { } comment) return cameraUdta;

		List<Mp4Box> children = Mp4Box.ParseChildren(cameraPayload);
		int metaIndex = children.FindIndex(c => c.Type == "meta");
		if (metaIndex < 0)
		{
			children.Add(ffmpegMeta);
			return Mp4Box.Leaf("udta", Join(children));
		}

		if (children[metaIndex].Payload is not { } metaPayload || metaPayload.Length < FullBoxHeader) return cameraUdta;

		List<Mp4Box> metaChildren = Mp4Box.ParseChildren(metaPayload.AsSpan(FullBoxHeader));
		int ilstIndex = metaChildren.FindIndex(c => c.Type == "ilst");
		if (ilstIndex < 0 || metaChildren[ilstIndex].Payload is not { } ilstPayload) return cameraUdta;

		List<Mp4Box> items = Mp4Box.ParseChildren(ilstPayload);
		items.RemoveAll(i => i.Type == CommentItem);
		items.Add(comment);
		metaChildren[ilstIndex] = Mp4Box.Leaf("ilst", Join(items));
		children[metaIndex] = Mp4Box.Leaf("meta", [.. metaPayload.AsSpan(0, FullBoxHeader), .. Join(metaChildren)]);
		return Mp4Box.Leaf("udta", Join(children));
	}

	private static List<Mp4Box>? Items(Mp4Box meta)
	{
		if (meta.Payload is not { } payload || payload.Length < FullBoxHeader) return null;

		Mp4Box? ilst = Mp4Box.ParseChildren(payload.AsSpan(FullBoxHeader)).FirstOrDefault(c => c.Type == "ilst");
		return ilst?.Payload is { } items ? Mp4Box.ParseChildren(items) : null;
	}

	private static byte[] Join(IEnumerable<Mp4Box> boxes)
	{
		using var stream = new MemoryStream();
		foreach (Mp4Box box in boxes) box.WriteTo(stream);
		return stream.ToArray();
	}
}
