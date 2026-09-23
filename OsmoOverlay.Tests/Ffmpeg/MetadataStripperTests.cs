using System.Text.Json.Nodes;
using OsmoOverlay.Core.Ffmpeg;

namespace OsmoOverlay.Tests.Ffmpeg;

/// <summary>Verify on hand-made ffprobe JSON shaped like an Osmo Action original and a clean copy of it.</summary>
[TestClass]
public sealed class MetadataStripperTests
{
	private static JsonObject Video()
	{
		return new JsonObject
		{
			["codec_type"] = "video", ["codec_name"] = "hevc", ["profile"] = "Main 10", ["level"] = 156, ["width"] = 3840,
			["height"] = 2160, ["pix_fmt"] = "yuv420p10le", ["color_primaries"] = "bt709", ["color_transfer"] = "bt709",
			["color_space"] = "bt709", ["color_range"] = "tv", ["nb_frames"] = "12763",
			["disposition"] = new JsonObject { ["attached_pic"] = 0 },
			["side_data_list"] = new JsonArray(new JsonObject { ["side_data_type"] = "Display Matrix", ["rotation"] = -90 })
		};
	}

	private static JsonObject Audio()
	{
		return new JsonObject
		{
			["codec_type"] = "audio", ["codec_name"] = "aac", ["profile"] = "LC", ["sample_rate"] = "48000", ["channels"] = 2,
			["nb_frames"] = "9981", ["disposition"] = new JsonObject { ["attached_pic"] = 0 }
		};
	}

	private static JsonObject Tagged(JsonObject stream, params (string Key, string Value)[] tags)
	{
		var t = new JsonObject();
		foreach (var (k, v) in tags) t[k] = v;
		stream["tags"] = t;
		return stream;
	}

	private static JsonObject Source()
	{
		return new JsonObject
		{
			["streams"] = new JsonArray(
				Tagged(Video(), ("creation_time", "2026-09-16T08:06:34Z"), ("timecode", "10:06:31;10"), ("handler_name", "DJI.AVC")),
				Tagged(Audio(), ("creation_time", "2026-09-16T08:06:34Z")),
				new JsonObject { ["codec_type"] = "data", ["codec_tag_string"] = "djmd" },
				new JsonObject { ["codec_type"] = "data", ["codec_tag_string"] = "dbgi" },
				new JsonObject { ["codec_type"] = "data", ["codec_tag_string"] = "tmcd" },
				new JsonObject
				{
					["codec_type"] = "video", ["codec_name"] = "mjpeg", ["disposition"] = new JsonObject { ["attached_pic"] = 1 }
				}),
			["format"] = new JsonObject
			{
				["tags"] = new JsonObject { ["major_brand"] = "isom", ["creation_time"] = "2026-09-16T08:06:34Z", ["encoder"] = "DJI AC006" }
			}
		};
	}

	private static JsonObject Clean()
	{
		return new JsonObject
		{
			["streams"] = new JsonArray(
				Tagged(Video(), ("language", "und"), ("handler_name", "VideoHandler")),
				Tagged(Audio(), ("language", "und"), ("handler_name", "SoundHandler"))),
			["format"] = new JsonObject
			{
				["tags"] = new JsonObject { ["major_brand"] = "isom", ["minor_version"] = "512", ["compatible_brands"] = "isomiso2mp41" }
			}
		};
	}

	[TestMethod]
	public void CleanCopy_Passes()
	{
		MetadataStripper.Verify(Source(), Clean());
	}

	[TestMethod]
	public void LeftoverTelemetryTrack_Fails()
	{
		JsonObject output = Clean();
		output["streams"]!.AsArray().Add(new JsonObject { ["codec_type"] = "data", ["codec_tag_string"] = "djmd" });

		AssertRejected(output, "data, timecode or thumbnail");
	}

	[TestMethod]
	public void LeftoverThumbnail_Fails()
	{
		JsonObject output = Clean();
		output["streams"]!.AsArray().Add(new JsonObject
		{
			["codec_type"] = "video", ["codec_name"] = "mjpeg", ["disposition"] = new JsonObject { ["attached_pic"] = 1 }
		});

		AssertRejected(output, "thumbnail");
	}

	[TestMethod]
	[DataRow("creation_time")]
	[DataRow("encoder")]
	[DataRow("location")]
	public void LeftoverContainerTag_Fails(string tag)
	{
		JsonObject output = Clean();
		output["format"]!["tags"]![tag] = "x";

		AssertRejected(output, tag);
	}

	[TestMethod]
	public void LeftoverStreamTag_Fails()
	{
		JsonObject output = Clean();
		output["streams"]![0]!["tags"]!["timecode"] = "10:06:31;10";

		AssertRejected(output, "timecode");
	}

	[TestMethod]
	[DataRow("color_primaries", "bt2020")]
	[DataRow("color_transfer", "arib-std-b67")]
	[DataRow("color_range", "pc")]
	[DataRow("profile", "Main")]
	[DataRow("nb_frames", "12762")]
	public void ChangedVideoProperty_Fails(string key, string value)
	{
		JsonObject output = Clean();
		output["streams"]![0]![key] = value;

		AssertRejected(output, key);
	}

	[TestMethod]
	public void ChangedRotation_Fails()
	{
		JsonObject output = Clean();
		output["streams"]![0]!["side_data_list"] = new JsonArray();

		AssertRejected(output, "rotation");
	}

	[TestMethod]
	public void MissingAudio_Fails()
	{
		JsonObject output = Clean();
		output["streams"]!.AsArray().RemoveAt(1);

		AssertRejected(output, "expected 2");
	}

	private static void AssertRejected(JsonObject output, string reasonFragment)
	{
		var ex = Assert.ThrowsExactly<InvalidOperationException>(() => MetadataStripper.Verify(Source(), output));
		StringAssert.Contains(ex.Message, reasonFragment);
	}
}
