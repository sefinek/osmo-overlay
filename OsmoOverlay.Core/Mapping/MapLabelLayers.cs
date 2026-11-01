namespace OsmoOverlay.Core.Mapping;

/// <summary>
///     The transparent name layers laid over a map's picture (street and place names), as one string so a map's settings, its
///     cache key and the mosaic builder pass them around like a single template. Several are joined by a line break, bottom first.
/// </summary>
public static class MapLabelLayers
{
	private const char Separator = '\n';

	public static string Join(params string[] templates)
	{
		return string.Join(Separator, templates);
	}

	public static string[] Split(string? templates)
	{
		return string.IsNullOrEmpty(templates) ? [] : templates.Split(Separator, StringSplitOptions.RemoveEmptyEntries);
	}
}
