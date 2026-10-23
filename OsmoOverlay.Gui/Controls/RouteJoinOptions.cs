using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Gui;

/// <summary>The route joins as a menu lists them (RouteJoins.Menu) with their names - shared by the cut editor and a trail widget's GPS loss choice.</summary>
internal static class RouteJoinOptions
{
	public static IReadOnlyList<string> Labels { get; } = [.. RouteJoins.Menu.Select(Label)];

	/// <summary>The menu row of a join - the default's for none.</summary>
	public static int IndexOf(RouteJoin? join)
	{
		return Math.Max(0, RouteJoins.Menu.ToList().IndexOf(join ?? RouteJoins.Default));
	}

	/// <summary>The join of a menu row - null for the default, which a cut stores as "not chosen".</summary>
	public static RouteJoin? At(int index)
	{
		RouteJoin join = RouteJoins.Menu[Math.Clamp(index, 0, RouteJoins.Menu.Count - 1)];
		return join == RouteJoins.Default ? null : join;
	}

	private static string Label(RouteJoin join)
	{
		return join switch
		{
			RouteJoin.Straight => Strings.Route_JoinStraight,
			RouteJoin.Gap => Strings.Route_JoinGap,
			_ => Strings.Route_JoinDashed
		};
	}
}
