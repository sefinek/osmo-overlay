using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Core;

public static class TelemetryExtraction
{
	public static TelemetryExtractionResult Extract(string inputPath, SourceInfo source)
	{
		if (source.DjmdStreamIndex is { } djmdStreamIndex)
			try
			{
				var raw = DjiMetaTelemetryParser.ExtractRawStream(inputPath, djmdStreamIndex);
				return DjiMetaTelemetryParser.Parse(raw, source.Video.Fps);
			}
			catch
			{
				// Native djmd decode is verified against DJI Osmo Action 6 firmware; fall back
				// to exiftool below for other models/firmware where the raw layout might differ.
			}

		return ExifToolRunner.Extract(inputPath);
	}
}
