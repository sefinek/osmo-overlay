using System.Diagnostics;

namespace OsmoOverlay.Core.Ffmpeg;

/// <summary>
///     Vegas Pro's MainConcept MP4 muxer (confirmed on Vegas Pro 2026's HEVC/NVENC export path only -
///     the AVC/H.264 export path hasn't been tested and may not have the same issue) leaves
///     color_primaries/color_transfer/color_space unset in the exported stream even when the project's
///     color space is explicitly Rec.709 - a muxer limitation, not a project setting the user can fix
///     from Vegas's own UI. Players that then can't read an explicit tag guess the matrix themselves
///     (often wrongly), which shows up as a small but real color shift. Measured on real DJI Osmo
///     footage: forcing bt709 on decode of an "unknown"-tagged render brings it back in line with the
///     source to within compression noise, confirming the encoded pixels were always correct - only the
///     tag was missing. The fix itself (below) still supports both HEVC and H.264 streams either way,
///     since the tag-rewrite is the same regardless of which encoder produced them.
/// </summary>
public sealed record ColorTagStatus(
	bool NeedsFix,
	bool IsEligible,
	string? ColorPrimaries,
	string? ColorTransfer,
	string? ColorSpace,
	string? ColorRange)
{
	public static ColorTagStatus From(VideoInfo video)
	{
		var missingPrimaries = IsMissing(video.ColorPrimaries);
		var missingTransfer = IsMissing(video.ColorTransfer);
		var missingSpace = IsMissing(video.ColorSpace);
		var needsFix = missingPrimaries || missingTransfer || missingSpace;

		// A file that already carries an EXPLICIT tag other than bt709 is real HDR/wide-gamut content
		// (Rec.2020, PQ/ST.2084, HLG, ...) - forcing bt709 over that would mislabel it, not fix it.
		// "Missing/unknown" is exactly the untagged-Vegas-export case this tool exists for; only a
		// tag that's present and NOT bt709 makes a file ineligible. This is the only validation this
		// tool needs: a random non-Osmo/non-Rec.709 file either already has correct tags (nothing to
		// do) or an explicit conflicting one (refused below) - there's no third case to guard against.
		var isEligible = (missingPrimaries || IsBt709(video.ColorPrimaries!))
		                 && (missingTransfer || IsBt709(video.ColorTransfer!))
		                 && (missingSpace || IsBt709(video.ColorSpace!));

		return new ColorTagStatus(needsFix && isEligible, isEligible,
			video.ColorPrimaries, video.ColorTransfer, video.ColorSpace, video.ColorRange);
	}

	private static bool IsMissing(string? tag)
	{
		return string.IsNullOrWhiteSpace(tag) || tag.Equals("unknown", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsBt709(string tag)
	{
		return tag.Equals("bt709", StringComparison.OrdinalIgnoreCase);
	}
}

public sealed record ColorTagFixResult(ColorTagStatus Before, ColorTagStatus After, string OutputPath);

public static class ColorTagFixer
{
	public static ColorTagStatus Check(string inputPath)
	{
		return ColorTagStatus.From(SourceProbe.Probe(inputPath).Video);
	}

	/// <summary>
	///     Rewrites the container's color tags to Rec.709 via a lossless stream copy (no re-encode) - the
	///     same fix validated by hand while dialing in Vegas render settings for this app's DJI footage.
	///     Always Rec.709: every SDR DJI Osmo source this app targets, and every render this app's own
	///     pipeline produces from one, is Rec.709, so there's no ambiguity to ask the caller to resolve.
	/// </summary>
	public static ColorTagFixResult Fix(string inputPath, string? outputPath = null)
	{
		SourceInfo before = SourceProbe.Probe(inputPath);
		ColorTagStatus beforeStatus = ColorTagStatus.From(before.Video);

		if (!beforeStatus.IsEligible)
			throw new InvalidOperationException(
				"This file already has an explicit color tag that isn't Rec.709 (looks like HDR/wide-gamut " +
				"content) - forcing Rec.709 over it would mislabel the color space instead of fixing it, so " +
				"this tool won't touch it.");

		var resolvedOutput = outputPath ?? DefaultFixedPath(inputPath);

		// hevc_metadata/h264_metadata both take the same field names; colour_primaries=1,
		// transfer_characteristics=1, matrix_coefficients=1 are the ISO/IEC 23091-4 codes for BT.709.
		var metadataBsf = before.Video.CodecName switch
		{
			"hevc" => "hevc_metadata",
			"h264" => "h264_metadata",
			_ => throw new NotSupportedException(
				$"Color tag fix only supports HEVC and H.264 streams (this file is '{before.Video.CodecName}').")
		};

		ProcessStartInfo psi = ProcessHelper.CreateHidden("ffmpeg",
			"-y", "-i", inputPath,
			"-c", "copy",
			"-bsf:v", $"{metadataBsf}=colour_primaries=1:transfer_characteristics=1:matrix_coefficients=1",
			"-color_primaries", "bt709",
			"-color_trc", "bt709",
			"-colorspace", "bt709",
			resolvedOutput);

		var (exitCode, stdout, stderr) = ProcessHelper.RunCaptured(psi);
		if (exitCode != 0)
			throw new InvalidOperationException($"ffmpeg exited with an error ({exitCode}): {stderr}{stdout}");

		SourceInfo after = SourceProbe.Probe(resolvedOutput);
		return new ColorTagFixResult(beforeStatus, ColorTagStatus.From(after.Video), resolvedOutput);
	}

	private static string DefaultFixedPath(string inputPath)
	{
		var dir = Path.GetDirectoryName(inputPath) ?? "";
		var name = Path.GetFileNameWithoutExtension(inputPath);
		var ext = Path.GetExtension(inputPath);
		return Path.Combine(dir, $"{name}_fixed{ext}");
	}
}
