using Avalonia.Controls;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Gui;

/// <summary>
///     The size of the cards, dragged on the gaps between them (PanelSplitter): the source column and the overlay column by their
///     width, the bottom card by its height - the log's, or the expanded timeline's, which until it is dragged is as tall as its
///     layers (Settings' "Layer rows") and after it holds as many as fit. The sizes are kept in OverlaySettings, one per card, and a
///     double-click on a gap brings the default back. The preview takes what's left, so a drag stops where it would get too narrow.
/// </summary>
public partial class MainWindow
{
	private const double SourceWidthDefault = 550;
	private const double OverlayWidthDefault = 260;
	private const double LogHeightDefault = 230;
	private const double SourceWidthMin = 500;
	private const double OverlayWidthMin = 220;
	private const double PreviewWidthMin = 420;
	private const double BottomHeightMin = 120;
	private const double TopHeightMin = 300;
	// The gap between two cards - the width of a PanelSplitter.
	private const double CardGap = 10;

	private double _panelDragStart;
	// The widths asked for (saved, dragged) - the columns are as wide as the window leaves room for, and go back to these when it grows.
	private double _sourceWidth = SourceWidthDefault;
	private double _overlayWidth = OverlayWidthDefault;

	private bool BottomExpanded => TimelineCard.IsVisible;

	private void WirePanelLayout()
	{
		OverlaySettings settings = OverlaySettingsStore.Load();
		TopGrid.ColumnDefinitions[0].MinWidth = SourceWidthMin;
		TopGrid.ColumnDefinitions[4].MinWidth = OverlayWidthMin;
		_sourceWidth = settings.SourcePanelWidth ?? SourceWidthDefault;
		_overlayWidth = settings.OverlayPanelWidth ?? OverlayWidthDefault;
		FitColumns();

		TopGrid.SizeChanged += (_, _) => FitColumns();

		SourceSplitter.DragStarted += () => _panelDragStart = _sourceWidth;
		SourceSplitter.Dragged += delta => SetSourceWidth(_panelDragStart + delta);
		SourceSplitter.DragCompleted += () => SavePanelSize(s => s with { SourcePanelWidth = _sourceWidth });
		SourceSplitter.ResetRequested += () =>
		{
			SetSourceWidth(SourceWidthDefault);
			SavePanelSize(s => s with { SourcePanelWidth = null });
		};

		OverlaySplitter.DragStarted += () => _panelDragStart = _overlayWidth;
		OverlaySplitter.Dragged += delta => SetOverlayWidth(_panelDragStart - delta);
		OverlaySplitter.DragCompleted += () => SavePanelSize(s => s with { OverlayPanelWidth = _overlayWidth });
		OverlaySplitter.ResetRequested += () =>
		{
			SetOverlayWidth(OverlayWidthDefault);
			SavePanelSize(s => s with { OverlayPanelWidth = null });
		};

		BottomSplitter.DragStarted += () => _panelDragStart = (BottomExpanded ? TimelineCard : LogCard).Bounds.Height;
		BottomSplitter.Dragged += delta => SetBottomHeight(_panelDragStart - delta);
		BottomSplitter.DragCompleted += () =>
		{
			double height = RootGrid.RowDefinitions[2].Height.Value;
			SavePanelSize(s => BottomExpanded ? s with { TimelineHeight = height } : s with { LogHeight = height });
		};
		BottomSplitter.ResetRequested += () =>
		{
			SavePanelSize(s => BottomExpanded ? s with { TimelineHeight = null } : s with { LogHeight = null });
			ApplyBottomHeight(BottomExpanded);
		};
	}

	private static void SavePanelSize(Func<OverlaySettings, OverlaySettings> change)
	{
		OverlaySettingsStore.Save(change(OverlaySettingsStore.Load()));
	}

	private void SetSourceWidth(double width)
	{
		_sourceWidth = Math.Max(SourceWidthMin, width);
		FitColumns();
		// A drag stops where the preview would get too narrow, it doesn't pile up past it.
		_sourceWidth = TopGrid.ColumnDefinitions[0].Width.Value;
	}

	private void SetOverlayWidth(double width)
	{
		_overlayWidth = Math.Max(OverlayWidthMin, width);
		FitColumns();
		_overlayWidth = TopGrid.ColumnDefinitions[4].Width.Value;
	}

	/// <summary>The two side columns at the widths asked for, as far as the preview keeps its minimum - the overlay column gives way first, then the source one.</summary>
	private void FitColumns()
	{
		double source = _sourceWidth, overlay = _overlayWidth;
		if (TopGrid.Bounds.Width > 0)
		{
			double room = TopGrid.Bounds.Width - 2 * CardGap - PreviewWidthMin;
			overlay = Math.Clamp(overlay, OverlayWidthMin, Math.Max(OverlayWidthMin, room - source));
			source = Math.Clamp(source, SourceWidthMin, Math.Max(SourceWidthMin, room - overlay));
		}

		TopGrid.ColumnDefinitions[0].Width = new GridLength(source);
		TopGrid.ColumnDefinitions[4].Width = new GridLength(overlay);
	}

	private void SetBottomHeight(double height)
	{
		double room = RootGrid.Bounds.Height > 0 ? RootGrid.Bounds.Height - CardGap - TopHeightMin : double.PositiveInfinity;
		RootGrid.RowDefinitions[2].Height = new GridLength(Math.Clamp(height, BottomHeightMin, Math.Max(BottomHeightMin, room)));
		if (BottomExpanded) LayerScroll.MaxHeight = double.PositiveInfinity;
	}

	/// <summary>The bottom card's height for the log or the expanded timeline: the dragged one, else the default (the log's fixed one, the timeline's as tall as its layers).</summary>
	private void ApplyBottomHeight(bool expanded)
	{
		OverlaySettings settings = OverlaySettingsStore.Load();
		double? dragged = expanded ? settings.TimelineHeight : settings.LogHeight;
		RootGrid.RowDefinitions[2].Height = dragged is { } height ? new GridLength(height) : expanded ? GridLength.Auto : new GridLength(LogHeightDefault);
		ApplyLayerRows(settings.LayerRowsVisible);
	}
}
