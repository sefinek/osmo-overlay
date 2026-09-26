using System.Globalization;
using FFmpeg.AutoGen;

namespace OsmoOverlay.Core.Preview;

/// <summary>
///     Plays audio faster or slower without changing its pitch - FFmpeg's atempo filter (libavfilter, already loaded
///     for the preview), fed interleaved float samples and handing them back the same way. atempo covers 0.5x-100x in
///     one stage, so slower rates chain stages (FilterFor). Not thread-safe - one feeder uses it.
/// </summary>
public sealed unsafe class AudioTempo : IDisposable
{
	private readonly AVFilterGraph* _graph;
	private readonly AVFilterContext* _source;
	private readonly AVFilterContext* _sink;
	private readonly AVFrame* _input;
	private readonly AVFrame* _output;
	private readonly int _channels;
	private readonly int _sampleRate;
	private float[] _buffer = [];
	private long _pts;

	public AudioTempo(double rate, int sampleRate, int channels)
	{
		if (LibavLoader.TryLoad() is { } failure) throw new InvalidOperationException($"Audio tempo unavailable: {failure}");

		_sampleRate = sampleRate;
		_channels = channels;
		_graph = ffmpeg.avfilter_graph_alloc();
		_input = ffmpeg.av_frame_alloc();
		_output = ffmpeg.av_frame_alloc();
		try
		{
			AVFilterContext* source = null;
			AVFilterContext* sink = null;
			var layout = ChannelLayoutName(channels);
			LibavStreamDecoder.Check(ffmpeg.avfilter_graph_create_filter(&source, ffmpeg.avfilter_get_by_name("abuffer"), "in",
				$"time_base=1/{sampleRate}:sample_rate={sampleRate}:sample_fmt=flt:channel_layout={layout}", null, _graph), "set up the audio tempo");
			LibavStreamDecoder.Check(ffmpeg.avfilter_graph_create_filter(&sink, ffmpeg.avfilter_get_by_name("abuffersink"), "out",
				null, null, _graph), "set up the audio tempo");
			_source = source;
			_sink = sink;

			AVFilterInOut* outputs = ffmpeg.avfilter_inout_alloc();
			AVFilterInOut* inputs = ffmpeg.avfilter_inout_alloc();
			outputs->name = ffmpeg.av_strdup("in");
			outputs->filter_ctx = source;
			inputs->name = ffmpeg.av_strdup("out");
			inputs->filter_ctx = sink;
			try
			{
				// aformat keeps the output interleaved float at the input's layout, whatever atempo would prefer.
				LibavStreamDecoder.Check(ffmpeg.avfilter_graph_parse_ptr(_graph,
					$"{FilterFor(rate)},aformat=sample_fmts=flt:channel_layouts={layout}", &inputs, &outputs, null), "set up the audio tempo");
				LibavStreamDecoder.Check(ffmpeg.avfilter_graph_config(_graph, null), "set up the audio tempo");
			}
			finally
			{
				ffmpeg.avfilter_inout_free(&inputs);
				ffmpeg.avfilter_inout_free(&outputs);
			}
		}
		catch
		{
			Dispose();
			throw;
		}
	}

	/// <summary>"atempo=2" - or, below atempo's 0.5x floor, stages multiplying to the rate ("atempo=0.5,atempo=0.5" for 0.25x).</summary>
	public static string FilterFor(double rate)
	{
		if (rate <= 0) throw new ArgumentOutOfRangeException(nameof(rate), rate, "The rate must be positive.");

		List<string> stages = [];
		while (rate < 0.5)
		{
			stages.Add("atempo=0.5");
			rate /= 0.5;
		}

		stages.Add($"atempo={rate.ToString("0.######", CultureInfo.InvariantCulture)}");
		return string.Join(',', stages);
	}

	/// <summary>Feeds interleaved samples in and returns what the filter has ready (possibly nothing yet) - valid until the next call.</summary>
	public ReadOnlySpan<float> Process(ReadOnlySpan<float> samples)
	{
		var sampleFrames = samples.Length / _channels;
		if (sampleFrames == 0) return [];

		ffmpeg.av_frame_unref(_input);
		_input->nb_samples = sampleFrames;
		_input->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLT;
		_input->sample_rate = _sampleRate;
		_input->pts = _pts;
		ffmpeg.av_channel_layout_default(&_input->ch_layout, _channels);
		LibavStreamDecoder.Check(ffmpeg.av_frame_get_buffer(_input, 0), "buffer the audio");
		samples[..(sampleFrames * _channels)].CopyTo(new Span<float>(_input->data[0], sampleFrames * _channels));
		_pts += sampleFrames;
		LibavStreamDecoder.Check(ffmpeg.av_buffersrc_add_frame_flags(_source, _input, 0), "change the audio tempo");

		var count = 0;
		while (ffmpeg.av_buffersink_get_frame(_sink, _output) >= 0)
		{
			var produced = _output->nb_samples * _channels;
			if (_buffer.Length < count + produced) Array.Resize(ref _buffer, Math.Max(count + produced, _buffer.Length * 2));
			new ReadOnlySpan<float>(_output->data[0], produced).CopyTo(_buffer.AsSpan(count));
			count += produced;
			ffmpeg.av_frame_unref(_output);
		}

		return _buffer.AsSpan(0, count);
	}

	private static string ChannelLayoutName(int channels)
	{
		AVChannelLayout layout;
		ffmpeg.av_channel_layout_default(&layout, channels);
		const int size = 64;
		var name = stackalloc byte[size];
		ffmpeg.av_channel_layout_describe(&layout, name, size);
		ffmpeg.av_channel_layout_uninit(&layout);
		return new string((sbyte*)name);
	}

	public void Dispose()
	{
		AVFilterGraph* graph = _graph;
		AVFrame* input = _input;
		AVFrame* output = _output;
		ffmpeg.avfilter_graph_free(&graph);
		ffmpeg.av_frame_free(&input);
		ffmpeg.av_frame_free(&output);
	}
}
