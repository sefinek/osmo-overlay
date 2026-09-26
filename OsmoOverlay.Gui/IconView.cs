using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace OsmoOverlay.Gui;

/// <summary>
///     Draws one Icons.axaml geometry: its 16x16 design grid scaled uniformly to this control's size, filled
///     with Foreground - inherited, so an icon inside a button takes the button's (hover/disabled) text color.
///     Not PathIcon: that stretches each geometry to its own bounds, so a narrow icon would render bigger
///     than a wide one and the set wouldn't line up.
/// </summary>
public sealed class IconView : Control
{
	private const double GridSize = 16;

	public static readonly StyledProperty<Geometry?> DataProperty = AvaloniaProperty.Register<IconView, Geometry?>(nameof(Data));
	public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<IconView>();

	static IconView()
	{
		AffectsRender<IconView>(DataProperty, ForegroundProperty);
	}

	public Geometry? Data
	{
		get => GetValue(DataProperty);
		set => SetValue(DataProperty, value);
	}

	public IBrush? Foreground
	{
		get => GetValue(ForegroundProperty);
		set => SetValue(ForegroundProperty, value);
	}

	protected override Size MeasureOverride(Size availableSize)
	{
		return new Size(double.IsNaN(Width) ? GridSize : Width, double.IsNaN(Height) ? GridSize : Height);
	}

	public override void Render(DrawingContext context)
	{
		if (Data is null || Foreground is null) return;

		var size = Math.Min(Bounds.Width, Bounds.Height);
		var scale = size / GridSize;
		Matrix transform = Matrix.CreateScale(scale, scale) *
		                   Matrix.CreateTranslation((Bounds.Width - size) / 2, (Bounds.Height - size) / 2);
		using (context.PushTransform(transform))
			context.DrawGeometry(Foreground, null, Data);
	}
}
