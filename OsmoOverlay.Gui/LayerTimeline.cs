using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Gui;

/// <summary>
///     One widget on a layer: what it shows (Label) and when it's on screen, in output seconds - OverlayElement's own timing,
///     the way out as stored (null: none, see ElementAnimation).
/// </summary>
public sealed record LayerClip(
	string Id,
	string Label,
	double? AppearAtSeconds,
	double? DisappearAtSeconds,
	OverlayAnimationType Animation,
	double AnimationDurationSeconds,
	OverlayAnimationType? OutAnimation,
	double? OutAnimationDurationSeconds,
	bool Available)
{
	public OverlayAnimationType ResolvedOut => ElementAnimation.OutType(OutAnimation);
	public double ResolvedOutSeconds => ElementAnimation.OutDuration(OutAnimationDurationSeconds);
}

/// <summary>
///     A layer (OverlayLayer): its widgets in draw order (the last drawn in front). Silenced: not drawn right now - muted, or
///     another layer is soloed.
/// </summary>
public sealed record LayerTrack(string Key, string Name, IReadOnlyList<LayerClip> Clips, bool Muted, bool Solo, bool Locked, bool Silenced);

public enum LayerSwitch
{
	Mute,
	Solo,
	Lock
}

public sealed record LayerTiming(
	string Id,
	double? AppearAtSeconds,
	double? DisappearAtSeconds,
	OverlayAnimationType Animation,
	double AnimationDurationSeconds,
	OverlayAnimationType? OutAnimation,
	double? OutAnimationDurationSeconds);

/// <summary>
///     The widgets on layers under the expanded timeline, NLE style: tracks top to bottom (the top one drawn in front, so
///     Tracks run the reverse of the layout's draw order), each holding one or more clips - a widget each, for when it's on
///     screen. A clip's body moves it in time, its edges set appear/disappear, the handles at its top corners the
///     animation's length (turning a widget without one to a fade). A clip dragged onto another track's middle joins that
///     layer, onto the line between two tracks gets a layer of its own there; a track's header dragged up or down moves the
///     whole layer. Its M/S/L switches mute, solo and lock it (a locked layer's clips don't move, nothing is dropped on it),
///     a right-click asks for its menu (LayerMenuRequested). Times snap to whole frames, and within SnapPixels to the
///     playhead, the other clips' edges, the cuts and the ends. Drawn on the expanded timeline's own mapping (Source - its
///     zoom, scroll and playhead) right of a HeaderWidth column. Widgets are timed on the output (after cuts), the timeline
///     shows the recording: ToRecording and ToOutput convert between the two. The playhead is a visual of its own
///     (PlayheadLine), so playback redraws that one line, not every track.
/// </summary>
public sealed class LayerTimeline : Control
{
	public const double HeaderWidth = 150;
	public const double RowHeight = 26;
	// How many tracks show before the list scrolls (MainWindow.WireLayers sizes the ScrollViewer around this).
	public const int VisibleTracks = 5;

	private const double ClipInset = 3;
	private const double EdgeGrabPixels = 5;
	private const double FadeHandleSize = 7;
	private const double SnapPixels = 6;
	// How near a track's top or bottom edge a dragged clip counts as dropped between tracks (a new layer), as a share of the row.
	private const double InsertZone = 0.25;
	private const int MaxCachedTexts = 256;
	private const double SwitchWidth = 18;
	private const double SwitchHeight = 16;
	private const double SwitchGap = 3;

	private static readonly IBrush HeaderBrush = Palette.Control;
	private static readonly IBrush SelectedHeaderBrush = Palette.Tint(Palette.Accent, 0.22);
	private static readonly IBrush TrackBrush = Palette.SubtleFill;
	private static readonly IBrush DropTrackBrush = Palette.Tint(Palette.Accent, 0.14);
	private static readonly IBrush ClipBrush = Palette.Tint(Palette.Accent, 0.38);
	private static readonly IBrush SelectedClipBrush = Palette.Tint(Palette.Accent, 0.62);
	private static readonly IBrush UnavailableClipBrush = Palette.Tint(Palette.TextMuted, 0.22);
	private static readonly IPen ClipBorder = new Pen(Palette.Accent);
	private static readonly IPen SelectedClipBorder = new Pen(Palette.TextPrimary, 1.5);
	private static readonly IBrush FadeShade = Palette.Tint(Palette.SurfaceSunken, 0.55);
	private static readonly IPen FadeLine = new Pen(Palette.TextPrimary);
	private static readonly IBrush FadeHandleBrush = Palette.TextPrimary;
	private static readonly IBrush CutFill = Palette.Tint(Palette.SurfaceSunken, 0.6);
	private static readonly IBrush PastEndFill = Palette.Tint(Palette.SurfaceSunken, 0.75);
	private static readonly IPen CutBorder = new Pen(Palette.Tint(Palette.Danger, 0.9));
	private static readonly IPen PlayheadPen = new Pen(Palette.TextPrimary, 1.5);
	private static readonly IPen RowDivider = new Pen(Palette.Stroke);
	private static readonly IPen InsertPen = new Pen(Palette.Accent, 2);
	private static readonly IBrush TextBrush = Palette.TextPrimary;
	private static readonly IBrush MutedTextBrush = Palette.TextMuted;
	private static readonly IBrush LabelBackground = Palette.SurfaceSunken;
	private static readonly IBrush SwitchOffBrush = Palette.SurfaceSunken;
	private static readonly IBrush MuteOnBrush = Palette.Warning;
	private static readonly IBrush SoloOnBrush = Palette.Accent;
	private static readonly IBrush LockOnBrush = Palette.TextMuted;
	private static readonly IBrush SwitchOnText = Palette.SurfaceSunken;
	private static readonly Typeface LabelTypeface = new(FontFamily.Default);
	private static readonly Cursor ResizeCursor = new(StandardCursorType.SizeWestEast);
	private static readonly Cursor MoveCursor = new(StandardCursorType.SizeAll);
	private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

	// Redrawn with every playhead move while playing - the labels' layouts are kept rather than made again each frame.
	private readonly Dictionary<(string Text, IBrush Brush, int MaxWidth), FormattedText> _texts = [];
	private readonly Dictionary<(string Letter, bool On), FormattedText> _switchTexts = [];
	private readonly PlayheadLine _playhead;

	private PreviewTimeline? _source;
	private IReadOnlyList<LayerTrack> _tracks = [];
	private string? _selectedId;

	private Zone _drag;
	private LayerClip? _dragClip;
	private int _dragTrack;
	private double _grabOffset;
	// A header dragged: where the layer goes (a gap between tracks, 0 = above the first).
	private int _insertIndex;
	// A clip dragged off its own track: the layer it joins, or the gap it gets a new layer in.
	private Drop _drop;
	private bool _leftOwnTrack;
	private bool _scrubbing;
	private (Point At, string Text)? _dragLabel;

	static LayerTimeline()
	{
		FocusableProperty.OverrideDefaultValue<LayerTimeline>(false);
	}

	public LayerTimeline()
	{
		ClipToBounds = true;
		_playhead = new PlayheadLine(this);
		VisualChildren.Add(_playhead);
		ToolTip.SetTip(this, "Drag a clip to move it in time, its edges to set when the widget appears and disappears, the handles at its " +
		                     "top corners for the fade · Drag a clip onto another layer to put it there, between two layers for a layer " +
		                     "of its own · Drag a layer's name up or down: draw order (top is in front) · M/S/L: mute, solo, lock · " +
		                     "Right-click a layer's name: rename, delete · Click: select and open its settings · " +
		                     "Wheel: scroll the layers · Ctrl+wheel: zoom · Shift+wheel: scroll in time");
	}

	/// <summary>A clip dragged: the widget's new timing - `final` on release (save it), not while dragging (preview only).</summary>
	public event Action<LayerTiming, bool>? TimingEdited;

	/// <summary>A widget clicked - it's selected and its settings open.</summary>
	public event Action<string>? ClipClicked;

	/// <summary>Layers reordered - every track's Key, top to bottom.</summary>
	public event Action<IReadOnlyList<string>>? LayersReordered;

	/// <summary>A widget dropped onto another layer: its id and that layer's Key.</summary>
	public event Action<string, string>? ClipMovedToLayer;

	/// <summary>A widget dropped between layers: its id and the gap (0 = above the first track) its new layer goes in.</summary>
	public event Action<string, int>? ClipMovedToNewLayer;

	/// <summary>A layer's M/S/L switch clicked: its Key and which.</summary>
	public event Action<string, LayerSwitch>? LayerSwitched;

	/// <summary>A layer's header right-clicked: its Key - the caller shows the menu (rename, delete).</summary>
	public event Action<string>? LayerMenuRequested;

	/// <summary>A click or drag on a track's empty part or on the playhead: the recording time to move the playhead to.</summary>
	public event Action<double>? SeekRequested;

	/// <summary>Moving the playhead from the layers began / ended - like the timeline's own scrub, playback pauses meanwhile.</summary>
	public event Action? ScrubStarted;
	public event Action? ScrubEnded;

	public PreviewTimeline? Source
	{
		get => _source;
		set
		{
			if (_source is not null)
			{
				_source.ViewChanged -= Redraw;
				_source.PropertyChanged -= OnSourcePropertyChanged;
			}

			_source = value;
			if (_source is not null)
			{
				_source.ViewChanged += Redraw;
				_source.PropertyChanged += OnSourcePropertyChanged;
			}

			Redraw();
		}
	}

	/// <summary>Top to bottom - the layer in front first.</summary>
	public IReadOnlyList<LayerTrack> Tracks
	{
		get => _tracks;
		set
		{
			var countChanged = value.Count != _tracks.Count;
			_tracks = value;
			if (countChanged) InvalidateMeasure();
			Redraw();
		}
	}

	public string? SelectedId
	{
		get => _selectedId;
		set
		{
			if (_selectedId == value) return;

			_selectedId = value;
			InvalidateVisual();
		}
	}

	/// <summary>False for the read-only built-in preset: widgets can be selected, not changed.</summary>
	public bool Editable { get; set; } = true;

	public double OutputDurationSeconds { get; set; }
	public double Fps { get; set; } = 30;
	public Func<double, double> ToRecording { get; set; } = seconds => seconds;
	public Func<double, double> ToOutput { get; set; } = seconds => seconds;

	private double HalfFrame => 0.5 / Fps;
	private double MinClipSeconds => 1 / Fps;

	private void OnSourcePropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
	{
		if (!IsEffectivelyVisible) return;

		if (e.Property == RangeBase.ValueProperty) _playhead.InvalidateVisual();
		else if (e.Property == BoundsProperty) Redraw();
	}

	/// <summary>Tracks and playhead - for what moves both (the view, the tracks); the playhead alone moves on its own.</summary>
	private void Redraw()
	{
		InvalidateVisual();
		_playhead.InvalidateVisual();
	}

	protected override Size MeasureOverride(Size availableSize)
	{
		_playhead.Measure(availableSize);
		return new Size(0, Math.Max(1, _tracks.Count) * RowHeight);
	}

	protected override Size ArrangeOverride(Size finalSize)
	{
		_playhead.Arrange(new Rect(finalSize));
		return finalSize;
	}

	private double X(double outputSeconds)
	{
		return HeaderWidth + (_source?.XOf(ToRecording(outputSeconds)) ?? 0);
	}

	private double OutputAt(double x)
	{
		return _source is null ? 0 : ToOutput(_source.SecondsAt(x - HeaderWidth));
	}

	private (double Start, double End) Span(LayerClip clip)
	{
		return (clip.AppearAtSeconds ?? 0, clip.DisappearAtSeconds ?? OutputDurationSeconds);
	}

	/// <summary>Where the way in ends and the way out starts - as ElementAnimation plays them, the way out never before the way in ends.</summary>
	private (double FadeIn, double FadeOut) FadeEnds(LayerClip clip)
	{
		var (start, end) = Span(clip);
		var fadeIn = clip.Animation == OverlayAnimationType.None ? start : Math.Min(start + clip.AnimationDurationSeconds, end);
		var fadeOut = clip.ResolvedOut == OverlayAnimationType.None ? end : Math.Max(end - clip.ResolvedOutSeconds, fadeIn);
		return (fadeIn, fadeOut);
	}

	private (double X1, double X2, double Top, double Bottom) ClipRect(LayerClip clip, int track)
	{
		var (start, end) = Span(clip);
		var x1 = X(start);
		return (x1, Math.Max(X(end), x1 + 2), track * RowHeight + ClipInset, (track + 1) * RowHeight - ClipInset);
	}

	public override void Render(DrawingContext context)
	{
		var width = Bounds.Width;
		var height = Bounds.Height;
		context.FillRectangle(TrackBrush, new Rect(HeaderWidth, 0, Math.Max(0, width - HeaderWidth), height));
		context.FillRectangle(HeaderBrush, new Rect(0, 0, HeaderWidth, height));

		if (_tracks.Count == 0)
		{
			DrawText(context, "Nothing on the overlay yet", new Point(HeaderWidth + 8, (RowHeight - 14) / 2), MutedTextBrush, width);
			return;
		}

		for (var i = 0; i < _tracks.Count; i++) DrawHeader(context, _tracks[i], i);
		if (_drop.Join is { } joined) context.FillRectangle(DropTrackBrush, new Rect(0, joined * RowHeight, width, RowHeight));

		using (context.PushClip(new Rect(HeaderWidth, 0, Math.Max(0, width - HeaderWidth), height)))
		{
			if (_source is not null && OutputDurationSeconds > 0)
			{
				// After the video's end nothing can be shown - shaded, so a clip's end reads as one.
				var end = X(OutputDurationSeconds);
				if (end < width) context.FillRectangle(PastEndFill, new Rect(end, 0, width - end, height));

				for (var i = 0; i < _tracks.Count; i++)
				{
					if (!_tracks[i].Silenced)
					{
						DrawClips(context, i);
						continue;
					}

					// A layer that isn't drawn right now (muted, or another one soloed) is shown faded.
					using (context.PushOpacity(0.4)) DrawClips(context, i);
				}

				DrawCuts(context, height);
			}
		}

		for (var i = 1; i < _tracks.Count; i++) context.DrawLine(RowDivider, new Point(0, i * RowHeight), new Point(width, i * RowHeight));

		int? insertLine = _drag == Zone.Header ? _insertIndex : _drop.Insert;
		if (insertLine is { } line) context.DrawLine(InsertPen, new Point(0, line * RowHeight), new Point(width, line * RowHeight));
		if (_dragLabel is { } label) DrawDragLabel(context, label.At, label.Text);
	}

	private void DrawClips(DrawingContext context, int track)
	{
		foreach (LayerClip clip in _tracks[track].Clips) DrawClip(context, clip, track);
	}

	private void DrawHeader(DrawingContext context, LayerTrack track, int index)
	{
		var top = index * RowHeight;
		if (track.Clips.Any(c => c.Id == _selectedId)) context.FillRectangle(SelectedHeaderBrush, new Rect(0, top, HeaderWidth, RowHeight));

		var nameRight = SwitchRect(index, LayerSwitch.Mute).Left - 4;
		using (context.PushClip(new Rect(0, top, nameRight, RowHeight)))
			DrawText(context, track.Name, new Point(10, top + (RowHeight - 14) / 2), track.Silenced ? MutedTextBrush : TextBrush, nameRight - 10);

		DrawSwitch(context, index, LayerSwitch.Mute, "M", track.Muted, MuteOnBrush);
		DrawSwitch(context, index, LayerSwitch.Solo, "S", track.Solo, SoloOnBrush);
		DrawSwitch(context, index, LayerSwitch.Lock, "L", track.Locked, LockOnBrush);
	}

	private Rect SwitchRect(int track, LayerSwitch which)
	{
		var right = HeaderWidth - 6 - (2 - (int)which) * (SwitchWidth + SwitchGap);
		return new Rect(right - SwitchWidth, track * RowHeight + (RowHeight - SwitchHeight) / 2, SwitchWidth, SwitchHeight);
	}

	private void DrawSwitch(DrawingContext context, int track, LayerSwitch which, string letter, bool on, IBrush onBrush)
	{
		Rect rect = SwitchRect(track, which);
		context.DrawRectangle(on ? onBrush : SwitchOffBrush, null, rect, 3, 3);
		if (!_switchTexts.TryGetValue((letter, on), out FormattedText? text))
			_switchTexts[(letter, on)] = text = new FormattedText(letter, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
				LabelTypeface, 10, on ? SwitchOnText : MutedTextBrush);
		context.DrawText(text, new Point(rect.X + (rect.Width - text.Width) / 2, rect.Y + (rect.Height - text.Height) / 2));
	}

	private LayerSwitch? SwitchAt(Point point, int track)
	{
		foreach (LayerSwitch which in Enum.GetValues<LayerSwitch>())
			if (SwitchRect(track, which).Inflate(1).Contains(point))
				return which;

		return null;
	}

	private bool CanEdit(LayerClip clip, int track)
	{
		return Editable && clip.Available && !_tracks[track].Locked;
	}

	private void DrawClip(DrawingContext context, LayerClip clip, int track)
	{
		var (x1, x2, top, bottom) = ClipRect(clip, track);
		var (fadeIn, fadeOut) = FadeEnds(clip);
		var selected = clip.Id == _selectedId;
		var rect = new Rect(x1, top, x2 - x1, bottom - top);

		context.DrawRectangle(!clip.Available ? UnavailableClipBrush : selected ? SelectedClipBrush : ClipBrush, null, rect, 3, 3);

		if (clip.Animation != OverlayAnimationType.None) DrawFade(context, new Point(x1, bottom), new Point(X(fadeIn), top), new Point(x1, top));
		if (clip.ResolvedOut != OverlayAnimationType.None) DrawFade(context, new Point(x2, bottom), new Point(X(fadeOut), top), new Point(x2, top));

		context.DrawRectangle(null, selected ? SelectedClipBorder : ClipBorder, rect.Deflate(0.5), 3, 3);

		if (CanEdit(clip, track))
		{
			const double half = FadeHandleSize / 2;
			context.FillRectangle(FadeHandleBrush, new Rect(X(fadeIn) - half, top, FadeHandleSize, FadeHandleSize / 1.4));
			context.FillRectangle(FadeHandleBrush, new Rect(X(fadeOut) - half, top, FadeHandleSize, FadeHandleSize / 1.4));
		}

		// The label stays in view while the clip's start is scrolled off to the left.
		var textX = Math.Max(x1, HeaderWidth) + 6;
		using (context.PushClip(rect.Deflate(1)))
			DrawText(context, clip.Label, new Point(textX, top + (bottom - top - 14) / 2), clip.Available ? TextBrush : MutedTextBrush, x2 - textX);
	}

	/// <summary>The part the widget is still fading through shaded, with the ramp drawn from `from` (invisible) to `to` (full).</summary>
	private static void DrawFade(DrawingContext context, Point from, Point to, Point corner)
	{
		var shade = new StreamGeometry();
		using (StreamGeometryContext geometry = shade.Open())
		{
			geometry.BeginFigure(from, true);
			geometry.LineTo(to);
			geometry.LineTo(corner);
			geometry.EndFigure(true);
		}

		context.DrawGeometry(FadeShade, null, shade);
		context.DrawLine(FadeLine, from, to);
	}

	private void DrawCuts(DrawingContext context, double height)
	{
		if (_source is null) return;

		foreach (TimeRange cut in _source.Cuts)
		{
			var x1 = HeaderWidth + _source.XOf(cut.StartSeconds);
			var x2 = Math.Max(HeaderWidth + _source.XOf(cut.EndSeconds), x1 + 3);
			var band = new Rect(x1, 0, x2 - x1, height);
			context.FillRectangle(CutFill, band);
			context.DrawLine(CutBorder, band.TopLeft, band.BottomLeft);
			context.DrawLine(CutBorder, band.TopRight, band.BottomRight);
		}
	}

	private void DrawDragLabel(DrawingContext context, Point at, string text)
	{
		var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelTypeface, 11, TextBrush);
		var x = Math.Clamp(at.X + 10, HeaderWidth, Math.Max(HeaderWidth, Bounds.Width - formatted.Width - 8));
		var y = Math.Clamp(at.Y - 22, 0, Math.Max(0, Bounds.Height - formatted.Height - 4));
		context.DrawRectangle(LabelBackground, ClipBorder, new Rect(x - 4, y - 1, formatted.Width + 8, formatted.Height + 2), 3, 3);
		context.DrawText(formatted, new Point(x, y));
	}

	private void DrawText(DrawingContext context, string text, Point at, IBrush brush, double maxWidth)
	{
		if (maxWidth <= 8) return;

		// Past the text's own width the limit changes nothing, so a clip scrolled or zoomed wider still hits the cache.
		(string, IBrush, int) key = (text, brush, (int)Math.Min(maxWidth, 400));
		if (!_texts.TryGetValue(key, out FormattedText? formatted))
		{
			if (_texts.Count >= MaxCachedTexts) _texts.Clear();
			formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelTypeface, 12, brush)
			{
				MaxTextWidth = key.Item3,
				MaxLineCount = 1,
				Trimming = TextTrimming.CharacterEllipsis
			};
			_texts[key] = formatted;
		}

		context.DrawText(formatted, at);
	}

	/// <summary>
	///     What's under a point: the track, the clip on it (the one drawn in front first, where clips overlap) and the part
	///     of it - a clip's fade handles before its edges, they sit on its top corners.
	/// </summary>
	private (int Track, LayerClip? Clip, Zone Zone) HitAt(Point point)
	{
		var track = (int)Math.Floor(point.Y / RowHeight);
		if (track < 0 || track >= _tracks.Count) return (-1, null, Zone.None);
		if (point.X < HeaderWidth) return (track, null, SwitchAt(point, track) is null ? Zone.Header : Zone.Switch);

		IReadOnlyList<LayerClip> clips = _tracks[track].Clips;
		for (var i = clips.Count - 1; i >= 0; i--)
		{
			LayerClip clip = clips[i];
			var (x1, x2, top, bottom) = ClipRect(clip, track);
			if (CanEdit(clip, track))
			{
				var (fadeIn, fadeOut) = FadeEnds(clip);
				if (point.Y <= top + FadeHandleSize)
				{
					if (Math.Abs(point.X - X(fadeIn)) <= FadeHandleSize) return (track, clip, Zone.FadeIn);
					if (Math.Abs(point.X - X(fadeOut)) <= FadeHandleSize) return (track, clip, Zone.FadeOut);
				}

				if (point.Y >= top && point.Y <= bottom)
				{
					if (Math.Abs(point.X - x1) <= EdgeGrabPixels) return (track, clip, Zone.Start);
					if (Math.Abs(point.X - x2) <= EdgeGrabPixels) return (track, clip, Zone.End);
				}
			}

			if (point.Y >= top && point.Y <= bottom && point.X >= x1 && point.X <= x2) return (track, clip, Zone.Body);
		}

		return (track, null, Zone.Empty);
	}

	protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
	{
		base.OnPointerWheelChanged(e);
		// The plain wheel scrolls the list of layers (the ScrollViewer around this); Ctrl+wheel zooms the time axis like the
		// wheel over the video/audio tracks above, Shift+wheel scrolls it sideways.
		if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;

		var x = Math.Max(e.GetPosition(this).X - HeaderWidth, 0);
		if (_source?.Wheel(x, e.Delta, e.KeyModifiers & ~KeyModifiers.Control) == true) e.Handled = true;
	}

	protected override void OnPointerPressed(PointerPressedEventArgs e)
	{
		base.OnPointerPressed(e);
		PointerPoint point = e.GetCurrentPoint(this);
		var (track, clip, zone) = HitAt(point.Position);
		if (zone == Zone.None) return;

		if (point.Properties.IsRightButtonPressed && zone is Zone.Header or Zone.Switch)
		{
			e.Handled = true;
			if (Editable) LayerMenuRequested?.Invoke(_tracks[track].Key);
			return;
		}

		if (!point.Properties.IsLeftButtonPressed) return;

		e.Handled = true;
		if (zone == Zone.Switch)
		{
			if (Editable && SwitchAt(point.Position, track) is { } which) LayerSwitched?.Invoke(_tracks[track].Key, which);
			return;
		}

		if (zone == Zone.Empty || (zone == Zone.Body && IsOnPlayhead(point.Position.X)))
		{
			_scrubbing = true;
			e.Pointer.Capture(this);
			ScrubStarted?.Invoke();
			Seek(point.Position.X);
			return;
		}

		// A layer with one widget is that widget - its name selects it too.
		if ((clip ?? (_tracks[track].Clips.Count == 1 ? _tracks[track].Clips[0] : null)) is { } clicked) ClipClicked?.Invoke(clicked.Id);
		if (e.ClickCount > 1 || !Editable || (clip is not null && !CanEdit(clip, track) && zone != Zone.Header)) return;

		_drag = zone;
		_dragClip = clip;
		_dragTrack = track;
		_insertIndex = track;
		_drop = default;
		_leftOwnTrack = false;
		if (clip is not null) _grabOffset = OutputAt(point.Position.X) - Span(clip).Start;
		e.Pointer.Capture(this);
	}

	protected override void OnPointerMoved(PointerEventArgs e)
	{
		base.OnPointerMoved(e);
		Point point = e.GetPosition(this);

		if (_scrubbing)
		{
			Seek(point.X);
			return;
		}

		if (_drag == Zone.None)
		{
			var (hoveredTrack, hoveredClip, hovered) = HitAt(point);
			Cursor = hovered switch
			{
				Zone.Body or Zone.Empty when IsOnPlayhead(point.X) => ResizeCursor,
				Zone.Start or Zone.End => ResizeCursor,
				Zone.FadeIn or Zone.FadeOut or Zone.Switch => HandCursor,
				// A clip that can't move (locked layer, read-only preset, no data) is still clicked to select it.
				Zone.Body => hoveredClip is not null && CanEdit(hoveredClip, hoveredTrack) ? MoveCursor : HandCursor,
				Zone.Header => Editable ? MoveCursor : null,
				_ => null
			};
			return;
		}

		if (_drag == Zone.Header)
		{
			_insertIndex = Math.Clamp((int)Math.Round(point.Y / RowHeight), 0, _tracks.Count);
			InvalidateVisual();
			return;
		}

		if (_dragClip is not { } clip) return;

		if (_drag == Zone.Body) _drop = DropAt(point.Y);

		LayerTiming timing = Drag(clip, OutputAt(point.X));
		_dragLabel = (point, DragLabel(timing));
		TimingEdited?.Invoke(timing, false);
		InvalidateVisual();
	}

	/// <summary>
	///     Where a clip dragged to height y would go: nowhere until it leaves its own track (so a sideways drag can't slip onto
	///     another layer), then a track's middle joins that layer and the band around a line between tracks makes a new one.
	/// </summary>
	private Drop DropAt(double y)
	{
		var track = (int)Math.Floor(y / RowHeight);
		if (track != _dragTrack) _leftOwnTrack = true;
		if (!_leftOwnTrack) return default;

		if (track < 0) return new Drop(null, 0);
		if (track >= _tracks.Count) return new Drop(null, _tracks.Count);

		var within = (y - track * RowHeight) / RowHeight;
		int? insert = within < InsertZone ? track : within > 1 - InsertZone ? track + 1 : null;
		if (insert is { } gap)
		{
			// Its own layer, if it's alone on it, already sits in the gaps right above and below.
			var alone = _tracks[_dragTrack].Clips.Count == 1;
			return alone && (gap == _dragTrack || gap == _dragTrack + 1) ? default : new Drop(null, gap);
		}

		return track == _dragTrack || _tracks[track].Locked ? default : new Drop(track, null);
	}

	protected override void OnPointerReleased(PointerReleasedEventArgs e)
	{
		base.OnPointerReleased(e);
		EndDrag(e.GetPosition(this), true);
		e.Pointer.Capture(null);
	}

	protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
	{
		base.OnPointerCaptureLost(e);
		EndDrag(null, false);
	}

	/// <param name="commit">False when the capture was lost - what was dragged goes back to where it was.</param>
	private void EndDrag(Point? at, bool commit)
	{
		if (_scrubbing)
		{
			_scrubbing = false;
			ScrubEnded?.Invoke();
		}

		Zone drag = _drag;
		LayerClip? clip = _dragClip;
		Drop drop = _drop;
		// Before _drag is cleared - Drag() goes by it.
		LayerTiming? dropped = commit && at is { } point && clip is not null && drag is not (Zone.None or Zone.Header)
			? Drag(clip, OutputAt(point.X))
			: null;
		_drag = Zone.None;
		_dragClip = null;
		_drop = default;
		_dragLabel = null;
		InvalidateVisual();

		if (drag == Zone.Header)
		{
			if (commit) MoveLayer();
			return;
		}

		if (drag == Zone.None || clip is null) return;

		TimingEdited?.Invoke(dropped ?? Timing(clip), true);
		if (!commit) return;

		if (drop.Join is { } track && track < _tracks.Count) ClipMovedToLayer?.Invoke(clip.Id, _tracks[track].Key);
		else if (drop.Insert is { } gap) ClipMovedToNewLayer?.Invoke(clip.Id, gap);
	}

	/// <summary>Moves the dragged header's layer to _insertIndex - nothing when that's where it already is.</summary>
	private void MoveLayer()
	{
		if (_dragTrack >= _tracks.Count || _insertIndex == _dragTrack || _insertIndex == _dragTrack + 1) return;

		List<string> order = [.. _tracks.Select(t => t.Key)];
		var key = order[_dragTrack];
		order.RemoveAt(_dragTrack);
		order.Insert(_insertIndex > _dragTrack ? _insertIndex - 1 : _insertIndex, key);
		LayersReordered?.Invoke(order);
	}

	/// <summary>The playhead's line can be grabbed over a clip too - otherwise a track full of clips leaves nothing to grab it by.</summary>
	private bool IsOnPlayhead(double x)
	{
		return _source is not null && x >= HeaderWidth && Math.Abs(HeaderWidth + _source.XOf(_source.Value) - x) <= EdgeGrabPixels;
	}

	private void Seek(double x)
	{
		if (_source is not null) SeekRequested?.Invoke(_source.SecondsAt(x - HeaderWidth));
	}

	private static LayerTiming Timing(LayerClip clip)
	{
		return new LayerTiming(clip.Id, clip.AppearAtSeconds, clip.DisappearAtSeconds, clip.Animation, clip.AnimationDurationSeconds,
			clip.OutAnimation, clip.OutAnimationDurationSeconds);
	}

	/// <summary>`clip` (as it was when the drag began) with the dragged part at output time `at`.</summary>
	private LayerTiming Drag(LayerClip clip, double at)
	{
		var (start, end) = Span(clip);
		var length = end - start;
		double? appear = clip.AppearAtSeconds, disappear = clip.DisappearAtSeconds;
		OverlayAnimationType animation = clip.Animation;
		var inLength = clip.AnimationDurationSeconds;
		OverlayAnimationType? outAnimation = clip.OutAnimation;
		var outLength = clip.OutAnimationDurationSeconds;

		switch (_drag)
		{
			case Zone.Start:
				appear = OrStart(Math.Clamp(Snap(at, clip.Id), 0, end - MinClipSeconds));
				break;
			case Zone.End:
				disappear = OrEnd(Math.Clamp(Snap(at, clip.Id), start + MinClipSeconds, OutputDurationSeconds));
				break;
			case Zone.Body when clip.DisappearAtSeconds is null:
				// A clip running to the end moves its start only - the end stays "never".
				appear = OrStart(Math.Clamp(Snap(at - _grabOffset, clip.Id), 0, OutputDurationSeconds - MinClipSeconds));
				break;
			case Zone.Body:
			{
				// Either edge can catch a snap target - the one closer to its target wins; neither: whole frames.
				var dragged = at - _grabOffset;
				var newStart = Math.Round(dragged * Fps) / Fps;
				var startTarget = NearestTarget(dragged, clip.Id);
				var endTarget = NearestTarget(dragged + length, clip.Id);
				if (startTarget is { } s && (endTarget is not { } e || s.Distance <= e.Distance)) newStart = s.Seconds;
				else if (endTarget is { } e2) newStart = e2.Seconds - length;
				newStart = Math.Clamp(newStart, 0, Math.Max(0, OutputDurationSeconds - length));
				appear = OrStart(newStart);
				disappear = newStart + length;
				break;
			}
			case Zone.FadeIn:
			{
				// Up to where the way out starts - the two never overlap.
				var room = clip.ResolvedOut == OverlayAnimationType.None ? length : length - clip.ResolvedOutSeconds;
				inLength = AnimationLength(Snap(at, clip.Id) - start, room);
				if (animation == OverlayAnimationType.None) animation = OverlayAnimationType.Fade;
				break;
			}
			case Zone.FadeOut:
			{
				var room = clip.Animation == OverlayAnimationType.None ? length : length - clip.AnimationDurationSeconds;
				outLength = AnimationLength(end - Snap(at, clip.Id), room);
				if (clip.ResolvedOut == OverlayAnimationType.None) outAnimation = OverlayAnimationType.Fade;
				break;
			}
		}

		return new LayerTiming(clip.Id, appear, disappear, animation, inLength, outAnimation, outLength);
	}

	private double AnimationLength(double dragged, double room)
	{
		const double min = OverlayRenderer.AnimationDurationSecondsMin;
		return Math.Clamp(Math.Round(dragged * Fps) / Fps, min, Math.Max(min, Math.Min(OverlayRenderer.AnimationDurationSecondsMax, room)));
	}

	private double? OrStart(double seconds)
	{
		return seconds <= HalfFrame ? null : seconds;
	}

	private double? OrEnd(double seconds)
	{
		return seconds >= OutputDurationSeconds - HalfFrame ? null : seconds;
	}

	/// <summary>The nearest snap target within SnapPixels, else the nearest whole frame.</summary>
	private double Snap(double seconds, string draggedId)
	{
		return NearestTarget(seconds, draggedId)?.Seconds ?? Math.Round(seconds * Fps) / Fps;
	}

	private (double Seconds, double Distance)? NearestTarget(double seconds, string draggedId)
	{
		var x = X(seconds);
		(double, double)? best = null;
		var bestDistance = SnapPixels;
		foreach (var target in SnapTargets(draggedId))
		{
			var distance = Math.Abs(X(target) - x);
			if (distance > bestDistance) continue;

			best = (target, distance);
			bestDistance = distance;
		}

		return best;
	}

	private IEnumerable<double> SnapTargets(string draggedId)
	{
		yield return 0;
		yield return OutputDurationSeconds;
		if (_source is null) yield break;

		yield return ToOutput(_source.Value);
		foreach (TimeRange cut in _source.Cuts) yield return ToOutput(cut.EndSeconds);
		foreach (LayerClip clip in _tracks.SelectMany(t => t.Clips))
		{
			if (clip.Id == draggedId) continue;
			if (clip.AppearAtSeconds is { } appear) yield return appear;
			if (clip.DisappearAtSeconds is { } disappear) yield return disappear;
		}
	}

	private string DragLabel(LayerTiming timing)
	{
		return _drag switch
		{
			Zone.FadeIn => $"In animation {timing.AnimationDurationSeconds.ToString("0.0#", CultureInfo.InvariantCulture)} s",
			Zone.FadeOut => $"Out animation {(timing.OutAnimationDurationSeconds ?? timing.AnimationDurationSeconds).ToString("0.0#", CultureInfo.InvariantCulture)} s",
			Zone.End => $"Out {(timing.DisappearAtSeconds is { } end ? TimeText.Format(end) : "end")}",
			_ => $"In {TimeText.Format(timing.AppearAtSeconds ?? 0)}" +
			     (_drag == Zone.Body ? $" · Out {(timing.DisappearAtSeconds is { } end ? TimeText.Format(end) : "end")}" : "")
		};
	}

	/// <summary>The playhead's line over the tracks, redrawn alone as playback moves it - not hit-tested, grabbing it is IsOnPlayhead.</summary>
	private sealed class PlayheadLine : Control
	{
		private readonly LayerTimeline _owner;

		public PlayheadLine(LayerTimeline owner)
		{
			_owner = owner;
			IsHitTestVisible = false;
		}

		public override void Render(DrawingContext context)
		{
			if (_owner._source is not { } source || _owner.OutputDurationSeconds <= 0 || _owner._tracks.Count == 0) return;

			var x = Math.Round(HeaderWidth + source.XOf(source.Value)) + 0.5;
			if (x >= HeaderWidth && x <= Bounds.Width) context.DrawLine(PlayheadPen, new Point(x, 0), new Point(x, Bounds.Height));
		}
	}

	/// <summary>Join: the track whose layer the dragged clip joins. Insert: the gap its own new layer goes in.</summary>
	private readonly record struct Drop(int? Join, int? Insert);

	private enum Zone
	{
		None,
		Header,
		Switch,
		Empty,
		Body,
		Start,
		End,
		FadeIn,
		FadeOut
	}
}
