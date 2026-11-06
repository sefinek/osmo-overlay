using Avalonia.Interactivity;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Mapping;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Gui;

/// <summary>The export rights notice (ExportNotice): asked once before the first render or queue run of any kind, opened on request after that.</summary>
public partial class MainWindow
{
	/// <summary>True when the render may go on: the notice was confirmed now or earlier. The one gate every way of starting an export passes.</summary>
	private async Task<bool> ConfirmExportRightsAsync(Func<IReadOnlyList<ExportMap>> maps)
	{
		if (OverlaySettingsStore.Load().ExportNoticeAccepted) return true;
		if (!await ExportNotice.AskAsync(this, maps())) return false;

		OverlaySettingsStore.Save(OverlaySettingsStore.Load() with { ExportNoticeAccepted = true });
		return true;
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
		if (await ExportNotice.ShowAsync(this, CurrentExportMaps()))
			OverlaySettingsStore.Save(OverlaySettingsStore.Load() with { ExportNoticeAccepted = true });
	}
}
