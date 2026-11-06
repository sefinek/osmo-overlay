using Avalonia.Controls;
using OsmoOverlay.Core.Mapping;

namespace OsmoOverlay.Gui;

/// <summary>
///     The notice on what may be done with an export (ExportRightsWindow): the program's licence, the rights to the recording,
///     music and maps, and the terms of the maps this export really draws. The first one before any render is asked once
///     (OverlaySettings.ExportNoticeAccepted); afterwards it is only opened on request, for the current export.
/// </summary>
internal static class ExportNotice
{
	public static Task<bool> AskAsync(Window owner, IReadOnlyList<ExportMap> maps)
	{
		return new ExportRightsWindow(maps, true).ShowDialog<bool>(owner);
	}

	public static Task ShowAsync(Window owner, IReadOnlyList<ExportMap> maps)
	{
		return new ExportRightsWindow(maps, false).ShowDialog<bool>(owner);
	}

	internal static string UseText(MapUse use)
	{
		return use switch
		{
			MapUse.Allowed => Strings.MapTerms_Allowed,
			MapUse.PaidPlanOnly => Strings.MapTerms_PaidPlanOnly,
			MapUse.NotAllowed => Strings.MapTerms_NotAllowed,
			MapUse.Conditional => Strings.MapTerms_Conditional,
			_ => Strings.MapTerms_NotStated
		};
	}
}
