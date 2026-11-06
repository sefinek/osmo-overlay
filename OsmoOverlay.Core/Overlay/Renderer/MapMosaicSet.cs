using OsmoOverlay.Core.Mapping;
using MapMosaicKey = (string Url, int Zoom, double MaxZoomOutFactor, string? LabelsUrl);
using ElementMapKey = (string? ProviderId, int Zoom, double MaxZoomOutFactor, bool? Labels);

namespace OsmoOverlay.Core.Overlay;

/// <summary>A Map widget's source settings, the mosaic that serves them, and one widget that has them (the fetch reads its provider).</summary>
internal readonly record struct MapAssignment(ElementMapKey Source, MapMosaicKey Key, MapWidgetElement Element);

/// <summary>Which mosaic each distinct Map widget source draws - what a fetch is asked for, compared to tell a superseded one from the current.</summary>
public sealed class MapAssignments : IEquatable<MapAssignments>
{
	internal MapAssignments(IReadOnlyList<MapAssignment> items)
	{
		Items = items;
	}

	internal IReadOnlyList<MapAssignment> Items { get; }

	public bool Equals(MapAssignments? other)
	{
		return other is not null && Items.Select(i => (i.Source, i.Key)).ToHashSet().SetEquals(other.Items.Select(i => (i.Source, i.Key)));
	}

	public override bool Equals(object? obj)
	{
		return Equals(obj as MapAssignments);
	}

	public override int GetHashCode()
	{
		return Items.Count;
	}
}

/// <summary>The mosaics one fetch produced, by key - owned until the renderer takes them (ApplyMapMosaic), disposed otherwise.</summary>
public sealed class MapMosaicSet : IDisposable
{
	private readonly Dictionary<MapMosaicKey, RouteMapMosaic> _mosaics = [];

	internal void Add(MapMosaicKey key, RouteMapMosaic mosaic)
	{
		_mosaics[key] = mosaic;
	}

	internal RouteMapMosaic? Take(MapMosaicKey key)
	{
		return _mosaics.Remove(key, out RouteMapMosaic? mosaic) ? mosaic : null;
	}

	public void Dispose()
	{
		foreach (RouteMapMosaic mosaic in _mosaics.Values) mosaic.Dispose();
		_mosaics.Clear();
	}
}
