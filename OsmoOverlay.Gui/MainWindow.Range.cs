using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using AvaloniaRectangle = Avalonia.Controls.Shapes.Rectangle;

namespace OsmoOverlay.Gui;

/// <summary>
///     Render range: In/Out points set on the preview timeline (buttons or I/O keys, like an NLE), shown as
///     a band under the scrubber and passed to RenderJob as RenderOptions.RangeStartSeconds/RangeEndSeconds.
///     Out includes the frame it was set on. The preview gets the same timeline offset the render will use
///     (PreviewPlayer.SetTimelineOffset), so from In onwards it shows the clip exactly as rendered.
/// </summary>
public partial class MainWindow
{
	private static readonly IBrush RangeBrush = Palette.Tint(Palette.Accent, 0.35);

	private double? _rangeStartSeconds;
	private double? _rangeEndSeconds;

	private bool HasRange => _rangeStartSeconds is not null || _rangeEndSeconds is not null;

	private void WireRangeShortcuts()
	{
		KeyDown += OnRangeShortcutKeyDown;
		RangeCanvas.SizeChanged += (_, _) => DrawRangeMarks();
	}

	private void OnRangeShortcutKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.KeyModifiers != KeyModifiers.None || !SetRangeStartButton.IsEnabled) return;
		// Typing an "i" or "o" into a text box (preset name, color, label...) must not move the range.
		if (FocusManager?.GetFocusedElement() is TextBox) return;

		switch (e.Key)
		{
			case Key.I:
				SetRangeStart();
				e.Handled = true;
				break;
			case Key.O:
				SetRangeEnd();
				e.Handled = true;
				break;
		}
	}

	private void OnSetRangeStartClick(object? sender, RoutedEventArgs e)
	{
		SetRangeStart();
	}

	private void OnSetRangeEndClick(object? sender, RoutedEventArgs e)
	{
		SetRangeEnd();
	}

	private void OnClearRangeClick(object? sender, RoutedEventArgs e)
	{
		ClearRange();
	}

	private void SetRangeStart()
	{
		var position = PreviewSlider.Value;
		_rangeStartSeconds = position <= 0 ? null : position;
		if (_rangeEndSeconds <= position) _rangeEndSeconds = null;
		ApplyRange();
	}

	private void SetRangeEnd()
	{
		if (_summary is null) return;

		// The slider sits on the start of the frame being shown - the range ends after it.
		var end = PreviewSlider.Value + 1 / _summary.Video.Fps;
		_rangeEndSeconds = end >= PreviewSlider.Maximum ? null : end;
		if (_rangeStartSeconds >= end) _rangeStartSeconds = null;
		ApplyRange();
	}

	private void ClearRange()
	{
		_rangeStartSeconds = null;
		_rangeEndSeconds = null;
		ApplyRange();
	}

	private void ApplyRange()
	{
		_previewPlayer.SetTimelineOffset(_rangeStartSeconds ?? 0);
		ClearRangeButton.IsVisible = HasRange;
		ToolTip.SetTip(ClearRangeButton, HasRange ? $"Render the whole recording (now: {DescribeRange()})" : null);
		DrawRangeMarks();
		if (_summary is not null) PopulateOutputInfo(_summary, _detectedEncoder);
	}

	/// <summary>"01:00.00 - 01:05.00" on the recording's timeline.</summary>
	private string DescribeRange()
	{
		return $"{FormatRangeTime(_rangeStartSeconds ?? 0)} - {FormatRangeTime(_rangeEndSeconds ?? PreviewSlider.Maximum)}";
	}

	private static string FormatRangeTime(double seconds)
	{
		return TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss\.ff" : @"mm\:ss\.ff");
	}

	/// <summary>Frames the render will produce - the range (rounded to frames the same way RenderJob does), then the frame limit.</summary>
	private long PlannedFrameCount(long sourceFrames, double fps)
	{
		var start = _rangeStartSeconds is { } s ? Math.Clamp((long)Math.Round(s * fps), 0, sourceFrames) : 0;
		var end = _rangeEndSeconds is { } e ? Math.Clamp((long)Math.Round(e * fps), 0, sourceFrames) : sourceFrames;
		var count = Math.Max(0, end - start);
		return _frameLimit is { } limit ? Math.Min(limit, count) : count;
	}

	private void DrawRangeMarks()
	{
		RangeCanvas.Children.Clear();

		var width = RangeCanvas.Bounds.Width;
		var duration = PreviewSlider.Maximum;
		if (!HasRange || width <= 0 || duration <= 0) return;

		var x1 = width * Math.Clamp((_rangeStartSeconds ?? 0) / duration, 0, 1);
		var x2 = width * Math.Clamp((_rangeEndSeconds ?? duration) / duration, 0, 1);
		var band = new AvaloniaRectangle
		{
			Width = Math.Max(2, x2 - x1), Height = RangeCanvas.Height, Fill = RangeBrush, RadiusX = 3, RadiusY = 3
		};
		Canvas.SetLeft(band, x1);
		RangeCanvas.Children.Add(band);
	}
}
