using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace OsmoOverlay.Gui;

/// <summary>
///     The gap between two cards, dragged to resize them: Vertical is a bar between columns (dragged sideways), otherwise a bar between
///     rows. It only reports how far the pointer went (Dragged, from where it was pressed, in the parent's units - the parent's own layout
///     doesn't move with the card edge) and lets the window decide the sizes; a double-click asks for the default (ResetRequested).
///     Shows a line while the pointer is over it or it is dragged.
/// </summary>
public sealed class PanelSplitter : Control
{
	private static readonly IPen HighlightPen = new Pen(Palette.Accent, 2);
	private static readonly Cursor ColumnCursor = new(StandardCursorType.SizeWestEast);
	private static readonly Cursor RowCursor = new(StandardCursorType.SizeNorthSouth);

	private bool _vertical;
	private bool _hot;
	private bool _dragging;
	private double _pressed;

	static PanelSplitter()
	{
		FocusableProperty.OverrideDefaultValue<PanelSplitter>(false);
	}

	public PanelSplitter()
	{
		Cursor = RowCursor;
	}

	public bool Vertical
	{
		get => _vertical;
		set
		{
			_vertical = value;
			Cursor = value ? ColumnCursor : RowCursor;
			InvalidateVisual();
		}
	}

	/// <summary>The pointer's distance from where it was pressed along the bar's axis - while dragging.</summary>
	public event Action<double>? Dragged;

	/// <summary>The drag started: the window notes the sizes it is going to change.</summary>
	public event Action? DragStarted;

	/// <summary>The drag ended - the sizes are final (save them).</summary>
	public event Action? DragCompleted;

	/// <summary>A double-click: back to the default sizes.</summary>
	public event Action? ResetRequested;

	public override void Render(DrawingContext context)
	{
		context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
		if (!_hot && !_dragging) return;

		if (_vertical) context.DrawLine(HighlightPen, new Point(Bounds.Width / 2, 6), new Point(Bounds.Width / 2, Bounds.Height - 6));
		else context.DrawLine(HighlightPen, new Point(6, Bounds.Height / 2), new Point(Bounds.Width - 6, Bounds.Height / 2));
	}

	private double Axis(PointerEventArgs e)
	{
		Point point = e.GetPosition(this.GetVisualParent<Visual>());
		return _vertical ? point.X : point.Y;
	}

	protected override void OnPointerEntered(PointerEventArgs e)
	{
		base.OnPointerEntered(e);
		_hot = true;
		InvalidateVisual();
	}

	protected override void OnPointerExited(PointerEventArgs e)
	{
		base.OnPointerExited(e);
		_hot = false;
		InvalidateVisual();
	}

	protected override void OnPointerPressed(PointerPressedEventArgs e)
	{
		base.OnPointerPressed(e);
		if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

		e.Handled = true;
		if (e.ClickCount > 1)
		{
			ResetRequested?.Invoke();
			return;
		}

		_dragging = true;
		_pressed = Axis(e);
		e.Pointer.Capture(this);
		DragStarted?.Invoke();
		InvalidateVisual();
	}

	protected override void OnPointerMoved(PointerEventArgs e)
	{
		base.OnPointerMoved(e);
		if (_dragging) Dragged?.Invoke(Axis(e) - _pressed);
	}

	protected override void OnPointerReleased(PointerReleasedEventArgs e)
	{
		base.OnPointerReleased(e);
		if (!_dragging) return;

		e.Pointer.Capture(null);
	}

	protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
	{
		base.OnPointerCaptureLost(e);
		if (!_dragging) return;

		_dragging = false;
		InvalidateVisual();
		DragCompleted?.Invoke();
	}
}
