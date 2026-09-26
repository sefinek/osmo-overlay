namespace OsmoOverlay.Core.Overlay;

/// <summary>
///     A layer of the overlay, NLE style (OverlayPreset.Layers): widgets on it share its Id as their LayerKey. Muted layers
///     aren't drawn; while any layer is Solo, only Solo ones are (both in the preview and the render - OverlayLayers.Drawn).
///     Locked keeps its widgets from being moved or retimed in the GUI. A widget's own default layer (Id = the widget's Id)
///     goes once nothing is on it, unless it was renamed or switched (M/S/L) - then it stays like an NLE track, as does one
///     made by hand (KeepWhenEmpty), until deleted.
/// </summary>
public sealed record OverlayLayer(string Id)
{
	public string? Name { get; init; }
	public bool Muted { get; init; }
	public bool Solo { get; init; }
	public bool Locked { get; init; }
	public bool KeepWhenEmpty { get; init; }
}

/// <summary>
///     Keeps a preset's Layers and Elements in step. The layout's order is the draw order and the renderer never reads a
///     layer: Normalize orders the elements by layer (each layer's widgets together, the layers from the back to the front),
///     and Drawn hides what muted or unsoloed layers hold.
/// </summary>
public static class OverlayLayers
{
	/// <summary>The layer an element is on: its LayerId, or without one a layer of its own, keyed by its Id.</summary>
	public static string Key(OverlayElement element)
	{
		return element.LayerId ?? element.Id;
	}

	/// <summary>
	///     `layers` (top to bottom, the front first) completed and cleaned up for `elements`, and `elements` in the draw order
	///     that gives: hidden elements first (they aren't drawn), then each layer's, back to front, each layer's in their
	///     current order. A visible element on a layer not listed gets that layer on top - a widget just added lands in front,
	///     and a preset without Layers gets them in its current draw order. An empty layer goes unless KeptWhenEmpty.
	/// </summary>
	public static (List<OverlayLayer> Layers, List<OverlayElement> Elements) Normalize(IReadOnlyList<OverlayElement> elements,
		IReadOnlyList<OverlayLayer>? layers)
	{
		List<OverlayElement> visible = [.. elements.Where(e => e.Visible)];
		List<OverlayLayer> result = [];
		HashSet<string> seen = [];
		foreach (OverlayLayer layer in layers ?? [])
			if (seen.Add(layer.Id))
				result.Add(layer);

		// Walked front to back (the layout's end first), so the missing layers land on top in their draw order.
		var insertAt = 0;
		foreach (var key in visible.Select(Key).Reverse().Distinct())
			if (seen.Add(key))
				result.Insert(insertAt++, new OverlayLayer(key));

		HashSet<string> used = [.. visible.Select(Key)];
		result.RemoveAll(layer => !KeptWhenEmpty(layer) && !used.Contains(layer.Id));

		List<OverlayElement> ordered = [.. elements.Where(e => !e.Visible)];
		for (var i = result.Count - 1; i >= 0; i--)
		{
			var id = result[i].Id;
			ordered.AddRange(visible.Where(e => Key(e) == id));
		}

		return (result, ordered);
	}

	/// <summary>Made by hand, or set up by the user (renamed, muted, soloed, locked) - an empty one of those stays.</summary>
	private static bool KeptWhenEmpty(OverlayLayer layer)
	{
		return layer.KeepWhenEmpty || layer.Name is not null || layer.Muted || layer.Solo || layer.Locked;
	}

	/// <summary>`elements` as they're drawn: those on a muted layer, or - while any layer is Solo - on one that isn't, hidden.</summary>
	public static IReadOnlyList<OverlayElement> Drawn(IReadOnlyList<OverlayElement> elements, IReadOnlyList<OverlayLayer>? layers)
	{
		HashSet<string> silenced = Silenced(layers);
		if (silenced.Count == 0) return elements;

		return [.. elements.Select(e => e.Visible && silenced.Contains(Key(e)) ? e with { Visible = false } : e)];
	}

	/// <summary>The Ids of the layers not drawn: muted, or - while any layer is Solo - not soloed.</summary>
	public static HashSet<string> Silenced(IReadOnlyList<OverlayLayer>? layers)
	{
		if (layers is null || layers.Count == 0) return [];

		var anySolo = layers.Any(l => l.Solo);
		return [.. layers.Where(l => l.Muted || (anySolo && !l.Solo)).Select(l => l.Id)];
	}
}
