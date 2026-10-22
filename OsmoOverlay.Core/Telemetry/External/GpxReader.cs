using System.Globalization;
using System.Xml;

namespace OsmoOverlay.Core.Telemetry.External;

/// <summary>
///     The track points of a GPX file (trkpt, in every segment of every track): position, elevation and time. GPX has no
///     speed and no timer events - a device that wrote only fixes - so a gap in it is told a stop from a lost signal by how far
///     the camera moved over it (ExternalTrack). Points without a time can't be put on the video's clock and are dropped.
///     The XML is read without resolving DTDs or external entities.
/// </summary>
public static class GpxReader
{
	public static ExternalTrack Read(string path)
	{
		using FileStream stream = File.OpenRead(path);
		return Read(stream);
	}

	public static ExternalTrack Read(Stream stream)
	{
		var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, IgnoreWhitespace = true };
		using var reader = XmlReader.Create(stream, settings);

		List<ExternalTrackPoint> points = [];
		while (reader.Read())
		{
			if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "trkpt" && ReadPoint(reader) is { } point) points.Add(point);
		}

		return new ExternalTrack(points);
	}

	private static ExternalTrackPoint? ReadPoint(XmlReader reader)
	{
		if (!double.TryParse(reader.GetAttribute("lat"), NumberStyles.Float, CultureInfo.InvariantCulture, out double lat) ||
		    !double.TryParse(reader.GetAttribute("lon"), NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
			return null;

		double? elevation = null;
		DateTime? time = null;
		using (XmlReader point = reader.ReadSubtree())
		{
			point.Read();
			point.Read();
			while (!point.EOF)
			{
				if (point.NodeType != XmlNodeType.Element || point.Depth != 1)
				{
					point.Read();
					continue;
				}

				// Both of these move the reader on to the next node, so no Read after them.
				switch (point.LocalName)
				{
					case "ele":
						elevation = double.TryParse(point.ReadElementContentAsString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double ele) ? ele : null;
						break;
					case "time":
						time = DateTimeOffset.TryParse(point.ReadElementContentAsString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset utc)
							? utc.UtcDateTime
							: null;
						break;
					default:
						point.Skip();
						break;
				}
			}
		}

		return time is { } at ? new ExternalTrackPoint(at, lat, lon, elevation) : null;
	}
}
