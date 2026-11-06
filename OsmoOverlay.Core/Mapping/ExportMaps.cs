using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Core.Mapping;

/// <summary>A tile source an export draws: the built-in provider with its terms, null for the user's own server (its licence is unknown to the app).</summary>
public sealed record ExportMap(string ProviderId, string Name, MapProvider? Provider);

/// <summary>Which map sources a render really draws - what the export rights notice lists, never guessed from a preset's name.</summary>
public static class ExportMaps
{
	/// <summary>
	///     The distinct sources of every visible Map widget and, with the route overview on, its card, resolved as the renderer
	///     resolves them (MapSources.Resolve). Whether a widget is hidden for lack of a GPS fix isn't known here, so a source
	///     may be listed that the finished film doesn't show.
	/// </summary>
	public static IReadOnlyList<ExportMap> Used(IEnumerable<OverlayElement>? layout, OverlaySettings settings)
	{
		var sources = MapSources.From(settings);
		List<string?> ids = [.. (layout ?? []).OfType<MapWidgetElement>().Where(m => m.Visible).Select(m => m.MapProviderId)];
		if (settings.ShowRouteIntro) ids.Add(settings.RouteIntroMapProvider);

		return
		[
			.. ids.Select(sources.Resolve).Distinct()
				.Select(id => new ExportMap(id, sources.NameOf(id), id == MapProviders.CustomId ? null : MapProviders.Get(id)))
		];
	}

	/// <summary>The sources of a saved project as a render of it would draw them.</summary>
	public static IReadOnlyList<ExportMap> Used(OverlayProject project, OverlaySettings current, IReadOnlyList<SettingsPreset> presets)
	{
		OverlaySettings settings = ProjectRender.SettingsFor(project, current, presets);
		return Used(project.Preset is { } preset ? OverlayLayers.Drawn(preset.Elements, preset.Layers) : null, settings);
	}

	public static IReadOnlyList<ExportMap> Merge(IEnumerable<IReadOnlyList<ExportMap>> lists)
	{
		return [.. lists.SelectMany(l => l).DistinctBy(m => m.ProviderId)];
	}
}
