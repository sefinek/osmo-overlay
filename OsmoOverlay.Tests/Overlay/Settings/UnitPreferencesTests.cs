using System.Text.Json;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Tests.Overlay;

[TestClass]
public sealed class UnitPreferencesTests
{
	[TestMethod]
	public void ForRegion_UsesWhatTheCountryUses()
	{
		Assert.AreEqual(UnitPreferences.UnitedStates, UnitPreferences.ForRegion("us"));
		Assert.AreEqual(UnitPreferences.UnitedStates, UnitPreferences.ForRegion("LR"));
		Assert.AreEqual(UnitPreferences.UnitedKingdom, UnitPreferences.ForRegion("GB"));
		Assert.AreEqual(UnitPreferences.Metric, UnitPreferences.ForRegion("PL"));
		Assert.AreEqual(UnitPreferences.Metric, UnitPreferences.ForRegion("CA"));
		Assert.AreEqual(UnitPreferences.Metric, UnitPreferences.ForRegion(null));
	}

	[TestMethod]
	public void ApplyTo_GivesEachWidgetTheUnitsOfItsQuantity()
	{
		var units = new UnitPreferences(UnitSystem.Imperial, UnitSystem.Metric, UnitSystem.Imperial, UnitSystem.Metric);
		OverlayElement[] elements =
		[
			new SpeedGaugeElement { Id = "s", X = 0, Y = 0 },
			new ElevationElement { Id = "e", X = 0, Y = 0 },
			new HeightElement { Id = "h", X = 0, Y = 0 },
			new VerticalSpeedElement { Id = "v", X = 0, Y = 0 },
			new DistanceElement { Id = "d", X = 0, Y = 0 },
			new HomeDistanceElement { Id = "hd", X = 0, Y = 0 },
			new AmbientTemperatureElement { Id = "t", X = 0, Y = 0, Units = UnitSystem.Imperial },
			new TripStatElement { Id = "ms", X = 0, Y = 0, Stat = TripStatKind.MaxSpeed },
			new TripStatElement { Id = "eg", X = 0, Y = 0, Stat = TripStatKind.ElevationGain },
			new TripStatElement { Id = "mh", X = 0, Y = 0, Stat = TripStatKind.MaxHomeDistance, Units = UnitSystem.Imperial },
			new ProfileChartElement { Id = "ps", X = 0, Y = 0, Series = ProfileSeries.Speed },
			new ProfileChartElement { Id = "pe", X = 0, Y = 0, Series = ProfileSeries.Elevation }
		];

		Dictionary<string, UnitSystem> applied = elements.Select(units.ApplyTo).ToDictionary(e => e.Id, UnitsOf);

		Assert.AreEqual(UnitSystem.Imperial, applied["s"]);
		Assert.AreEqual(UnitSystem.Imperial, applied["e"]);
		Assert.AreEqual(UnitSystem.Imperial, applied["h"]);
		Assert.AreEqual(UnitSystem.Imperial, applied["v"]);
		Assert.AreEqual(UnitSystem.Metric, applied["d"]);
		Assert.AreEqual(UnitSystem.Metric, applied["hd"]);
		Assert.AreEqual(UnitSystem.Metric, applied["t"]);
		Assert.AreEqual(UnitSystem.Imperial, applied["ms"]);
		Assert.AreEqual(UnitSystem.Imperial, applied["eg"]);
		Assert.AreEqual(UnitSystem.Metric, applied["mh"]);
		Assert.AreEqual(UnitSystem.Imperial, applied["ps"]);
		Assert.AreEqual(UnitSystem.Imperial, applied["pe"]);
	}

	[TestMethod]
	public void ApplyTo_CoversEveryWidgetTypeWithUnits()
	{
		var imperial = new UnitPreferences(UnitSystem.Imperial, UnitSystem.Imperial, UnitSystem.Imperial, UnitSystem.Imperial);
		IEnumerable<Type> withUnits = typeof(OverlayElement).Assembly.GetTypes()
			.Where(t => t is { IsAbstract: false } && t.IsSubclassOf(typeof(OverlayElement)) && t.GetProperty("Units")?.PropertyType == typeof(UnitSystem));

		foreach (Type type in withUnits)
		{
			OverlayElement element = OverlayPreset.CreateDrone("x", "X").Elements.Concat(OverlayPreset.CreateDefault("d", "D").Elements).First(e => e.GetType() == type);
			Assert.AreEqual(UnitSystem.Imperial, UnitsOf(imperial.ApplyTo(element with { Id = "u" })), type.Name);
		}
	}

	[TestMethod]
	public void ApplyTo_ConvertsAFixedGaugeScale()
	{
		var gauge = new SpeedGaugeElement { Id = "s", X = 0, Y = 0, MaxSpeed = 120 };

		var applied = (SpeedGaugeElement)UnitPreferences.UnitedStates.ApplyTo(gauge);

		Assert.AreEqual(UnitSystem.Imperial, applied.Units);
		Assert.AreEqual(80.0, applied.MaxSpeed);
	}

	[TestMethod]
	public void ApplyTo_MetricLeavesTheBuiltInLayoutsAsTheyAre()
	{
		foreach (OverlayPreset preset in new[] { OverlayPreset.CreateDefault("d", "D"), OverlayPreset.CreateDrone("x", "X"), OverlayPreset.CreateFpv("y", "Y") })
			Assert.AreEqual(JsonSerializer.Serialize(preset), JsonSerializer.Serialize(UnitPreferences.Metric.ApplyTo(preset)));
	}

	[TestMethod]
	public void ApplyTo_ImperialLeavesNoMeasuredWidgetMetric()
	{
		OverlayPreset preset = UnitPreferences.UnitedStates.ApplyTo(OverlayPreset.CreateDrone("x", "X"));

		Assert.IsTrue(preset.Elements.Select(UnitsOf).All(u => u == UnitSystem.Imperial));
		Assert.IsTrue(preset.Elements.Any(e => e is HeightElement { Units: UnitSystem.Imperial }));
	}

	[TestMethod]
	public void Settings_RoundTripTheChoice()
	{
		var settings = new OverlaySettings { MeasurementUnits = UnitPreferences.UnitedKingdom };

		OverlaySettings read = JsonSerializer.Deserialize<OverlaySettings>(JsonSerializer.Serialize(settings))!;

		Assert.AreEqual(UnitPreferences.UnitedKingdom, read.MeasurementUnits);
		Assert.AreEqual(UnitPreferences.Metric, UnitPreferences.From(new OverlaySettings()));
	}

	private static UnitSystem UnitsOf(OverlayElement element)
	{
		return element switch
		{
			SpeedGaugeElement e => e.Units,
			ElevationElement e => e.Units,
			DistanceElement e => e.Units,
			TemperatureElementBase e => e.Units,
			DeviceDistanceElement e => e.Units,
			VerticalSpeedElement e => e.Units,
			DroneBatteryElement e => e.Units,
			TripProgressBarElement e => e.Units,
			ProfileChartElement e => e.Units,
			TripStatElement e => e.Units,
			RangeLimitedElement e => e.Units,
			_ => UnitSystem.Imperial
		};
	}
}
