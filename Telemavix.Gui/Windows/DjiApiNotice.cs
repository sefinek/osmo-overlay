using Avalonia.Controls;

namespace Telemavix.Gui;

internal static class DjiApiNotice
{
	public static Task ShowAsync(Window owner) => ConfirmDialog.ShowAsync(owner,
		Strings.DjiApiNotice_Title, Strings.DjiApiNotice_Message,
		secondaryText: Strings.DjiApiNotice_ReadTerms,
		onSecondary: () => AppUpdateFlow.OpenInBrowser("https://developer.dji.com/policies/flight_record"));
}
