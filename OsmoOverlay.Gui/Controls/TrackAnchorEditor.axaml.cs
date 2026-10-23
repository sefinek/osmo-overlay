using System.Globalization;
using Avalonia.Controls;
using OsmoOverlay.Core.Telemetry.External;

namespace OsmoOverlay.Gui;

/// <summary>
///     One synchronization point of an external track: a moment of the video (seconds from the start of the whole recording) and the
///     track's UTC time of the same event - typed, or picked from the track's own fixes.
/// </summary>
public partial class TrackAnchorEditor : UserControl
{
	private double _previewSeconds;

	public TrackAnchorEditor()
	{
		InitializeComponent();
		CurrentFrameButton.Click += (_, _) => VideoSeconds = _previewSeconds;
		PointBox.SelectionChanged += (_, _) =>
		{
			if (PointBox.SelectedItem is TrackPointChoice choice) Utc = choice.Point.Utc;
		};
	}

	/// <summary>The latest moment of the video the point can be at, and the step of its box (one frame).</summary>
	public void SetVideoRange(double lastSeconds, double framesPerSecond, double previewSeconds)
	{
		VideoBox.Maximum = (decimal)lastSeconds;
		VideoBox.Increment = framesPerSecond > 0 ? (decimal)(1 / framesPerSecond) : 0.04m;
		_previewSeconds = Math.Clamp(previewSeconds, 0, lastSeconds);
	}

	public void SetTrackPoints(IEnumerable<ExternalTrackPoint> points)
	{
		PointBox.ItemsSource = points.Select(p => new TrackPointChoice(p)).ToList();
	}

	public double VideoSeconds
	{
		get => (double)(VideoBox.Value ?? 0);
		set => VideoBox.Value = (decimal)Math.Clamp(value, 0, (double)VideoBox.Maximum);
	}

	public double MaxVideoSeconds => (double)VideoBox.Maximum;

	public DateTime Utc
	{
		set => UtcBox.Text = value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
	}

	/// <summary>The point as typed - InvalidDataException with a message for the user when the time or the date doesn't read.</summary>
	public TrackSyncAnchor Read()
	{
		if (DataValidationErrors.GetHasErrors(VideoBox) || VideoBox.Value is not { } seconds ||
		    !TrackSyncAnchor.TryParse($"{seconds.ToString(CultureInfo.InvariantCulture)}={UtcBox.Text}", out TrackSyncAnchor? anchor))
			throw new InvalidDataException(Strings.Track_InvalidTime);

		return anchor!;
	}

	private sealed record TrackPointChoice(ExternalTrackPoint Point)
	{
		public override string ToString() => $"{Point.Utc:HH:mm:ss} UTC" + (Point.SpeedMs is { } speed ? $" · {speed * 3.6:0.0} km/h" : "");
	}
}
