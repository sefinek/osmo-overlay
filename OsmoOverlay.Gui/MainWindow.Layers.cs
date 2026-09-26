using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Gui;

/// <summary>
///     The overlay's layers under the expanded timeline (LayerTracks, see LayerTimeline) - the active preset's Layers
///     (OverlayLayer), which ReplaceActiveElements keeps in step with its elements (OverlayLayers.Normalize: the layout's
///     order is the draw order, grouped by layer). Timing dragged on a clip writes the same OverlayElement fields the Timing
///     section of a widget's settings does; mute/solo reach the preview through ShowLayout and the render through
///     OverlayLayers.Drawn. Tracks are rebuilt whenever the preset changes (ReplaceActiveElements, RefreshWidgetList) or
///     the cuts do (RefreshCutViews); a preset without Layers gets them in RefreshWidgetList.
/// </summary>
public partial class MainWindow
{
	private OverlayPreset? ActivePreset => _overlayPresets.FirstOrDefault(p => p.Id == _activePresetId);
	private IReadOnlyList<OverlayLayer> ActiveLayers => ActivePreset?.Layers ?? [];

	private void WireLayers()
	{
		TimelineTracksGrid.ColumnDefinitions[0].Width = new GridLength(LayerTimeline.HeaderWidth);
		LayerScroll.MaxHeight = LayerTimeline.VisibleTracks * LayerTimeline.RowHeight;
		LayerTracks.Source = ExpandedTimeline;
		LayerTracks.TimingEdited += OnLayerTimingEdited;
		LayerTracks.ClipClicked += OnLayerClipClicked;
		LayerTracks.LayersReordered += OnLayersReordered;
		LayerTracks.ClipMovedToLayer += OnClipMovedToLayer;
		LayerTracks.ClipMovedToNewLayer += OnClipMovedToNewLayer;
		LayerTracks.LayerSwitched += OnLayerSwitched;
		LayerTracks.LayerMenuRequested += ShowLayerMenu;
		LayerTracks.SeekRequested += seconds => PreviewTimeline.Value = seconds;
		LayerTracks.ScrubStarted += BeginTimelineScrub;
		LayerTracks.ScrubEnded += EndTimelineScrub;
		LayerScroll.PropertyChanged += (_, e) =>
		{
			if (e.Property == ScrollViewer.ViewportProperty || e.Property == BoundsProperty) AlignTimelineWithLayers();
		};
	}

	/// <summary>
	///     The layers' vertical scrollbar (shown once they don't fit) takes width off their right side - the timeline above and
	///     its scrollbar get the same margin, so every track runs on one time axis and the layers' ends stay reachable.
	/// </summary>
	private void AlignTimelineWithLayers()
	{
		var margin = new Thickness(0, 0, Math.Max(0, LayerScroll.Bounds.Width - LayerScroll.Viewport.Width), 0);
		if (ExpandedTimelineFrame.Margin == margin) return;

		ExpandedTimelineFrame.Margin = margin;
		TimelineScrollBar.Margin = margin;
	}

	/// <summary>The active preset as the preview draws it - muted and unsoloed layers left out.</summary>
	private void ShowLayout()
	{
		_previewPlayer.SetLayout(OverlayLayers.Drawn(ActiveElements, ActiveLayers));
	}

	private OverlayLayer? LayerOf(OverlayElement element)
	{
		var key = OverlayLayers.Key(element);
		return ActiveLayers.FirstOrDefault(l => l.Id == key);
	}

	private bool IsLayerLocked(OverlayElement element)
	{
		return LayerOf(element)?.Locked == true;
	}

	private void RefreshLayers()
	{
		if (_summary is null || ActivePreset is null)
		{
			LayerTracks.Tracks = [];
			return;
		}

		var fps = _summary.Video.Fps;
		LayerTracks.Fps = fps > 0 ? fps : 30;
		LayerTracks.OutputDurationSeconds = PlannedFrameCount() / LayerTracks.Fps;
		if (_outputTimeline is { } output)
		{
			LayerTracks.ToRecording = output.ToRecordingSeconds;
			LayerTracks.ToOutput = output.NearestOutputSeconds;
		}
		else
		{
			LayerTracks.ToRecording = seconds => seconds;
			LayerTracks.ToOutput = seconds => seconds;
		}

		LayerTracks.Editable = !IsActivePresetDefault;
		LayerTracks.SelectedId = _selectedElementId;
		NewLayerButton.IsEnabled = !IsActivePresetDefault;

		List<(OverlayElement Element, string Name)> widgets = VisibleWidgetNames();
		var anySolo = ActiveLayers.Any(l => l.Solo);
		LayerTracks.Tracks =
		[
			.. ActiveLayers.Select((layer, i) => new LayerTrack(layer.Id, layer.Name ?? $"Layer {i + 1}",
			[
				.. widgets.Where(w => OverlayLayers.Key(w.Element) == layer.Id).Select(w => new LayerClip(w.Element.Id, ClipLabel(w.Element, w.Name),
					w.Element.AppearAtSeconds, w.Element.DisappearAtSeconds, w.Element.AnimationType, w.Element.AnimationDurationSeconds,
					w.Element.OutAnimationType, w.Element.OutAnimationDurationSeconds, IsTypeSupported(w.Element.Type)))
			], layer.Muted, layer.Solo, layer.Locked, layer.Muted || (anySolo && !layer.Solo)))
		];
	}

	/// <summary>What a clip says: what the widget shows where it's its own (a text, a picked file, a statistic), else its name.</summary>
	private static string ClipLabel(OverlayElement element, string name)
	{
		return element switch
		{
			TextElement text => string.Join(" / ", OverlayElementBounds.TextLines(text.Text).Select(l => l.Trim()).Where(l => l.Length > 0))
				is { Length: > 0 } shown
				? shown
				: name,
			ImageElement { ImagePath: { } path } when !string.IsNullOrWhiteSpace(path) => Path.GetFileName(path),
			ImageElement => $"{name} - no file chosen",
			TripStatElement stat => stat.Label ?? SentenceCase(OverlayRenderer.DefaultTripStatLabel(stat.Stat)),
			ProfileChartElement chart => chart.Label ?? (chart.Series == ProfileSeries.Speed ? "Speed chart" : "Elevation chart"),
			ElapsedTimeTextElement { Label: { } label } when !string.IsNullOrWhiteSpace(label) => label,
			LabeledStatElement { Label: { } label } => label,
			_ => name
		};
	}

	/// <summary>Writes `elements` and `layers` (null: the preset's own) back to the active preset, in step - see OverlayLayers.Normalize.</summary>
	private void NormalizeActivePreset(IReadOnlyList<OverlayElement> elements, IReadOnlyList<OverlayLayer>? layers)
	{
		var index = _overlayPresets.FindIndex(p => p.Id == _activePresetId);
		if (index < 0) return;

		OverlayPreset preset = _overlayPresets[index];
		var (normalizedLayers, ordered) = OverlayLayers.Normalize(elements, layers ?? preset.Layers);
		_overlayPresets[index] = preset with { Elements = ordered, Layers = normalizedLayers };
	}

	private void ApplyLayerChange(List<OverlayElement> elements, IReadOnlyList<OverlayLayer>? layers = null)
	{
		if (IsActivePresetDefault) return;

		ReplaceActiveElements(elements, layers);
		ShowLayout();
		SaveOverlayPresets();
		RefreshWidgetList();
	}

	/// <summary>Live while dragging (the preview follows), saved - and the open settings panel refreshed - on release.</summary>
	private void OnLayerTimingEdited(LayerTiming timing, bool final)
	{
		if (IsActivePresetDefault) return;

		List<OverlayElement> elements = [.. ActiveElements];
		var index = elements.FindIndex(el => el.Id == timing.Id);
		if (index < 0) return;

		OverlayElement edited = elements[index] = elements[index] with
		{
			AppearAtSeconds = timing.AppearAtSeconds,
			DisappearAtSeconds = timing.DisappearAtSeconds,
			AnimationType = timing.Animation,
			AnimationDurationSeconds = timing.AnimationDurationSeconds,
			OutAnimationType = timing.OutAnimation,
			OutAnimationDurationSeconds = timing.OutAnimationDurationSeconds
		};
		ReplaceActiveElements(elements);
		ShowLayout();
		if (!final) return;

		SaveOverlayPresets();
		if (_editingElementId == timing.Id) PopulateElementSettings(edited);
	}

	private void OnLayerClipClicked(string id)
	{
		if (ActiveElements.FirstOrDefault(e => e.Id == id) is not { } element || !IsTypeSupported(element.Type)) return;

		if (_selectedElementId != id)
		{
			_selectedElementId = id;
			RebuildAddedWidgetsList();
			RefreshSelectionHighlight();
		}

		// Not again for the same widget - its panel is already open, and filling it anew would reset a field being typed in.
		if (_editingElementId != id) OpenElementSettings(element);
	}

	/// <summary>`topToBottom` is every layer's Id, the one in front first.</summary>
	private void OnLayersReordered(IReadOnlyList<string> topToBottom)
	{
		Dictionary<string, OverlayLayer> byId = ActiveLayers.ToDictionary(l => l.Id);
		ApplyLayerChange([.. ActiveElements], [.. topToBottom.Where(byId.ContainsKey).Select(id => byId[id])]);
	}

	/// <summary>Onto another layer, in front of what's on it already.</summary>
	private void OnClipMovedToLayer(string id, string layerId)
	{
		List<OverlayElement> elements = [.. ActiveElements];
		var index = elements.FindIndex(e => e.Id == id);
		if (index < 0) return;

		OverlayElement moved = elements[index] with { LayerId = layerId };
		elements.RemoveAt(index);
		elements.Add(moved);
		ApplyLayerChange(elements);
	}

	/// <summary>A layer of its own, at `gap` counted from the top (0 = in front of every other).</summary>
	private void OnClipMovedToNewLayer(string id, int gap)
	{
		List<OverlayElement> elements = [.. ActiveElements];
		var index = elements.FindIndex(e => e.Id == id);
		if (index < 0) return;

		OverlayLayer layer = NewLayer();
		List<OverlayLayer> layers = [.. ActiveLayers];
		layers.Insert(Math.Clamp(gap, 0, layers.Count), layer);
		elements[index] = elements[index] with { LayerId = layer.Id };
		ApplyLayerChange(elements, layers);
	}

	/// <summary>Made by hand, so it stays while empty; a fresh Id, never a widget's (that keys the widget's own layer).</summary>
	private static OverlayLayer NewLayer()
	{
		return new OverlayLayer(Guid.NewGuid().ToString("N")) { KeepWhenEmpty = true };
	}

	/// <summary>"+ New layer" above the layers: an empty one on top, to drag widgets onto.</summary>
	private void OnNewLayerClick(object? sender, RoutedEventArgs e)
	{
		ApplyLayerChange([.. ActiveElements], [NewLayer(), .. ActiveLayers]);
	}

	private void OnLayerSwitched(string layerId, LayerSwitch which)
	{
		UpdateLayer(layerId, layer => which switch
		{
			LayerSwitch.Mute => layer with { Muted = !layer.Muted },
			LayerSwitch.Solo => layer with { Solo = !layer.Solo },
			_ => layer with { Locked = !layer.Locked }
		});
		RefreshSelectionHighlight();
	}

	private void UpdateLayer(string layerId, Func<OverlayLayer, OverlayLayer> update)
	{
		ApplyLayerChange([.. ActiveElements], [.. ActiveLayers.Select(l => l.Id == layerId ? update(l) : l)]);
	}

	private void ShowLayerMenu(string layerId)
	{
		if (ActiveLayers.FirstOrDefault(l => l.Id == layerId) is not { } layer) return;

		var rename = new MenuItem { Header = "Rename..." };
		rename.Click += (_, _) => ShowLayerRename(layer);
		var delete = new MenuItem { Header = "Delete layer" };
		delete.Click += async (_, _) => await DeleteLayerAsync(layer);

		var menu = new ContextMenu { ItemsSource = new[] { rename, delete } };
		menu.Open(LayerTracks);
	}

	/// <summary>An empty name goes back to the automatic "Layer n".</summary>
	private void ShowLayerRename(OverlayLayer layer)
	{
		var box = new TextBox { Text = layer.Name ?? "", Width = 200, PlaceholderText = "Layer name" };
		var flyout = new Flyout { Content = box };
		var committed = false;

		void Commit()
		{
			if (committed) return;

			committed = true;
			var name = string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim();
			if (name != layer.Name) UpdateLayer(layer.Id, l => l with { Name = name });
		}

		box.KeyDown += (_, e) =>
		{
			if (e.Key == Key.Enter)
			{
				Commit();
				flyout.Hide();
			}
			else if (e.Key == Key.Escape)
			{
				committed = true;
				flyout.Hide();
			}
		};
		flyout.Closed += (_, _) => Commit();
		flyout.Opened += (_, _) =>
		{
			box.Focus();
			box.SelectAll();
		};
		flyout.ShowAt(LayerTracks, true);
	}

	/// <summary>
	///     With the widgets on it - asked first then. Hidden elements (the palette's templates) aren't on any layer, so they
	///     stay even if one still names it.
	/// </summary>
	private async Task DeleteLayerAsync(OverlayLayer layer)
	{
		if (IsActivePresetDefault) return;

		bool OnLayer(OverlayElement e) => e.Visible && OverlayLayers.Key(e) == layer.Id;

		var count = ActiveElements.Count(OnLayer);
		if (count > 0 && !await ConfirmDialog.AskAsync(this, "Delete layer",
			    $"Delete this layer and the {count} widget{(count == 1 ? "" : "s")} on it?", "Delete", DialogKind.Danger))
			return;

		List<OverlayElement> elements = [.. ActiveElements.Where(e => !OnLayer(e))];
		if (_selectedElementId is { } selected && elements.All(e => e.Id != selected)) _selectedElementId = null;
		if (_editingElementId is { } editing && elements.All(e => e.Id != editing)) CloseElementSettings();
		ApplyLayerChange(elements, [.. ActiveLayers.Where(l => l.Id != layer.Id)]);
	}
}
