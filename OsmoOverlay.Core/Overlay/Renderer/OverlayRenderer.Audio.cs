using OsmoOverlay.Core.Telemetry;
using SkiaSharp;

namespace OsmoOverlay.Core.Overlay;

public sealed partial class OverlayRenderer
{
	internal const float AudioMeterWidth = 620;

	/// <summary>Where the meter is divided, every 12 dB of its -60 to 0 dBFS span.</summary>
	private static readonly int[] AudioMeterTicksDbfs = [-48, -36, -24, -12];

	private void DrawAudioLevel(SKCanvas canvas, DerivedFrame frame, AudioLevelElement element)
	{
		SKColor text = TextColorOf(element);
		SKColor outline = OutlineColorOf(element);
		DrawOutlined(canvas, (element.Label ?? DefaultLabel(element)).ToUpperInvariant(), 0, 0,
			TextFont(element, OverlayElementBounds.LabelFontSize), text, outlineColor: outline, outlineWidthScale: element.OutlineWidth);

		_markerFillPaint.Color = text.WithAlpha(60);
		canvas.DrawRect(0, 26, AudioMeterWidth, 24, _markerFillPaint);
		if (frame.Raw.AudioRmsDbfs is >= -128 and <= 0 and { } rms)
		{
			_markerFillPaint.Color = AccentColorOf(element);
			canvas.DrawRect(0, 26, AudioMeterFraction(rms) * AudioMeterWidth, 24, _markerFillPaint);
		}

		_markerFillPaint.Color = OutlineColorOf(element).WithAlpha(150);
		foreach (int tick in AudioMeterTicksDbfs)
			canvas.DrawRect(AudioMeterFraction(tick) * AudioMeterWidth - 1, 26, 2, 24, _markerFillPaint);
		if (frame.Raw.AudioPeakDbfs is >= -128 and <= 0 and { } peak)
		{
			_markerFillPaint.Color = text;
			float x = Math.Clamp(AudioMeterFraction(peak) * AudioMeterWidth, 2, AudioMeterWidth - 2);
			canvas.DrawRect(x - 2, 20, 4, 36, _markerFillPaint);
		}

		string values = $"RMS {FormatAudioLevel(frame.Raw.AudioRmsDbfs)}   PEAK {FormatAudioLevel(frame.Raw.AudioPeakDbfs)} dBFS";
		DrawOutlined(canvas, values, 0, 106, TextFont(element, OverlayElementBounds.SmallFontSize), text,
			outlineColor: outline, outlineWidthScale: element.OutlineWidth);
	}

	internal static float AudioMeterFraction(double dbfs) => (float)Math.Clamp((dbfs + 60) / 60, 0, 1);

	private static string FormatAudioLevel(double? dbfs) => dbfs is >= -128 and <= 0 and { } value ? F(value, "0") : "--";
}
