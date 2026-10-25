using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Preview;

namespace OsmoOverlay.Gui;

/// <summary>
///     The music under the expanded timeline's layers (MusicClip): one track, clips laid on the output's timeline. A clip's
///     body moves it, its edges trim it (the left one also moves where in the file it starts), the handles at its top corners
///     set the fades. A click on the empty track moves the playhead, a right-click asks for a menu (MenuRequested), a
///     double-click for the clip's settings (EditRequested). Times snap to whole frames, and within SnapPixels to the playhead,
///     the other clips' edges, the cuts and the ends. Drawn on the expanded timeline's own mapping (Source) right of a
///     HeaderWidth column; the timeline shows the recording and music is timed on the output, so ToRecording and ToOutput
///     convert between the two.
/// </summary>
public sealed class MusicTimeline : Control
{
	public const double HeaderWidth = LayerTimeline.HeaderWidth;
	public const double RowHeight = 30;

	private const double ClipInset = 3;
	private const double EdgeGrabPixels = 5;
	private const double FadeHandleSize = 7;
	private const double SnapPixels = 6;
	private const double AddButtonSize = 18;
	private const double AutoScrollEdgePixels = 24;

	private static readonly IBrush HeaderBrush = Palette.Control;
	private static readonly IBrush TrackBrush = Palette.SubtleFill;
	private static readonly IBrush ClipBrush = Palette.Tint(Palette.Success, 0.38);
	private static readonly IBrush SelectedClipBrush = Palette.Tint(Palette.Success, 0.62);
	private static readonly IPen ClipBorder = new Pen(Palette.Success);
	private static readonly IPen SelectedClipBorder = new Pen(Palette.TextPrimary, 1.5);
	private static readonly IPen WaveformPen = new Pen(Palette.Tint(Palette.TextPrimary, 0.45), 1);
	private static readonly IPen RepeatPen = new Pen(Palette.Tint(Palette.TextPrimary, 0.45), 1, new DashStyle([2, 2], 0));
	private static readonly IBrush FadeShade = Palette.Tint(Palette.SurfaceSunken, 0.55);
	private static readonly IBrush CrossfadeFill = Palette.Tint(Palette.SurfaceSunken, 0.35);
	private static readonly IPen FadeLine = new Pen(Palette.TextPrimary);
	private static readonly IBrush FadeHandleBrush = Palette.TextPrimary;
	private static readonly IBrush PastEndFill = Palette.Tint(Palette.SurfaceSunken, 0.75);
	private static readonly IPen PlayheadPen = new Pen(Palette.TextPrimary, 1.5);
	private static readonly IBrush TextBrush = Palette.TextPrimary;
	private static readonly IBrush MutedTextBrush = Palette.TextMuted;
	private static readonly IBrush LabelBackground = Palette.SurfaceSunken;
	private static readonly IBrush AddButtonBrush = Palette.SurfaceSunken;
	private static readonly IPen AddPen = new Pen(Palette.TextPrimary, 1.5);
	private static readonly Typeface LabelTypeface = new(FontFamily.Default);
	private static readonly Cursor ResizeCursor = new(StandardCursorType.SizeWestEast);

	private const int MaxCachedTexts = 256;

	private readonly Dictionary<(string Text, IBrush Brush, int MaxWidth), FormattedText> _texts = [];
	private readonly PlayheadLine _playhead;
	private PreviewTimeline? _source;
	private IReadOnlyList<MusicClip> _clips = [];
	private int? _selected;
	private Zone _drag;
	private int _dragIndex;
	private MusicClip? _dragOrigin;
	private double _grabOffset;
	private bool _scrubbing;
	private Point _lastPoint;
	private IReadOnlyList<MusicClip>? _crossfadeSource;
	private IReadOnlyList<MusicClip> _crossfaded = [];
	private DispatcherTimer? _autoScroll;
	private (Point At, string Text)? _dragLabel;

	static MusicTimeline()
	{
		FocusableProperty.OverrideDefaultValue<MusicTimeline>(false);
	}

	public MusicTimeline()
	{
		ClipToBounds = true;
		_playhead = new PlayheadLine(this);
		VisualChildren.Add(_playhead);
	}

	/// <summary>The clips as dragged - `final` on release (save it), not while dragging (preview only). Not normalized yet.</summary>
	public event Action<IReadOnlyList<MusicClip>, bool>? ClipsEdited;

	/// <summary>A right-click: the clip under it (null on the empty track) and the output time there.</summary>
	public event Action<int?, double>? MenuRequested;

	/// <summary>A double-click on a clip.</summary>
	public event Action<int>? EditRequested;

	/// <summary>A clip was clicked (its index) or the empty track was (null) - the selection the keyboard shortcuts act on.</summary>
	public event Action<int?>? SelectionChanged;

	/// <summary>The "+" in the header.</summary>
	public event Action? AddRequested;

	/// <summary>A click or drag on the empty track: the recording time to move the playhead to.</summary>
	public event Action<double>? SeekRequested;

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

			InvalidateVisual();
		}
	}

	public IReadOnlyList<MusicClip> Clips
	{
		get => _clips;
		set
		{
			_clips = value;
			if (_selected >= value.Count) _selected = null;
			InvalidateVisual();
		}
	}

	public int? SelectedIndex
	{
		get => _selected;
		set
		{
			_selected = value;
			InvalidateVisual();
		}
	}

	/// <summary>The peaks of a music file for its clips, null until they are decoded.</summary>
	public Func<string, AudioWaveform?>? Waveform { get; set; }

	public double OutputDurationSeconds { get; set; }
	public double Fps { get; set; } = 30;
	public Func<double, double> ToRecording { get; set; } = seconds => seconds;
	public Func<double, double> ToOutput { get; set; } = seconds => seconds;

	private double MinClipSeconds => Math.Max(MusicTrack.MinLengthSeconds, 1 / Fps);

	private void OnSourcePropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
	{
		if (!IsEffectivelyVisible) return;

		if (e.Property == RangeBase.ValueProperty) _playhead.InvalidateVisual();
		else if (e.Property == BoundsProperty) Redraw();
	}

	/// <summary>Clips and playhead - for what moves both; the playhead alone moves on its own (not redrawing every waveform column as playback goes).</summary>
	private void Redraw()
	{
		InvalidateVisual();
		_playhead.InvalidateVisual();
	}

	protected override Size MeasureOverride(Size availableSize)
	{
		_playhead.Measure(availableSize);
		return new Size(0, RowHeight);
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

	private (double X1, double X2, double Top, double Bottom) ClipRect(MusicClip clip)
	{
		double x1 = X(clip.StartSeconds);
		return (x1, Math.Max(X(clip.EndSeconds), x1 + 2), ClipInset, RowHeight - ClipInset);
	}

	private static Rect AddRect()
	{
		return new Rect(HeaderWidth - 6 - AddButtonSize, (RowHeight - AddButtonSize) / 2, AddButtonSize, AddButtonSize);
	}

	public override void Render(DrawingContext context)
	{
		double width = Bounds.Width, height = Bounds.Height;
		double header = HeaderWidth;
		context.FillRectangle(TrackBrush, new Rect(header, 0, Math.Max(0, width - header), height));
		context.FillRectangle(HeaderBrush, new Rect(0, 0, header, height));
		DrawText(context, Strings.Main_Music, new Point(10, (height - 14) / 2), MutedTextBrush, header - AddButtonSize - 24);

		Rect add = AddRect();
		context.DrawRectangle(AddButtonBrush, null, add, 3, 3);
		context.DrawLine(AddPen, new Point(add.X + 5, add.Center.Y), new Point(add.Right - 5, add.Center.Y));
		context.DrawLine(AddPen, new Point(add.Center.X, add.Y + 5), new Point(add.Center.X, add.Bottom - 5));

		using (context.PushClip(new Rect(header, 0, Math.Max(0, width - header), height)))
		{
			if (_source is not null && OutputDurationSeconds > 0)
			{
				double end = X(OutputDurationSeconds);
				if (end < width) context.FillRectangle(PastEndFill, new Rect(end, 0, width - end, height));

				if (_clips.Count == 0) DrawText(context, Strings.Music_EmptyHint, new Point(Math.Max(X(0), header) + 8, (height - 14) / 2), MutedTextBrush, width);
				IReadOnlyList<MusicClip> heard = Crossfaded();
				for (int i = 0; i < _clips.Count; i++) DrawClip(context, _clips[i], heard[i], i == _selected);
				for (int i = 0; i < _clips.Count; i++) DrawCrossfades(context, _clips, i);
			}
		}

		if (_dragLabel is { } label) DrawDragLabel(context, label.At, label.Text);
	}

	private void DrawClip(DrawingContext context, MusicClip clip, MusicClip heard, bool selected)
	{
		(double x1, double x2, double top, double bottom) = ClipRect(clip);
		// Zoomed in, a clip is thousands of pixels wide - only the part in view is drawn (a rounded rectangle or a triangle that long is slow to rasterize).
		double low = HeaderWidth - 8, high = Bounds.Width + 8;
		if (x2 < low || x1 > high) return;

		double cx1 = Math.Max(x1, low), cx2 = Math.Min(x2, high);
		var rect = new Rect(cx1, top, cx2 - cx1, bottom - top);
		context.DrawRectangle(selected ? SelectedClipBrush : ClipBrush, null, rect, 3, 3);

		DrawWaveform(context, clip, x1, x2, top, bottom);

		double fadeInX = X(clip.StartSeconds + clip.FadeInSeconds), fadeOutX = X(clip.EndSeconds - clip.FadeOutSeconds);
		double heardInX = X(heard.StartSeconds + heard.FadeInSeconds), heardOutX = X(heard.EndSeconds - heard.FadeOutSeconds);
		if (heard.FadeInSeconds > 0) DrawFade(context, new Point(cx1, bottom), new Point(Math.Clamp(heardInX, low, high), top), new Point(cx1, top), x1 >= low);
		if (heard.FadeOutSeconds > 0) DrawFade(context, new Point(cx2, bottom), new Point(Math.Clamp(heardOutX, low, high), top), new Point(cx2, top), x2 <= high);

		if (clip.Repeats)
		{
			double span = clip.SourceSeconds - clip.InSeconds;
			double firstRepeat = Math.Max(1, Math.Floor((OutputAt(HeaderWidth) - clip.StartSeconds) / span));
			for (double t = clip.StartSeconds + firstRepeat * span; t < clip.EndSeconds - 0.01 && X(t) <= high; t += span)
				context.DrawLine(RepeatPen, new Point(X(t), top + 2), new Point(X(t), bottom - 2));
		}

		context.DrawRectangle(null, selected ? SelectedClipBorder : ClipBorder, rect.Deflate(0.5), 3, 3);

		const double half = FadeHandleSize / 2;
		if (fadeInX >= low && fadeInX <= high) context.FillRectangle(FadeHandleBrush, new Rect(fadeInX - half, top, FadeHandleSize, FadeHandleSize / 1.4));
		if (fadeOutX >= low && fadeOutX <= high) context.FillRectangle(FadeHandleBrush, new Rect(fadeOutX - half, top, FadeHandleSize, FadeHandleSize / 1.4));

		double textX = Math.Max(x1, HeaderWidth) + 6;
		using (context.PushClip(rect.Deflate(1)))
			DrawText(context, Path.GetFileNameWithoutExtension(clip.Path), new Point(textX, top + (bottom - top - 14) / 2), TextBrush, cx2 - 6 - textX);
	}

	/// <summary>The crossfade where a clip starts over the end of another: the two ramps crossing, as in a video editor.</summary>
	private void DrawCrossfades(DrawingContext context, IReadOnlyList<MusicClip> clips, int later)
	{
		MusicClip b = clips[later];
		for (int earlier = 0; earlier < clips.Count; earlier++)
		{
			MusicClip a = clips[earlier];
			if (earlier == later || b.StartSeconds <= a.StartSeconds || b.StartSeconds >= a.EndSeconds || b.EndSeconds <= a.EndSeconds) continue;

			double x1 = Math.Max(X(b.StartSeconds), HeaderWidth - 8), x2 = Math.Min(X(a.EndSeconds), Bounds.Width + 8);
			if (x2 - x1 < 2) continue;

			double top = ClipInset, bottom = RowHeight - ClipInset;
			context.DrawRectangle(CrossfadeFill, null, new Rect(x1, top, x2 - x1, bottom - top));
			context.DrawLine(FadeLine, new Point(x1, bottom), new Point(x2, top));
			context.DrawLine(FadeLine, new Point(x1, top), new Point(x2, bottom));
		}
	}

	private IReadOnlyList<MusicClip> Crossfaded()
	{
		if (!ReferenceEquals(_clips, _crossfadeSource))
		{
			_crossfadeSource = _clips;
			_crossfaded = MusicTrack.WithCrossfades(_clips);
		}

		return _crossfaded;
	}

	/// <summary>One column per DIP, as tall as the loudest peak of the file under it (AudioWaveform.DisplayLevel) - the part of the file the clip plays, repeated where it loops.</summary>
	private void DrawWaveform(DrawingContext context, MusicClip clip, double x1, double x2, double top, double bottom)
	{
		if (Waveform?.Invoke(clip.Path) is not { } waveform || _source is null) return;

		double from = Math.Max(Math.Ceiling(x1), HeaderWidth), to = Math.Min(x2, Bounds.Width);
		double middle = (top + bottom) / 2, half = (bottom - top) / 2 - 2;
		for (double x = from; x < to; x++)
		{
			double t0 = Math.Max(0, OutputAt(x) - clip.StartSeconds), t1 = Math.Max(t0, OutputAt(x + 1) - clip.StartSeconds);
			double a = MusicTrack.FilePosition(clip, t0), b = MusicTrack.FilePosition(clip, t1);
			if (b < a) b = a;

			int bucketFrom = (int)(a * AudioWaveform.BucketsPerSecond), bucketTo = Math.Max(bucketFrom + 1, (int)(b * AudioWaveform.BucketsPerSecond));
			float peak = 0;
			for (int c = 0; c < waveform.Channels; c++) peak = Math.Max(peak, waveform.Peak(c, bucketFrom, bucketTo));

			double height = AudioWaveform.DisplayLevel(peak, waveform.LoudestPeak) * half;
			if (height > 0.4) context.DrawLine(WaveformPen, new Point(x + 0.5, middle - height), new Point(x + 0.5, middle + height));
		}
	}

	private static void DrawFade(DrawingContext context, Point from, Point to, Point corner, bool drawLine = true)
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
		if (drawLine) context.DrawLine(FadeLine, from, to);
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

	private void DrawDragLabel(DrawingContext context, Point at, string text)
	{
		var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelTypeface, 11, TextBrush);
		double x = Math.Clamp(at.X + 10, HeaderWidth, Math.Max(HeaderWidth, Bounds.Width - formatted.Width - 8));
		double y = Math.Clamp(at.Y - 22, 0, Math.Max(0, Bounds.Height - formatted.Height - 4));
		context.DrawRectangle(LabelBackground, ClipBorder, new Rect(x - 4, y - 1, formatted.Width + 8, formatted.Height + 2), 3, 3);
		context.DrawText(formatted, new Point(x, y));
	}

	/// <summary>What's under a point: the clip (the last, drawn in front, first) and the part of it - the fade handles before the edges.</summary>
	private (int Index, Zone Zone) HitAt(Point point)
	{
		if (point.Y < 0 || point.Y > RowHeight) return (-1, Zone.None);
		if (point.X < HeaderWidth) return (-1, AddRect().Inflate(2).Contains(point) ? Zone.Add : Zone.Header);

		for (int i = _clips.Count - 1; i >= 0; i--)
		{
			MusicClip clip = _clips[i];
			(double x1, double x2, double top, double bottom) = ClipRect(clip);
			if (point.Y <= top + FadeHandleSize)
			{
				if (Math.Abs(point.X - X(clip.StartSeconds + clip.FadeInSeconds)) <= FadeHandleSize) return (i, Zone.FadeIn);
				if (Math.Abs(point.X - X(clip.EndSeconds - clip.FadeOutSeconds)) <= FadeHandleSize) return (i, Zone.FadeOut);
			}

			if (point.Y < top || point.Y > bottom) continue;
			if (Math.Abs(point.X - x1) <= EdgeGrabPixels) return (i, Zone.Start);
			if (Math.Abs(point.X - x2) <= EdgeGrabPixels) return (i, Zone.End);
			if (point.X >= x1 && point.X <= x2) return (i, Zone.Body);
		}

		return (-1, Zone.Empty);
	}

	protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
	{
		base.OnPointerWheelChanged(e);
		bool sideways = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y);
		if (!sideways && !e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;

		double x = Math.Max(e.GetPosition(this).X - HeaderWidth, 0);
		if (_source?.Wheel(x, e.Delta, e.KeyModifiers & ~KeyModifiers.Control) == true) e.Handled = true;
	}

	protected override void OnPointerPressed(PointerPressedEventArgs e)
	{
		base.OnPointerPressed(e);
		PointerPoint point = e.GetCurrentPoint(this);
		(int index, Zone zone) = HitAt(point.Position);
		if (zone == Zone.None) return;

		if (point.Properties.IsRightButtonPressed)
		{
			e.Handled = true;
			Cursor = null;
			SelectedIndex = index >= 0 ? index : null;
			SelectionChanged?.Invoke(_selected);
			MenuRequested?.Invoke(index >= 0 ? index : null, OutputAt(point.Position.X));
			return;
		}

		if (!point.Properties.IsLeftButtonPressed) return;

		e.Handled = true;
		if (zone == Zone.Add)
		{
			AddRequested?.Invoke();
			return;
		}

		if (zone == Zone.Header) return;

		if (zone == Zone.Empty)
		{
			SelectedIndex = null;
			SelectionChanged?.Invoke(null);
			_scrubbing = true;
			e.Pointer.Capture(this);
			ScrubStarted?.Invoke();
			Seek(point.Position.X);
			return;
		}

		SelectedIndex = index;
		SelectionChanged?.Invoke(index);
		if (e.ClickCount > 1)
		{
			EditRequested?.Invoke(index);
			return;
		}

		_drag = zone;
		_dragIndex = index;
		_dragOrigin = _clips[index];
		_grabOffset = OutputAt(point.Position.X) - _clips[index].StartSeconds;
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
			(_, Zone hovered) = HitAt(point);
			Cursor = hovered switch
			{
				Zone.Start or Zone.End => ResizeCursor,
				Zone.FadeIn or Zone.FadeOut => ResizeCursor,
				_ => null
			};
			if (hovered == Zone.Add) ToolTip.SetTip(this, Strings.Music_AddTip);
			else ToolTip.SetTip(this, null);
			return;
		}

		UpdateDrag(point);
	}

	private void UpdateDrag(Point point)
	{
		_lastPoint = point;
		MusicClip dragged = Drag(_dragOrigin!, OutputAt(point.X));
		_dragLabel = (point, DragLabel(dragged));
		ClipsEdited?.Invoke(With(dragged), false);
		InvalidateVisual();
		UpdateAutoScroll(point);
	}

	/// <summary>A clip dragged to the edge of the view takes the view along (while the pointer stays there), so a zoomed-in clip can be stretched over the whole recording.</summary>
	private void UpdateAutoScroll(Point point)
	{
		if (_source is null || _drag == Zone.None || (point.X < Bounds.Width - AutoScrollEdgePixels && point.X > HeaderWidth + AutoScrollEdgePixels))
		{
			_autoScroll?.Stop();
			return;
		}

		_autoScroll ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
		_autoScroll.Tick -= OnAutoScrollTick;
		_autoScroll.Tick += OnAutoScrollTick;
		if (!_autoScroll.IsEnabled) _autoScroll.Start();
	}

	private void OnAutoScrollTick(object? sender, EventArgs e)
	{
		if (_source is null || _drag == Zone.None || _dragOrigin is null)
		{
			_autoScroll?.Stop();
			return;
		}

		double direction = _lastPoint.X > Bounds.Width - AutoScrollEdgePixels ? 1 : _lastPoint.X < HeaderWidth + AutoScrollEdgePixels ? -1 : 0;
		if (direction == 0)
		{
			_autoScroll?.Stop();
			return;
		}

		double before = _source.ViewStart;
		_source.ScrollTo(before + direction * _source.ViewLength * 0.02);
		if (_source.ViewStart != before) UpdateDrag(_lastPoint);
	}

	protected override void OnPointerExited(PointerEventArgs e)
	{
		base.OnPointerExited(e);
		if (_drag == Zone.None) Cursor = null;
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

	private void EndDrag(Point? at, bool commit)
	{
		if (_scrubbing)
		{
			_scrubbing = false;
			ScrubEnded?.Invoke();
		}

		_autoScroll?.Stop();
		MusicClip? origin = _dragOrigin;
		bool dragging = _drag != Zone.None && origin is not null && _dragIndex < _clips.Count;
		MusicClip? result = dragging ? commit && at is { } point ? Drag(origin!, OutputAt(point.X)) : origin : null;
		_drag = Zone.None;
		_dragOrigin = null;
		_dragLabel = null;
		InvalidateVisual();
		if (result is not null) ClipsEdited?.Invoke(With(result), true);
	}

	private List<MusicClip> With(MusicClip clip)
	{
		List<MusicClip> clips = [.. _clips];
		clips[_dragIndex] = clip;
		return clips;
	}

	private void Seek(double x)
	{
		if (_source is not null) SeekRequested?.Invoke(_source.SecondsAt(x - HeaderWidth));
	}

	/// <summary>`clip` (as it was when the drag began) with the dragged part at output time `at`.</summary>
	private MusicClip Drag(MusicClip clip, double at)
	{
		double start = clip.StartSeconds, end = clip.EndSeconds;
		switch (_drag)
		{
			case Zone.Body:
			{
				double dragged = at - _grabOffset;
				double newStart = Math.Round(dragged * Fps) / Fps;
				(double Seconds, double Distance)? startTarget = NearestTarget(dragged);
				(double Seconds, double Distance)? endTarget = NearestTarget(dragged + clip.LengthSeconds);
				if (startTarget is { } s && (endTarget is not { } e || s.Distance <= e.Distance)) newStart = s.Seconds;
				else if (endTarget is { } e2) newStart = e2.Seconds - clip.LengthSeconds;
				newStart = Math.Clamp(newStart, 0, Math.Max(0, OutputDurationSeconds - clip.LengthSeconds));
				return clip with { StartSeconds = newStart };
			}
			case Zone.Start:
			{
				double newStart = Math.Clamp(Snap(at), Math.Max(0, start - clip.InSeconds), end - MinClipSeconds);
				double inPoint = clip.InSeconds + (newStart - start);
				if (clip.SourceSeconds > 0) inPoint = Math.Min(inPoint, clip.SourceSeconds - MusicTrack.MinLengthSeconds);
				return clip with { StartSeconds = newStart, InSeconds = Math.Max(0, inPoint), LengthSeconds = end - newStart };
			}
			case Zone.End:
			{
				double room = clip.Loop || clip.SourceSeconds <= 0 ? OutputDurationSeconds : Math.Min(OutputDurationSeconds, start + clip.SourceSeconds - clip.InSeconds);
				return clip with { LengthSeconds = Math.Clamp(Snap(at), start + MinClipSeconds, Math.Max(start + MinClipSeconds, room)) - start };
			}
			case Zone.FadeIn:
				return clip with { FadeInSeconds = Math.Clamp(Snap(at) - start, 0, clip.LengthSeconds - clip.FadeOutSeconds) };
			case Zone.FadeOut:
				return clip with { FadeOutSeconds = Math.Clamp(end - Snap(at), 0, clip.LengthSeconds - clip.FadeInSeconds) };
			default:
				return clip;
		}
	}

	private double Snap(double seconds)
	{
		return NearestTarget(seconds)?.Seconds ?? Math.Round(seconds * Fps) / Fps;
	}

	private (double Seconds, double Distance)? NearestTarget(double seconds)
	{
		double x = X(seconds);
		(double, double)? best = null;
		double bestDistance = SnapPixels;
		foreach (double target in SnapTargets())
		{
			double distance = Math.Abs(X(target) - x);
			if (distance > bestDistance) continue;

			best = (target, distance);
			bestDistance = distance;
		}

		return best;
	}

	private IEnumerable<double> SnapTargets()
	{
		yield return 0;
		yield return OutputDurationSeconds;
		if (_source is null) yield break;

		yield return ToOutput(_source.Value);
		foreach (TimeRange cut in _source.Cuts) yield return ToOutput(cut.EndSeconds);
		for (int i = 0; i < _clips.Count; i++)
		{
			if (i == _dragIndex && _drag != Zone.None) continue;

			yield return _clips[i].StartSeconds;
			yield return _clips[i].EndSeconds;
		}
	}

	private string DragLabel(MusicClip clip)
	{
		return _drag switch
		{
			Zone.FadeIn => string.Format(Strings.Timeline_InAnimation, clip.FadeInSeconds.ToString("0.0#", CultureInfo.InvariantCulture)),
			Zone.FadeOut => string.Format(Strings.Timeline_OutAnimation, clip.FadeOutSeconds.ToString("0.0#", CultureInfo.InvariantCulture)),
			Zone.End => string.Format(Strings.Timeline_Out, TimeText.Format(clip.EndSeconds)),
			_ => string.Format(Strings.Timeline_In, TimeText.Format(clip.StartSeconds)) + " · " + string.Format(Strings.Timeline_Out, TimeText.Format(clip.EndSeconds))
		};
	}

	private sealed class PlayheadLine : Control
	{
		private readonly MusicTimeline _owner;

		public PlayheadLine(MusicTimeline owner)
		{
			_owner = owner;
			IsHitTestVisible = false;
		}

		public override void Render(DrawingContext context)
		{
			if (_owner._source is not { } source || _owner.OutputDurationSeconds <= 0) return;

			double x = Math.Round(HeaderWidth + source.XOf(source.Value)) + 0.5;
			if (x >= HeaderWidth && x <= Bounds.Width) context.DrawLine(PlayheadPen, new Point(x, 0), new Point(x, Bounds.Height));
		}
	}

	private enum Zone
	{
		None,
		Header,
		Add,
		Empty,
		Body,
		Start,
		End,
		FadeIn,
		FadeOut
	}
}
