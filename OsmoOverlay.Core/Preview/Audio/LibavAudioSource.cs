using FFmpeg.AutoGen;
using OsmoOverlay.Core.Localization;

namespace OsmoOverlay.Core.Preview;

/// <summary>
///     The recording's audio for preview playback: decoded in-process like the video (LibavStreamDecoder, its own
///     demuxer per file), converted to interleaved 32-bit float at the track's own rate and channel count - the audio
///     device's stream converts from there. Sample-accurate: a seek drops the samples before the target and a read
///     stops exactly at the given end, so the kept stretches of a cut recording join the way the render joins them.
///     Thread-safe; after Dispose every read returns nothing.
/// </summary>
public sealed unsafe class LibavAudioSource : IDisposable
{
	private readonly Lock _lock = new();
	private readonly List<(string Path, double StartSeconds)> _segments;
	private readonly Dictionary<int, LibavStreamDecoder> _decoders = [];
	private SwrContext* _resampler;
	private readonly bool _converts;
	private readonly bool _probe;
	private float[] _buffer = [];
	private int _segment;
	// Samples starting before this time are dropped (the part of the first frame after a seek before its target).
	private double _skipUntil;
	private bool _disposed;

	private LibavAudioSource(List<(string Path, double StartSeconds)> segments, LibavStreamDecoder first, int? sampleRate, int? channels, bool probeStreams)
	{
		_segments = segments;
		_decoders[0] = first;
		SampleRate = sampleRate ?? first.Codec->sample_rate;
		Channels = channels ?? first.Codec->ch_layout.nb_channels;
		_converts = sampleRate is not null || channels is not null;
		_probe = probeStreams;
	}

	public int SampleRate { get; }
	public int Channels { get; }

	/// <summary>
	///     Null when the recording has no audio track. With a rate and a channel count the samples come out in those instead
	///     of the track's own (music joining the recording's sound).
	/// </summary>
	public static LibavAudioSource? TryOpen(IReadOnlyList<PlaybackSegment> segments, int? sampleRate = null, int? channels = null,
		bool probeStreams = false)
	{
		if (LibavLoader.TryLoad() is { } failure) throw new InvalidOperationException(string.Format(CoreStrings.Libav_CantDecodeAudio, failure));

		List<(string Path, double StartSeconds)> withOffsets = [];
		double offset = 0;
		foreach (PlaybackSegment segment in segments)
		{
			withOffsets.Add((segment.Path, offset));
			offset += segment.DurationSeconds;
		}

		var first = LibavStreamDecoder.Open(segments[0].Path, AVMediaType.AVMEDIA_TYPE_AUDIO, false, 0, probeStreams || sampleRate is not null);
		return first is null ? null : new LibavAudioSource(withOffsets, first, sampleRate, channels, probeStreams || sampleRate is not null);
	}

	/// <summary>Positions the decoder so the next Read starts exactly at this recording time.</summary>
	public void Seek(double seconds)
	{
		lock (_lock)
		{
			if (_disposed) return;

			_segment = 0;
			for (int i = _segments.Count - 1; i > 0; i--)
			{
				if (seconds >= _segments[i].StartSeconds)
				{
					_segment = i;
					break;
				}
			}

			DecoderFor(_segment)?.Seek(Math.Max(0, seconds - _segments[_segment].StartSeconds));
			_skipUntil = seconds;
		}
	}

	/// <summary>
	///     The next decoded samples (interleaved, Channels per sample frame) that start before endSeconds, cut off at
	///     it; empty once endSeconds or the end of the recording is reached. The span is valid until the next call.
	/// </summary>
	public ReadOnlySpan<float> Read(double endSeconds)
	{
		lock (_lock)
		{
			while (!_disposed && DecoderFor(_segment) is { } decoder)
			{
				if (!decoder.Receive())
				{
					if (_segment == _segments.Count - 1) return [];

					// The next file continues where this one really ended.
					_segment++;
					DecoderFor(_segment)?.Seek(0);
					continue;
				}

				AVFrame* frame = decoder.Frame;
				double start = _segments[_segment].StartSeconds + decoder.FrameSeconds();
				if (start >= endSeconds) return [];

				int count = Convert(frame);
				int skip = (int)Math.Clamp(Math.Round((_skipUntil - start) * SampleRate), 0, count);
				int keep = (int)Math.Clamp(Math.Round((endSeconds - start) * SampleRate), 0, count);
				if (keep <= skip) continue;

				return _buffer.AsSpan(skip * Channels, (keep - skip) * Channels);
			}

			return [];
		}
	}

	/// <summary>The frame into _buffer as interleaved float; returns the sample frames written.</summary>
	private int Convert(AVFrame* frame)
	{
		if (_resampler is null)
		{
			SwrContext* resampler = null;
			AVChannelLayout layout = frame->ch_layout;
			AVChannelLayout outLayout = layout;
			if (_converts) ffmpeg.av_channel_layout_default(&outLayout, Channels);
			LibavStreamDecoder.Check(ffmpeg.swr_alloc_set_opts2(&resampler, &outLayout, AVSampleFormat.AV_SAMPLE_FMT_FLT, _converts ? SampleRate : frame->sample_rate,
				&layout, (AVSampleFormat)frame->format, frame->sample_rate, 0, null), "set up the audio conversion");
			LibavStreamDecoder.Check(ffmpeg.swr_init(resampler), "set up the audio conversion");
			_resampler = resampler;
		}

		int capacity = _converts ? Math.Min(int.MaxValue / Channels, ffmpeg.swr_get_out_samples(_resampler, frame->nb_samples)) : frame->nb_samples;
		int needed = capacity * Channels;
		if (_buffer.Length < needed) _buffer = new float[needed];

		fixed (float* output = _buffer)
		{
			byte** outputs = stackalloc byte*[1];
			outputs[0] = (byte*)output;
			int converted = ffmpeg.swr_convert(_resampler, outputs, capacity, frame->extended_data, frame->nb_samples);
			LibavStreamDecoder.Check(converted, "convert the audio");
			return converted;
		}
	}

	private LibavStreamDecoder? DecoderFor(int segment)
	{
		if (_decoders.TryGetValue(segment, out LibavStreamDecoder? decoder)) return decoder;

		decoder = LibavStreamDecoder.Open(_segments[segment].Path, AVMediaType.AVMEDIA_TYPE_AUDIO, false, 0, _probe);
		if (decoder is not null) _decoders[segment] = decoder;
		return decoder;
	}

	public void Dispose()
	{
		lock (_lock)
		{
			if (_disposed) return;

			_disposed = true;
			foreach (LibavStreamDecoder decoder in _decoders.Values) decoder.Dispose();
			_decoders.Clear();
			SwrContext* resampler = _resampler;
			ffmpeg.swr_free(&resampler);
			_resampler = null;
		}
	}
}
