using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering;
using Avalonia.Threading;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Preview;

namespace OsmoOverlay.Gui;

/// <summary>
///     The preview's timeline, in two forms of one control (MainWindow has one of each - the compact one in the
///     transport row, the expanded one in the bottom panel, see Expanded): compact, a slider-like track with the same
///     marks, and expanded, NLE style: a ruler,
///     the video as a filmstrip of thumbnails (TimelineThumbnails) and the
///     audio as a waveform per channel (AudioWaveform), with everything marked on the recording drawn across the
///     tracks on one seconds-to-pixels mapping - the parts cut out of the render (red, hatched, the stripes slowly
///     moving), the In/Out selection about to be cut (bracketed), GPS signal loss (amber, under the ruler) and the
///     playhead. Value is the playhead in recording seconds, like a Slider: a click or drag anywhere scrubs.
///     Zoom: the wheel zooms around the pointer, Shift+wheel scrolls, a double-click on the ruler fits the whole
///     recording; while playing, the view pages along with the playhead. ViewChanged reports the visible stretch
///     for the scrollbar under it.
/// </summary>
public sealed class PreviewTimeline : RangeBase, ICustomHitTest
{
	// Public for the track headers beside the expanded timeline (MainWindow.axaml).
	public const double RulerHeight = 26;
	public const double VideoTrackHeight = 64;
	public const double AudioTrackHeight = VideoTrackHeight;
	public const double TrackGap = 2;
	private const double StripeSpacing = 7;
	private const double StripePixelsPerSecond = 8;
	// The stripes move 8 px a second - ~30 steps a second is smooth, redrawing on every display refresh (144-280 Hz,
	// both timelines, paused or not) only kept the UI thread busy next to playback.
	private static readonly TimeSpan StripeFrameInterval = TimeSpan.FromMilliseconds(33);
	// Bitmaps made from the generator's pixels (which it keeps for the whole recording): a few screens' worth, the
	// least recently drawn dropped past this - otherwise every second ever shown stayed in memory twice.
	private const int MaxThumbnailBitmaps = 240;
	private const double MinMajorTickPixels = 90;
	private const double MaxPixelsPerFrame = 24;
	private const double WheelZoomStep = 1.25;
	private const double EdgeGrabPixels = 5;
	private const double SnapPixels = 6;
	private const double CompactThumbRadius = 7;
	private const double CompactTrackHeight = 6;
	private const double CompactCutHeight = 16;
	private const double CompactSelectionHeight = 24;
	// Room after the recording's end on the expanded timeline, so the end - and whatever ends there, a cut or a widget's
	// layer - sits in view and can be grabbed rather than on the control's last pixel.
	private const double ExpandedEndPadding = 32;
	// Generated content redraws the tracks layer at most this often: after a zoom step every tile wants a new
	// thumbnail, one every ~15 ms, and a full layer redraw per thumbnail kept the UI thread busy for half a second.
	private static readonly TimeSpan ContentRefreshInterval = TimeSpan.FromMilliseconds(100);

	private static readonly double[] TickSteps = [0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600];

	private static readonly IBrush RulerBrush = Palette.Control;
	private static readonly IBrush TrackBrush = Palette.SubtleFill;
	private static readonly IBrush PlaceholderBrush = Palette.Control;
	private static readonly IPen TickPen = new Pen(Palette.TextMuted);
	private static readonly IPen MinorTickPen = new Pen(Palette.Stroke);
	private static readonly IBrush LabelBrush = Palette.TextMuted;
	private static readonly ISolidColorBrush WaveformBrush = (ISolidColorBrush)Palette.Tint(Palette.Accent, 0.85);
	private static readonly IPen WaveformCenterPen = new Pen(Palette.Tint(Palette.Accent, 0.3));
	private static readonly IBrush PlayedBrush = Palette.Accent;
	private static readonly IBrush GpsLossBrush = Palette.Warning;
	private static readonly IBrush CutFill = Palette.Tint(Palette.SurfaceSunken, 0.6);
	private static readonly IPen CutBorder = new Pen(Palette.Tint(Palette.Danger, 0.9));
	private static readonly IPen CutStripe = new Pen(Palette.Tint(Palette.Danger, 0.55), 2);
	private static readonly IBrush SelectionFill = Palette.Tint(Palette.Accent, 0.18);
	private static readonly IPen SelectionBracket = new Pen(Palette.Accent, 2);
	private static readonly IPen PlayheadPen = new Pen(Palette.TextPrimary, 1.5);
	private static readonly IBrush PlayheadBrush = Palette.TextPrimary;
	private static readonly IPen HoverPen = new Pen(Palette.Tint(Palette.TextPrimary, 0.35));
	private static readonly Typeface LabelTypeface = new(FontFamily.Default);
	private static readonly IPen SelectedCutBorder = new Pen(Palette.Danger, 2);
	private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
	private static readonly Cursor ResizeCursor = new(StandardCursorType.SizeWestEast);

	private readonly Dictionary<int, CachedThumbnail> _thumbnailBitmaps = [];
	private long _thumbnailUse;
	private readonly Stopwatch _stripeClock = Stopwatch.StartNew();
	private DispatcherTimer? _stripeTimer;
	private IReadOnlyList<TimeRange> _cuts = [];
	private IReadOnlyList<TimeRange> _gpsLoss = [];
	private TimeRange? _selection;
	private TimelineThumbnails? _thumbnails;
	private AudioWaveform? _waveform;
	private double _fps = 30;
	private double _thumbnailAspect = 16.0 / 9;
	// Zoom as a multiple of "the whole recording fits", so a resize keeps what's visible; the view's left edge in seconds.
	private double _zoom = 1;
	private bool _scrubbing;
	private bool _attached;
	private int _refreshPosted;
	private long _lastContentRefresh;
	private double _stripeOffset;
	private double? _hoverX;
	private DragMode _drag;
	private int _dragCut;
	private double _dragAnchor;
	// A cut edge being dragged is drawn here until the release applies it (CutResized).
	private TimeRange? _dragPreview;
	private int? _selectedCut;
	private bool _expanded;
	// The tracks' static part (ruler, filmstrip, waveform, GPS loss) drawn once into a bitmap and redrawn only when
	// the view or the generated content changes. Playback moves the playhead every frame; repainting the thumbnails
	// and a waveform column per pixel on each of those made the preview stutter.
	private RenderTargetBitmap? _tracksLayer;
	// Kept until the next layer is drawn, not disposed right after DrawImage took it.
	private WriteableBitmap? _waveformBitmap;
	private (double Start, double PixelsPerSecond, Size Size, double Scaling, int Version) _tracksLayerKey;
	private int _contentVersion;

	static PreviewTimeline()
	{
		AffectsRender<PreviewTimeline>(ValueProperty, MinimumProperty, MaximumProperty, IsEnabledProperty);
		FocusableProperty.OverrideDefaultValue<PreviewTimeline>(false);
	}

	public PreviewTimeline()
	{
		Cursor = HandCursor;
		ToolTip.SetTip(this, "Click or drag: move the playhead · Shift+drag: select · Drag a cut's or the selection's edge to change it · " +
		                     "Click a cut, then Delete: remove it · Up/Down: previous/next marker · Wheel: zoom · Shift+wheel: scroll · " +
		                     "Double-click the ruler: fit");
	}

	/// <summary>A drag on the timeline began / ended - Value keeps changing (ValueChanged) in between.</summary>
	public event Action? ScrubStarted;
	public event Action? ScrubEnded;

	/// <summary>Shift+drag, or an edge of the selection dragged: its start and end in seconds (end exclusive), while dragging.</summary>
	public event Action<double, double>? SelectionDragged;

	/// <summary>An edge of a cut was dragged: the cut's index in Cuts and its new start and end in seconds, on release.</summary>
	public event Action<int, double, double>? CutResized;

	/// <summary>A click: the index of the cut under it in Cuts, or null outside every cut.</summary>
	public event Action<int?>? CutClicked;

	/// <summary>The cut drawn as selected (Delete removes it) - an index into Cuts.</summary>
	public int? SelectedCut
	{
		get => _selectedCut;
		set
		{
			_selectedCut = value;
			InvalidateVisual();
		}
	}

	/// <summary>The visible stretch changed (zoom, scroll, resize) - see ViewStart/ViewLength.</summary>
	public event Action? ViewChanged;

	/// <summary>False: the compact track (whole recording, no zoom). True: ruler, filmstrip, waveform, zoom.</summary>
	public bool Expanded
	{
		get => _expanded;
		set
		{
			if (_expanded == value) return;

			_expanded = value;
			_hoverX = null;
			InvalidateMeasure();
			SetView(value ? _zoom : 1, value ? ViewStart : 0);
		}
	}

	// The compact track keeps the thumb whole at 0 and at Maximum.
	private double Inset => _expanded ? 0 : CompactThumbRadius;

	private double TimeWidth => Math.Max(0, Bounds.Width - 2 * Inset - (_expanded ? ExpandedEndPadding : 0));

	public double ViewStart { get; private set; }
	public double ViewLength => PixelsPerSecond > 0 ? TimeWidth / PixelsPerSecond : Duration;

	private double Duration => Math.Max(0, Maximum - Minimum);
	private double FitPixelsPerSecond => Duration > 0 ? TimeWidth / Duration : 0;
	public double PixelsPerSecond => FitPixelsPerSecond * _zoom;
	private double MaxZoom => FitPixelsPerSecond > 0 ? Math.Max(1, _fps * MaxPixelsPerFrame / FitPixelsPerSecond) : 1;

	public IReadOnlyList<TimeRange> Cuts
	{
		get => _cuts;
		set
		{
			_cuts = value;
			InvalidateVisual();
			UpdateStripeAnimation();
		}
	}

	public IReadOnlyList<TimeRange> GpsLoss
	{
		get => _gpsLoss;
		set
		{
			_gpsLoss = value;
			_contentVersion++;
			InvalidateVisual();
		}
	}

	public TimeRange? Selection
	{
		get => _selection;
		set
		{
			_selection = value;
			InvalidateVisual();
		}
	}

	/// <summary>The recording the tracks show - both sources are owned (and disposed) by the caller. Null clears the tracks.</summary>
	public void SetSources(TimelineThumbnails? thumbnails, AudioWaveform? waveform, double fps, double aspect)
	{
		if (_thumbnails is not null) _thumbnails.Updated -= PostRefresh;
		if (_waveform is not null) _waveform.Updated -= PostRefresh;

		foreach (CachedThumbnail cached in _thumbnailBitmaps.Values) cached.Bitmap.Dispose();
		_thumbnailBitmaps.Clear();

		_thumbnails = thumbnails;
		_waveform = waveform;
		_contentVersion++;
		_fps = fps > 0 ? fps : 30;
		_thumbnailAspect = aspect > 0 ? aspect : 16.0 / 9;
		if (_thumbnails is not null) _thumbnails.Updated += PostRefresh;
		if (_waveform is not null) _waveform.Updated += PostRefresh;

		_zoom = 1;
		ViewStart = 0;
		ViewChanged?.Invoke();
		RequestVisibleThumbnails();
		InvalidateVisual();
	}

	/// <summary>Scrolls the view so it starts at this time (the scrollbar under the timeline).</summary>
	public void ScrollTo(double start)
	{
		SetView(_zoom, start);
	}

	/// <summary>The whole control takes the pointer, not just what's drawn - a click between the marks still scrubs.</summary>
	public bool HitTest(Point point)
	{
		return new Rect(Bounds.Size).Contains(point);
	}

	protected override Size MeasureOverride(Size availableSize)
	{
		return new Size(0, _expanded ? RulerHeight + TrackGap + VideoTrackHeight + TrackGap + AudioTrackHeight : CompactSelectionHeight + 4);
	}

	protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
	{
		base.OnPropertyChanged(change);
		if (change.Property == BoundsProperty || change.Property == MaximumProperty)
		{
			SetView(_zoom, ViewStart);
		}
		else if (change.Property == ValueProperty && !_scrubbing && _expanded)
		{
			// Playing past the view's edge pages along, keeping a little of what was just played in sight.
			var x = X(Value);
			if (x > Bounds.Width - 12 || x < 0) SetView(_zoom, Value - ViewLength * 0.1);
		}
	}

	public override void Render(DrawingContext context)
	{
		if (_expanded) RenderExpanded(context);
		else RenderCompact(context);
	}

	private void RenderCompact(DrawingContext context)
	{
		var middle = Bounds.Height / 2;
		var trackWidth = Math.Max(0, Bounds.Width - 2 * Inset);
		var track = new Rect(Inset, middle - CompactTrackHeight / 2, trackWidth, CompactTrackHeight);
		using DrawingContext.PushedState opacity = context.PushOpacity(IsEffectivelyEnabled ? 1 : 0.45);

		context.DrawRectangle(Palette.StrokeStrong, null, track, CompactTrackHeight / 2, CompactTrackHeight / 2);
		// The part already played, up to the thumb - under the cuts and the selection, which stay readable on top.
		context.DrawRectangle(PlayedBrush, null, track.WithWidth(Math.Max(0, X(Value) - Inset)), CompactTrackHeight / 2, CompactTrackHeight / 2);

		foreach (TimeRange loss in _gpsLoss)
		{
			var (x1, x2) = Span(loss, 3);
			context.DrawRectangle(GpsLossBrush, null, new Rect(x1, middle + CompactTrackHeight / 2 + 3, x2 - x1, 3), 1.5, 1.5);
		}

		DrawCuts(context, middle - CompactCutHeight / 2, middle + CompactCutHeight / 2);
		if (_selection is { } selection)
			DrawSelection(context, selection, middle - CompactSelectionHeight / 2, middle + CompactSelectionHeight / 2);

		context.DrawEllipse(PlayedBrush, null, new Point(X(Value), middle), CompactThumbRadius, CompactThumbRadius);
	}

	private void RenderExpanded(DrawingContext context)
	{
		var width = Bounds.Width;
		using DrawingContext.PushedState clip = context.PushClip(new Rect(Bounds.Size));
		using DrawingContext.PushedState opacity = context.PushOpacity(IsEffectivelyEnabled ? 1 : 0.45);

		var videoTop = RulerHeight + TrackGap;
		var audioTop = videoTop + VideoTrackHeight + TrackGap;
		var bottom = audioTop + AudioTrackHeight;

		var scaling = UiScale.DeviceScaling(TopLevel.GetTopLevel(this));
		(double, double, Size, double, int) key = (ViewStart, PixelsPerSecond, Bounds.Size, scaling, _contentVersion);
		if (_tracksLayer is null || _tracksLayerKey != key)
		{
			_tracksLayer?.Dispose();
			_tracksLayer = RenderTracksLayer(scaling, width, videoTop, audioTop);
			_tracksLayerKey = key;
		}

		context.DrawImage(_tracksLayer, new Rect(Bounds.Size));
		if (Duration <= 0 || PixelsPerSecond <= 0) return;

		// Played so far, along the ruler's bottom edge.
		context.FillRectangle(PlayedBrush, new Rect(0, RulerHeight - 2, Math.Max(0, X(Value)), 2));

		DrawCuts(context, videoTop, bottom);
		if (_selection is { } selection) DrawSelection(context, selection, videoTop, bottom);

		if (_hoverX is { } hover) context.DrawLine(HoverPen, new Point(hover, 0), new Point(hover, bottom));
		DrawPlayhead(context, bottom);
	}

	private RenderTargetBitmap RenderTracksLayer(double scaling, double width, double videoTop, double audioTop)
	{
		var pixels = new PixelSize(Math.Max(1, (int)Math.Ceiling(Bounds.Width * scaling)), Math.Max(1, (int)Math.Ceiling(Bounds.Height * scaling)));
		// At 96 DPI with the display scale pushed by hand: a bitmap at 96 * scaling DPI was drawn as only its top-left
		// 1/scaling part blown up (at 125% the tracks came out 1.25x too big, the audio lane and the end cut off).
		var layer = new RenderTargetBitmap(pixels, new Vector(96, 96));
		using DrawingContext context = layer.CreateDrawingContext();
		using DrawingContext.PushedState scale = context.PushTransform(Matrix.CreateScale(scaling, scaling));

		context.FillRectangle(RulerBrush, new Rect(0, 0, width, RulerHeight));
		context.FillRectangle(TrackBrush, new Rect(0, videoTop, width, VideoTrackHeight));
		context.FillRectangle(TrackBrush, new Rect(0, audioTop, width, AudioTrackHeight));
		if (Duration <= 0 || PixelsPerSecond <= 0) return layer;

		DrawRuler(context, width);
		DrawFilmstrip(context, videoTop, width);
		DrawWaveform(context, audioTop, width, scaling);

		foreach (TimeRange loss in _gpsLoss)
		{
			var (x1, x2) = Span(loss, 3);
			context.FillRectangle(GpsLossBrush, new Rect(x1, RulerHeight - 3, x2 - x1, 3));
		}

		return layer;
	}

	private void DrawRuler(DrawingContext context, double width)
	{
		var major = TickSteps.FirstOrDefault(step => step * PixelsPerSecond >= MinMajorTickPixels, TickSteps[^1]);
		var minor = major / (major is 2 or 0.2 or 120 ? 4 : 5);
		var first = Math.Floor(ViewStart / minor) * minor;
		var last = Math.Min(Duration, ViewStart + ViewLength);

		for (var t = first; t <= last + minor / 2; t += minor)
		{
			var x = Math.Round(X(t)) + 0.5;
			var isMajor = Math.Abs(t / major - Math.Round(t / major)) < 1e-6;
			context.DrawLine(isMajor ? TickPen : MinorTickPen, new Point(x, isMajor ? 9 : 17), new Point(x, RulerHeight - 3));
			if (!isMajor) continue;

			var label = new FormattedText(FormatRulerTime(t, major), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
				LabelTypeface, 11, LabelBrush);
			context.DrawText(label, new Point(x + 3, 2));
		}
	}

	private static string FormatRulerTime(double seconds, double step)
	{
		if (step < 1) return TimeText.Format(seconds);

		TimeSpan time = TimeSpan.FromSeconds(Math.Round(seconds));
		return time.ToString(time.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss", CultureInfo.InvariantCulture);
	}

	/// <summary>Tiles on a grid fixed to the timeline (not the view), so they don't swim while scrolling; each shows the thumbnail of the second at its middle.</summary>
	private void DrawFilmstrip(DrawingContext context, double top, double width)
	{
		var tileHeight = VideoTrackHeight - 4;
		var tileWidth = Math.Round(tileHeight * _thumbnailAspect);
		var viewLeft = ViewStart * PixelsPerSecond;
		var firstTile = (int)Math.Floor(viewLeft / tileWidth);
		var endX = Math.Min(width, X(Duration));

		using DrawingContext.PushedState clip = context.PushClip(new Rect(0, top, endX, VideoTrackHeight));
		for (var tile = firstTile;; tile++)
		{
			var x = tile * tileWidth - viewLeft;
			if (x >= endX) break;

			var dest = new Rect(x + 1, top + 2, tileWidth - 2, tileHeight);
			var slot = TileSlot(tile, tileWidth);
			if ((Thumbnail(slot) ?? StandIn(slot, tileWidth)) is { } bitmap)
				context.DrawImage(bitmap, new Rect(bitmap.Size), dest);
			else
				context.FillRectangle(PlaceholderBrush, dest);
		}
	}

	/// <summary>
	///     Until a tile's own thumbnail is generated, the nearest one already generated within the tile's span - a zoom
	///     step moves every tile to another second, and a strip of placeholders flashed on each one.
	/// </summary>
	private Bitmap? StandIn(int slot, double tileWidth)
	{
		if (_thumbnails?.NearestGenerated(slot, (int)Math.Ceiling(tileWidth / PixelsPerSecond)) is not { } nearest) return null;
		return Thumbnail(nearest);
	}

	private int TileSlot(int tile, double tileWidth)
	{
		return (int)Math.Floor((tile + 0.5) * tileWidth / PixelsPerSecond);
	}

	private Bitmap? Thumbnail(int slot)
	{
		if (_thumbnails is null) return null;
		if (_thumbnailBitmaps.TryGetValue(slot, out CachedThumbnail? cached))
		{
			cached.LastUse = ++_thumbnailUse;
			return cached.Bitmap;
		}

		if (_thumbnails.TryGet(slot) is not { } bgra) return null;

		var bitmap = new WriteableBitmap(new PixelSize(_thumbnails.Width, _thumbnails.Height), new Vector(96, 96),
			PixelFormat.Bgra8888, AlphaFormat.Opaque);
		using (ILockedFramebuffer buffer = bitmap.Lock())
		{
			var rowBytes = _thumbnails.Width * 4;
			for (var y = 0; y < _thumbnails.Height; y++)
				Marshal.Copy(bgra, y * rowBytes, buffer.Address + y * buffer.RowBytes, rowBytes);
		}

		if (_thumbnailBitmaps.Count >= MaxThumbnailBitmaps) EvictThumbnailBitmaps();
		_thumbnailBitmaps[slot] = new CachedThumbnail(bitmap) { LastUse = ++_thumbnailUse };
		return bitmap;
	}

	/// <summary>Drops the least recently drawn quarter - the tiles of the layer being drawn were all used just now, so none of them.</summary>
	private void EvictThumbnailBitmaps()
	{
		foreach ((var slot, CachedThumbnail cached) in _thumbnailBitmaps.OrderBy(p => p.Value.LastUse).Take(MaxThumbnailBitmaps / 4).ToList())
		{
			cached.Bitmap.Dispose();
			_thumbnailBitmaps.Remove(slot);
		}
	}

	/// <summary>
	///     One column per device pixel, as tall as the loudest sample under it (in dB, see AudioWaveform.DisplayLevel) - one
	///     lane per channel, mirrored around its middle. Filled straight into a bitmap: the same columns as a geometry of one
	///     figure each took Skia 250-370 ms to fill on a 2000 px wide timeline, on every scroll or zoom step.
	/// </summary>
	private void DrawWaveform(DrawingContext context, double top, double width, double scaling)
	{
		if (_waveform is not { } waveform) return;

		var endX = Math.Min(width, X(Duration));
		var pixelWidth = (int)Math.Ceiling(endX * scaling);
		var pixelHeight = (int)Math.Round(AudioTrackHeight * scaling);
		if (pixelWidth <= 0 || pixelHeight <= 0) return;

		var lanes = Math.Min(2, waveform.Channels);
		var laneHeight = (double)pixelHeight / lanes;
		var pixels = new int[pixelWidth * pixelHeight];
		Color color = WaveformBrush.Color;
		var opacity = color.A / 255.0 * WaveformBrush.Opacity;
		for (var px = 0; px < pixelWidth; px++)
		{
			var from = (int)(TimeAt(px / scaling) * AudioWaveform.BucketsPerSecond);
			var to = Math.Max(from + 1, (int)(TimeAt((px + 1) / scaling) * AudioWaveform.BucketsPerSecond));
			for (var lane = 0; lane < lanes; lane++)
			{
				var middle = laneHeight * (lane + 0.5);
				var level = AudioWaveform.DisplayLevel(waveform.Peak(lane, from, to), waveform.LoudestPeak);
				var half = Math.Max(0.5 * scaling, level * (laneHeight / 2 - scaling));
				var firstRow = Math.Max(0, (int)Math.Floor(middle - half));
				var lastRow = Math.Min(pixelHeight - 1, (int)Math.Ceiling(middle + half) - 1);
				for (var y = firstRow; y <= lastRow; y++)
				{
					// The end rows only partly covered, so the edge stays as smooth as the antialiased figures were.
					var coverage = Math.Clamp(half - Math.Abs(y + 0.5 - middle) + 0.5, 0, 1);
					pixels[y * pixelWidth + px] = Premultiplied(color, opacity * coverage);
				}
			}
		}

		_waveformBitmap?.Dispose();
		_waveformBitmap = new WriteableBitmap(new PixelSize(pixelWidth, pixelHeight), new Vector(96, 96), PixelFormat.Bgra8888,
			AlphaFormat.Premul);
		using (ILockedFramebuffer buffer = _waveformBitmap.Lock())
		{
			for (var y = 0; y < pixelHeight; y++)
				Marshal.Copy(pixels, y * pixelWidth, buffer.Address + y * buffer.RowBytes, pixelWidth);
		}

		context.DrawImage(_waveformBitmap, new Rect(_waveformBitmap.Size), new Rect(0, top, pixelWidth / scaling, pixelHeight / scaling));
		for (var lane = 0; lane < lanes; lane++)
		{
			var middle = top + laneHeight * (lane + 0.5) / scaling;
			context.DrawLine(WaveformCenterPen, new Point(0, middle), new Point(endX, middle));
		}
	}

	/// <summary>A BGRA pixel of this color at this alpha (0-1), premultiplied.</summary>
	private static int Premultiplied(Color color, double alpha)
	{
		return (int)((uint)Math.Round(alpha * 255) << 24 | (uint)Math.Round(color.R * alpha) << 16 |
		             (uint)Math.Round(color.G * alpha) << 8 | (uint)Math.Round(color.B * alpha));
	}

	/// <summary>The cut being resized is drawn where its edge is dragged to; the selected one gets a stronger border.</summary>
	private void DrawCuts(DrawingContext context, double top, double bottom)
	{
		for (var i = 0; i < _cuts.Count; i++)
		{
			TimeRange cut = _drag is DragMode.CutStart or DragMode.CutEnd && i == _dragCut && _dragPreview is { } preview
				? new TimeRange(Math.Min(preview.StartSeconds, preview.EndSeconds), Math.Max(preview.StartSeconds, preview.EndSeconds))
				: _cuts[i];
			DrawCut(context, cut, top, bottom, i == _selectedCut);
		}
	}

	private void DrawCut(DrawingContext context, TimeRange cut, double top, double bottom, bool selected)
	{
		var (x1, x2) = Span(cut, 3);
		var band = new Rect(x1, top, x2 - x1, bottom - top);
		context.FillRectangle(CutFill, band);

		using (context.PushClip(band))
		{
			// Diagonal stripes (the usual "removed" hatching), shifted by the animation offset.
			for (var x = band.Left - band.Height - StripeSpacing + _stripeOffset; x < band.Right; x += StripeSpacing)
				context.DrawLine(CutStripe, new Point(x, band.Bottom), new Point(x + band.Height, band.Top));
		}

		context.DrawRectangle(null, selected ? SelectedCutBorder : CutBorder, band.Deflate(selected ? 1 : 0.5));
	}

	private void DrawSelection(DrawingContext context, TimeRange selection, double top, double bottom)
	{
		var (x1, x2) = Span(selection, 2);
		context.FillRectangle(SelectionFill, new Rect(x1, top, x2 - x1, bottom - top));

		const double tick = 6;
		foreach (var (x, direction) in new[] { (x1 + 1, 1.0), (x2 - 1, -1.0) })
		{
			context.DrawLine(SelectionBracket, new Point(x, top), new Point(x, bottom));
			context.DrawLine(SelectionBracket, new Point(x, top + 1), new Point(x + direction * tick, top + 1));
			context.DrawLine(SelectionBracket, new Point(x, bottom - 1), new Point(x + direction * tick, bottom - 1));
		}
	}

	private void DrawPlayhead(DrawingContext context, double bottom)
	{
		var x = Math.Round(X(Value)) + 0.5;
		if (x < -6 || x > Bounds.Width + 6) return;

		context.DrawLine(PlayheadPen, new Point(x, 0), new Point(x, bottom));
		var handle = new StreamGeometry();
		using (StreamGeometryContext stream = handle.Open())
		{
			stream.BeginFigure(new Point(x - 6, 0));
			stream.LineTo(new Point(x + 6, 0));
			stream.LineTo(new Point(x + 6, 6));
			stream.LineTo(new Point(x, 12));
			stream.LineTo(new Point(x - 6, 6));
			stream.EndFigure(true);
		}

		context.DrawGeometry(PlayheadBrush, null, handle);
	}

	private double X(double seconds)
	{
		return Inset + (seconds - Minimum - ViewStart) * PixelsPerSecond;
	}

	private double TimeAt(double x)
	{
		return PixelsPerSecond > 0 ? ViewStart + (x - Inset) / PixelsPerSecond : 0;
	}

	/// <summary>A range in pixels, at least minWidth wide so a one-frame cut still shows.</summary>
	private (double X1, double X2) Span(TimeRange range, double minWidth)
	{
		var x1 = X(range.StartSeconds);
		return (x1, Math.Max(X(range.EndSeconds), x1 + minWidth));
	}

	private double ValueAt(double x)
	{
		return Math.Clamp(Minimum + TimeAt(x), Minimum, Maximum);
	}

	/// <summary>Clamps zoom and scroll to the recording, then redraws and asks for the thumbnails now in view.</summary>
	private void SetView(double zoom, double start)
	{
		_zoom = Math.Clamp(zoom, 1, MaxZoom);
		ViewStart = Math.Clamp(start, 0, Math.Max(0, Duration - ViewLength));
		RequestVisibleThumbnails();
		InvalidateVisual();
		ViewChanged?.Invoke();
	}

	private void RequestVisibleThumbnails()
	{
		if (!_expanded || _thumbnails is null || PixelsPerSecond <= 0) return;

		var tileWidth = Math.Round((VideoTrackHeight - 4) * _thumbnailAspect);
		var viewLeft = ViewStart * PixelsPerSecond;
		var firstTile = (int)Math.Floor(viewLeft / tileWidth);
		var lastTile = (int)Math.Ceiling((viewLeft + Bounds.Width) / tileWidth);
		_thumbnails.Request(Enumerable.Range(firstTile, Math.Max(0, lastTile - firstTile + 1)).Select(t => TileSlot(t, tileWidth)));
	}

	/// <summary>The generators report from their own threads; reports collapse into one redraw per ContentRefreshInterval.</summary>
	private void PostRefresh()
	{
		if (Interlocked.Exchange(ref _refreshPosted, 1) == 1) return;

		Dispatcher.UIThread.Post(() =>
		{
			TimeSpan wait = ContentRefreshInterval - TimeSpan.FromMilliseconds(Environment.TickCount64 - _lastContentRefresh);
			if (wait <= TimeSpan.Zero) RefreshContent();
			else DispatcherTimer.RunOnce(RefreshContent, wait, DispatcherPriority.Background);
		}, DispatcherPriority.Background);
	}

	private void RefreshContent()
	{
		_lastContentRefresh = Environment.TickCount64;
		Volatile.Write(ref _refreshPosted, 0);
		_contentVersion++;
		InvalidateVisual();
	}

	protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
	{
		base.OnPointerWheelChanged(e);
		if (Wheel(e.GetPosition(this).X, e.Delta, e.KeyModifiers)) e.Handled = true;
	}

	/// <summary>
	///     Wheel zoom around `x` (this control's coordinates), Shift+wheel or a sideways wheel scrolls - also for the layer
	///     tracks under it (LayerTimeline), which share this view. False when there's nothing to zoom.
	/// </summary>
	public bool Wheel(double x, Vector delta, KeyModifiers modifiers)
	{
		if (!_expanded || Duration <= 0) return false;

		if (modifiers.HasFlag(KeyModifiers.Shift) || Math.Abs(delta.X) > Math.Abs(delta.Y))
		{
			var step = Math.Abs(delta.X) > Math.Abs(delta.Y) ? delta.X : delta.Y;
			SetView(_zoom, ViewStart - step * ViewLength * 0.1);
		}
		else
		{
			// Zooms around the time under the pointer, which stays where it is.
			var anchor = TimeAt(x);
			var zoom = Math.Clamp(_zoom * Math.Pow(WheelZoomStep, delta.Y), 1, MaxZoom);
			SetView(zoom, anchor - x / (FitPixelsPerSecond * zoom));
		}

		return true;
	}

	/// <summary>A recording time's x in this control - the layer tracks draw on the same mapping.</summary>
	public double XOf(double seconds)
	{
		return X(seconds);
	}

	/// <summary>The recording time at x in this control, clamped to the recording.</summary>
	public double SecondsAt(double x)
	{
		return ValueAt(x);
	}

	protected override void OnPointerPressed(PointerPressedEventArgs e)
	{
		base.OnPointerPressed(e);
		PointerPoint point = e.GetCurrentPoint(this);
		if (!point.Properties.IsLeftButtonPressed || Duration <= 0) return;

		var x = point.Position.X;
		if (_expanded && e.ClickCount == 2 && point.Position.Y < RulerHeight)
		{
			SetView(1, 0);
			e.Handled = true;
			return;
		}

		e.Pointer.Capture(this);
		e.Handled = true;
		if (EdgeAt(x) is { } edge)
		{
			(_drag, _dragCut) = edge;
			return;
		}

		if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
		{
			_drag = DragMode.NewSelection;
			_dragAnchor = Snap(ValueAt(x));
			return;
		}

		_drag = DragMode.Scrub;
		_scrubbing = true;
		ScrubStarted?.Invoke();
		Value = ValueAt(x);
		CutClicked?.Invoke(CutIndexAt(Value));
	}

	protected override void OnPointerMoved(PointerEventArgs e)
	{
		base.OnPointerMoved(e);
		var x = e.GetPosition(this).X;
		var time = Snap(ValueAt(x));
		switch (_drag)
		{
			case DragMode.Scrub:
				Value = ValueAt(x);
				break;
			case DragMode.NewSelection:
				SelectionDragged?.Invoke(Math.Min(_dragAnchor, time), Math.Max(_dragAnchor, time));
				break;
			case DragMode.SelectionStart when _selection is { } selection:
				SelectionDragged?.Invoke(Math.Min(time, selection.EndSeconds), Math.Max(time, selection.EndSeconds));
				break;
			case DragMode.SelectionEnd when _selection is { } selection:
				SelectionDragged?.Invoke(Math.Min(time, selection.StartSeconds), Math.Max(time, selection.StartSeconds));
				break;
			case DragMode.CutStart or DragMode.CutEnd when _dragCut < _cuts.Count:
				TimeRange cut = _cuts[_dragCut];
				_dragPreview = _drag == DragMode.CutStart ? cut with { StartSeconds = time } : cut with { EndSeconds = time };
				InvalidateVisual();
				break;
			default:
				Cursor = EdgeAt(x) is null ? HandCursor : ResizeCursor;
				break;
		}

		if (!_expanded) return;

		_hoverX = x;
		InvalidateVisual();
	}

	protected override void OnPointerExited(PointerEventArgs e)
	{
		base.OnPointerExited(e);
		_hoverX = null;
		InvalidateVisual();
	}

	protected override void OnPointerReleased(PointerReleasedEventArgs e)
	{
		base.OnPointerReleased(e);
		if (_drag == DragMode.None) return;

		// Ended before releasing the capture - OnPointerCaptureLost must not end the drag a second time.
		EndDrag(true);
		e.Pointer.Capture(null);
	}

	protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
	{
		base.OnPointerCaptureLost(e);
		EndDrag(false);
	}

	/// <param name="commit">False when the capture was lost - a cut edge dragged then snaps back instead of applying.</param>
	private void EndDrag(bool commit)
	{
		DragMode drag = _drag;
		_drag = DragMode.None;
		if (drag == DragMode.Scrub)
		{
			_scrubbing = false;
			ScrubEnded?.Invoke();
		}
		else if (drag is DragMode.CutStart or DragMode.CutEnd && _dragPreview is { } resized)
		{
			_dragPreview = null;
			if (commit) CutResized?.Invoke(_dragCut, Math.Min(resized.StartSeconds, resized.EndSeconds), Math.Max(resized.StartSeconds, resized.EndSeconds));
			InvalidateVisual();
		}
	}

	/// <summary>A selection or cut edge within grabbing distance of x - the selection's first, it's the one being worked on.</summary>
	private (DragMode Mode, int Cut)? EdgeAt(double x)
	{
		if (_selection is { } selection)
		{
			if (Math.Abs(X(selection.StartSeconds) - x) <= EdgeGrabPixels) return (DragMode.SelectionStart, -1);
			if (Math.Abs(X(selection.EndSeconds) - x) <= EdgeGrabPixels) return (DragMode.SelectionEnd, -1);
		}

		for (var i = 0; i < _cuts.Count; i++)
		{
			if (Math.Abs(X(_cuts[i].StartSeconds) - x) <= EdgeGrabPixels) return (DragMode.CutStart, i);
			if (Math.Abs(X(_cuts[i].EndSeconds) - x) <= EdgeGrabPixels) return (DragMode.CutEnd, i);
		}

		return null;
	}

	private int? CutIndexAt(double seconds)
	{
		for (var i = 0; i < _cuts.Count; i++)
			if (seconds >= _cuts[i].StartSeconds && seconds < _cuts[i].EndSeconds)
				return i;

		return null;
	}

	/// <summary>Dragged edges catch the playhead when they come close to it - the usual way to line a cut up with the frame on screen.</summary>
	private double Snap(double seconds)
	{
		return Math.Abs(X(seconds) - X(Value)) <= SnapPixels ? Value : seconds;
	}

	protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
	{
		base.OnAttachedToVisualTree(e);
		_attached = true;
		UpdateStripeAnimation();
	}

	protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
	{
		base.OnDetachedFromVisualTree(e);
		_attached = false;
		UpdateStripeAnimation();
		_tracksLayer?.Dispose();
		_tracksLayer = null;
		_waveformBitmap?.Dispose();
		_waveformBitmap = null;
	}

	/// <summary>The stripes' timer runs only while there is a cut to draw on a timeline in the window.</summary>
	private void UpdateStripeAnimation()
	{
		var needed = _attached && _cuts.Count > 0;
		if (needed == _stripeTimer is not null) return;

		if (needed)
		{
			_stripeTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = StripeFrameInterval };
			_stripeTimer.Tick += OnStripeFrame;
			_stripeTimer.Start();
		}
		else
		{
			_stripeTimer!.Stop();
			_stripeTimer.Tick -= OnStripeFrame;
			_stripeTimer = null;
		}
	}

	/// <summary>The offset follows the clock, not the ticks, so a late tick doesn't slow the stripes down; a hidden timeline (the compact one while the expanded one shows, or the reverse) isn't redrawn.</summary>
	private void OnStripeFrame(object? sender, EventArgs e)
	{
		if (!IsEffectivelyVisible) return;

		_stripeOffset = _stripeClock.Elapsed.TotalSeconds * StripePixelsPerSecond % StripeSpacing;
		InvalidateVisual();
	}

	private sealed class CachedThumbnail(Bitmap bitmap)
	{
		public Bitmap Bitmap { get; } = bitmap;
		public long LastUse { get; set; }
	}

	private enum DragMode
	{
		None,
		Scrub,
		NewSelection,
		SelectionStart,
		SelectionEnd,
		CutStart,
		CutEnd
	}
}
