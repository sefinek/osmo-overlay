using OsmoOverlay.Core.Mapping;

namespace OsmoOverlay.Tests.Mapping;

[TestClass]
public sealed class MapLabelLayersTests
{
	[TestMethod]
	public void SplitIsTheReverseOfJoin()
	{
		string[] layers = ["https://a/{z}/{y}/{x}", "https://b/{z}/{y}/{x}"];

		CollectionAssert.AreEqual(layers, MapLabelLayers.Split(MapLabelLayers.Join(layers)));
		Assert.IsEmpty(MapLabelLayers.Split(null));
		Assert.IsEmpty(MapLabelLayers.Split(""));
	}

	[TestMethod]
	public void PublicEsriLabels_IncludeTheStreetNamesNotOnlyThePlaces()
	{
		var sources = new MapSources(null, ShowEsriLabels: true);

		string[] layers = MapLabelLayers.Split(sources.LabelsUrlTemplate(MapProviders.AutoSatelliteId));

		Assert.HasCount(2, layers);
		Assert.Contains("World_Transportation", layers[0]);
		Assert.Contains("World_Boundaries_and_Places", layers[1]);
	}
}
