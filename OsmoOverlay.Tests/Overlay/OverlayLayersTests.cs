using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Tests.Overlay;

[TestClass]
public sealed class OverlayLayersTests
{
	private static TextElement Widget(string id, string? layer = null, bool visible = true)
	{
		return new TextElement { X = 0, Y = 0, Id = id, LayerId = layer, Visible = visible };
	}

	private static string[] Ids(IEnumerable<OverlayElement> elements)
	{
		return [.. elements.Select(e => e.Id)];
	}

	[TestMethod]
	public void WithoutLayers_EachWidgetGetsOne_InDrawOrder()
	{
		var (layers, elements) = OverlayLayers.Normalize([Widget("a"), Widget("b"), Widget("c")], null);

		CollectionAssert.AreEqual(new[] { "c", "b", "a" }, layers.Select(l => l.Id).ToArray(), "the last drawn is in front, on top");
		CollectionAssert.AreEqual(new[] { "a", "b", "c" }, Ids(elements));
	}

	[TestMethod]
	public void ANewWidget_LandsOnTop()
	{
		var (layers, _) = OverlayLayers.Normalize([Widget("a"), Widget("b"), Widget("new")], [new OverlayLayer("b"), new OverlayLayer("a")]);

		CollectionAssert.AreEqual(new[] { "new", "b", "a" }, layers.Select(l => l.Id).ToArray());
	}

	[TestMethod]
	public void WidgetsOnALayer_AreDrawnTogether_InTheLayersOrder()
	{
		// "c" joined "a"'s layer; "b" is in front of both.
		var (_, elements) = OverlayLayers.Normalize([Widget("a"), Widget("b"), Widget("c", "a"), Widget("hidden", visible: false)],
			[new OverlayLayer("b"), new OverlayLayer("a")]);

		CollectionAssert.AreEqual(new[] { "hidden", "a", "c", "b" }, Ids(elements));
	}

	[TestMethod]
	public void AWidgetsOwnLayer_GoesWithIt_AHandMadeOneStays()
	{
		var (layers, _) = OverlayLayers.Normalize([Widget("a", "kept")],
			[new OverlayLayer("kept") { KeepWhenEmpty = true }, new OverlayLayer("a"), new OverlayLayer("empty") { KeepWhenEmpty = true }]);

		CollectionAssert.AreEqual(new[] { "kept", "empty" }, layers.Select(l => l.Id).ToArray(),
			"'a' moved off its own layer - that one goes, the hand-made empty one stays");
	}

	[TestMethod]
	public void AnEmptiedLayerTheUserSetUp_Stays()
	{
		var (layers, _) = OverlayLayers.Normalize([Widget("x")],
		[
			new OverlayLayer("renamed") { Name = "Titles" }, new OverlayLayer("muted") { Muted = true },
			new OverlayLayer("locked") { Locked = true }, new OverlayLayer("plain"), new OverlayLayer("x")
		]);

		CollectionAssert.AreEqual(new[] { "renamed", "muted", "locked", "x" }, layers.Select(l => l.Id).ToArray(),
			"a renamed or switched layer stays like an NLE track, an untouched one goes with its widget");
	}

	[TestMethod]
	public void MutedLayer_IsntDrawn()
	{
		IReadOnlyList<OverlayElement> drawn = OverlayLayers.Drawn([Widget("a"), Widget("b", "a"), Widget("c")],
			[new OverlayLayer("c"), new OverlayLayer("a") { Muted = true }]);

		CollectionAssert.AreEqual(new[] { false, false, true }, drawn.Select(e => e.Visible).ToArray());
	}

	[TestMethod]
	public void Solo_DrawsOnlySoloedLayers()
	{
		IReadOnlyList<OverlayElement> drawn = OverlayLayers.Drawn([Widget("a"), Widget("b"), Widget("c")],
			[new OverlayLayer("c") { Solo = true }, new OverlayLayer("b"), new OverlayLayer("a") { Solo = true }]);

		CollectionAssert.AreEqual(new[] { true, false, true }, drawn.Select(e => e.Visible).ToArray());
	}

	[TestMethod]
	public void NothingSilenced_ReturnsTheSameList()
	{
		List<OverlayElement> elements = [Widget("a")];

		Assert.AreSame(elements, OverlayLayers.Drawn(elements, [new OverlayLayer("a")]));
	}
}
