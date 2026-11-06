using Avalonia.Interactivity;
using Telemavix.Core;
using Telemavix.Core.Mapping;
using Telemavix.Core.Overlay;

namespace Telemavix.Gui;

/// <summary>The export rights notice (ExportNotice): asked before every render or queue run of any kind, and opened on request.</summary>
public partial class MainWindow
{
	/// <summary>True when the render may go on: the notice was confirmed now. The one gate every way of starting an export passes.</summary>
	private async Task<bool> ConfirmExportRightsAsync(Func<IReadOnlyList<ExportMap>> maps)
	{
		return await ExportNotice.AskAsync(this, maps());
	}

	private IReadOnlyList<ExportMap> CurrentExportMaps()
	{
		IReadOnlyList<OverlayElement>? layout = _overlayPresets.Count > 0 ? OverlayLayers.Drawn(ActiveElements, ActiveLayers) : null;
		return ExportMaps.Used(layout, OverlaySettingsStore.Load(), _trackOnly);
	}

	private IReadOnlyList<ExportMap> QueuedExportMaps()
	{
		OverlaySettings current = OverlaySettingsStore.Load();
		IReadOnlyList<SettingsPreset> presets = SettingsPresetStore.Load().Presets;
		var lists = new List<IReadOnlyList<ExportMap>>();
		foreach (QueueItem item in Queue.Items.Where(i => i.State == QueueState.Waiting))
		{
			try
			{
				lists.Add(ExportMaps.Used(OverlayProject.Load(item.Request.ProjectPath), current, presets));
			}
			catch (Exception)
			{
				// An unreadable project fails on its own when the queue reaches it; the notice lists the rest.
			}
		}

		return ExportMaps.Merge(lists);
	}

	private async void OnExportRightsClick(object? sender, RoutedEventArgs e)
	{
		await ExportNotice.ShowAsync(this, CurrentExportMaps());
	}
}
