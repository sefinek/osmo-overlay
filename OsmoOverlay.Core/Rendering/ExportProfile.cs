using OsmoOverlay.Core.Localization;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Core;

/// <summary>
///     A ready-made set of the export options (size, codec, depth, bitrate - the same ones Settings has) for a common use of the
///     file. Applying one only sets those fields, so the output still goes through OutputVideo.For and the default (Source) is
///     exactly output parity. Only values Settings itself offers are used, so a profile always shows as chosen there.
/// </summary>
public sealed record ExportProfile(string Id, int? Resolution, string? Codec, bool EightBit, double BitrateMultiplier)
{
	public const string SourceId = "source";

	public static readonly ExportProfile Source = new(SourceId, null, null, false, 1.0);

	public static readonly IReadOnlyList<ExportProfile> All =
	[
		Source,
		new("compatible", null, null, true, 1.0),
		new("web1080", 1080, "h264", false, 1.0),
		new("phone720", 720, "h264", false, 1.0),
		new("quality", null, null, false, 1.5)
	];

	public string Name => Id switch
	{
		SourceId => CoreStrings.ExportProfile_Source,
		"compatible" => CoreStrings.ExportProfile_Compatible,
		"web1080" => CoreStrings.ExportProfile_Web1080,
		"phone720" => CoreStrings.ExportProfile_Phone720,
		"quality" => CoreStrings.ExportProfile_Quality,
		_ => Id
	};

	/// <summary>The profile named by its id or its name (any case), for the command line.</summary>
	public static ExportProfile? Find(string text)
	{
		return All.FirstOrDefault(p => p.Id.Equals(text, StringComparison.OrdinalIgnoreCase) || p.Name.Equals(text, StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>The profile whose options these settings have, or null for a mix of the user's own.</summary>
	public static ExportProfile? Of(OverlaySettings settings)
	{
		return All.FirstOrDefault(p => p.Matches(settings));
	}

	public bool Matches(OverlaySettings settings)
	{
		return settings.OutputResolution == Resolution && settings.OutputCodec == Codec && settings.OutputEightBit == EightBit &&
		       Math.Abs(settings.OutputBitrateMultiplier - BitrateMultiplier) < 0.001;
	}

	/// <summary>The settings with these options and nothing else changed.</summary>
	public OverlaySettings ApplyTo(OverlaySettings settings)
	{
		return settings with
		{
			OutputResolution = Resolution,
			OutputCodec = Codec,
			OutputEightBit = EightBit,
			OutputBitrateMultiplier = BitrateMultiplier
		};
	}
}
