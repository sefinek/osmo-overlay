using FFmpeg.AutoGen;

namespace OsmoOverlay.Core.Preview;

/// <summary>
///     One stream of one file, demuxed and decoded - what the preview's video (LibavVideoSource) and audio
///     (LibavAudioSource) decoding share: the file opened once, every other stream discarded, seeks to the keyframe at
///     or before a time, frames decoded on in order. Not thread-safe - each owner serializes its own use.
/// </summary>
internal sealed unsafe class LibavStreamDecoder : IDisposable
{
	private readonly AVFormatContext* _format;
	private readonly AVPacket* _packet;
	private readonly int _stream;
	private readonly double _timeBase;
	private readonly long _startPts;
	// Two frames taking turns, so the last decoded one survives a receive that hits the end of the file.
	private AVFrame* _spare;
	private bool _draining;

	private LibavStreamDecoder(AVFormatContext* format, AVCodecContext* codec, int stream, string? hardware)
	{
		_format = format;
		Codec = codec;
		_stream = stream;
		Hardware = hardware;

		AVStream* avStream = format->streams[stream];
		_timeBase = ffmpeg.av_q2d(avStream->time_base);
		_startPts = avStream->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : avStream->start_time;

		_packet = ffmpeg.av_packet_alloc();
		Frame = ffmpeg.av_frame_alloc();
		_spare = ffmpeg.av_frame_alloc();
	}

	public AVCodecContext* Codec { get; }

	/// <summary>The frame Receive decoded last - valid until the next Receive or Seek.</summary>
	public AVFrame* Frame { get; private set; }

	public bool HasFrame { get; private set; }

	/// <summary>The hardware decoder in use ("d3d11va"...), or null for software.</summary>
	public string? Hardware { get; }

	/// <summary>Null when the file has no stream of that type.</summary>
	public static LibavStreamDecoder? Open(string path, AVMediaType type, bool useHardware)
	{
		AVFormatContext* format = null;
		AVCodecContext* codec = null;
		try
		{
			Check(ffmpeg.avformat_open_input(&format, path, null, null), $"open {path}");

			AVCodec* decoder = null;
			var stream = ffmpeg.av_find_best_stream(format, type, -1, -1, &decoder, 0);
			if (stream == ffmpeg.AVERROR_STREAM_NOT_FOUND)
			{
				ffmpeg.avformat_close_input(&format);
				return null;
			}

			Check(stream, $"find the {ffmpeg.av_get_media_type_string(type)} stream of {path}");
			// Every other track (the camera's djmd/dbgi telemetry, timecode, the other media type) is never needed
			// here - discarded streams aren't handed back by av_read_frame at all.
			for (var i = 0; i < format->nb_streams; i++)
				if (i != stream)
					format->streams[i]->discard = AVDiscard.AVDISCARD_ALL;

			codec = ffmpeg.avcodec_alloc_context3(decoder);
			Check(ffmpeg.avcodec_parameters_to_context(codec, format->streams[stream]->codecpar), "set up the decoder");
			codec->pkt_timebase = format->streams[stream]->time_base;

			var hardware = useHardware ? AttachHardwareDevice(codec, decoder) : null;
			if (hardware is null)
			{
				codec->thread_count = 0;
				codec->thread_type = ffmpeg.FF_THREAD_FRAME | ffmpeg.FF_THREAD_SLICE;
			}

			Check(ffmpeg.avcodec_open2(codec, decoder, null), "open the decoder");
			return new LibavStreamDecoder(format, codec, stream, hardware);
		}
		catch
		{
			if (codec is not null) ffmpeg.avcodec_free_context(&codec);
			if (format is not null) ffmpeg.avformat_close_input(&format);
			throw;
		}
	}

	/// <summary>
	///     The first hardware decoder this platform has that also supports the codec. With hw_device_ctx set,
	///     libavcodec's default get_format picks the matching hardware format itself. CUDA goes first where there
	///     is an NVIDIA GPU: measured on a 4K HEVC 10-bit Osmo file (RTX 4070), decoding plus the download to
	///     the CPU ran at 188 fps through CUDA against 109 through D3D11VA, with bit-identical frames; without an
	///     NVIDIA GPU creating the CUDA device just fails and the next one is tried.
	/// </summary>
	private static string? AttachHardwareDevice(AVCodecContext* codec, AVCodec* decoder)
	{
		AVHWDeviceType[] candidates = OperatingSystem.IsWindows()
			? [AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2]
			: OperatingSystem.IsMacOS()
				? [AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX]
				: [AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA, AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI];

		foreach (AVHWDeviceType type in candidates)
		{
			if (!SupportsDevice(decoder, type)) continue;

			AVBufferRef* device = null;
			if (ffmpeg.av_hwdevice_ctx_create(&device, type, null, null, 0) < 0) continue;

			codec->hw_device_ctx = ffmpeg.av_buffer_ref(device);
			ffmpeg.av_buffer_unref(&device);
			return ffmpeg.av_hwdevice_get_type_name(type);
		}

		return null;
	}

	private static bool SupportsDevice(AVCodec* decoder, AVHWDeviceType type)
	{
		for (var i = 0;; i++)
		{
			AVCodecHWConfig* config = ffmpeg.avcodec_get_hw_config(decoder, i);
			if (config is null) return false;
			if (config->device_type == type && (config->methods & (int)AvCodecHwConfigMethod.AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX) != 0)
				return true;
		}
	}

	/// <summary>To the keyframe at or before the time in this file; the frames from there to it still have to be decoded.</summary>
	public void Seek(double seconds)
	{
		var timestamp = _startPts + (long)Math.Round(seconds / _timeBase);
		Check(ffmpeg.av_seek_frame(_format, _stream, timestamp, ffmpeg.AVSEEK_FLAG_BACKWARD), "seek");
		ffmpeg.avcodec_flush_buffers(Codec);
		_draining = false;
		HasFrame = false;
	}

	/// <summary>Decodes the next frame; false at the end of the file (the previous frame stays available).</summary>
	public bool Receive()
	{
		while (true)
		{
			var result = ffmpeg.avcodec_receive_frame(Codec, _spare);
			if (result == 0)
			{
				AVFrame* decoded = _spare;
				_spare = Frame;
				Frame = decoded;
				HasFrame = true;
				return true;
			}

			if (result == ffmpeg.AVERROR_EOF || _draining) return false;
			if (result != ffmpeg.AVERROR(ffmpeg.EAGAIN)) Check(result, "decode");

			result = ffmpeg.av_read_frame(_format, _packet);
			if (result == ffmpeg.AVERROR_EOF)
			{
				// Draining hands out the frames the decoder still holds, then the end of the file.
				ffmpeg.avcodec_send_packet(Codec, null);
				_draining = true;
				continue;
			}

			Check(result, "read");
			try
			{
				result = ffmpeg.avcodec_send_packet(Codec, _packet);
				// A damaged packet costs its own frame, not the whole preview.
				if (result < 0 && result != ffmpeg.AVERROR(ffmpeg.EAGAIN) && result != ffmpeg.AVERROR_INVALIDDATA) Check(result, "decode");
			}
			finally
			{
				ffmpeg.av_packet_unref(_packet);
			}
		}
	}

	/// <summary>When the current frame starts, in seconds from the start of this file's stream.</summary>
	public double FrameSeconds()
	{
		var pts = Frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE ? Frame->best_effort_timestamp : Frame->pts;
		return (pts - _startPts) * _timeBase;
	}

	public void Dispose()
	{
		AVFrame* frame = Frame;
		AVFrame* spare = _spare;
		AVPacket* packet = _packet;
		AVCodecContext* codec = Codec;
		AVFormatContext* format = _format;

		ffmpeg.av_frame_free(&frame);
		ffmpeg.av_frame_free(&spare);
		ffmpeg.av_packet_free(&packet);
		ffmpeg.avcodec_free_context(&codec);
		ffmpeg.avformat_close_input(&format);
	}

	public static void Check(int result, string action)
	{
		if (result >= 0) return;

		const int size = 256;
		var message = stackalloc byte[size];
		ffmpeg.av_strerror(result, message, size);
		throw new InvalidOperationException($"FFmpeg could not {action}: {new string((sbyte*)message)}");
	}
}
