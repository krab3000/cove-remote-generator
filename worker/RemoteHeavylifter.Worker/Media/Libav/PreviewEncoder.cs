using System.Globalization;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Logging;
using RemoteHeavylifter.Protocol;

namespace RemoteHeavylifter.Worker.Media.Libav;

/// <summary>The encoder (not the source) failed; a hardware encoder is retried with libx264.</summary>
internal sealed class EncodeException(string message) : MediaException(message);

/// <summary>A span of the source that goes into the preview.</summary>
/// <param name="Start">Where it starts (seconds, as <c>-ss</c>); null decodes from the beginning without seeking.</param>
/// <param name="Duration">How long (seconds, as <c>-t</c>); null runs to the end of the video.</param>
internal sealed record PreviewSegment(double? Start, double? Duration);

/// <summary>
/// Encodes a preview in this process: the segments' frames go through the command line's filter chain into one H.264
/// encoder with continuous timestamps, plus AAC audio when the preview has sound, muxed into one MP4. This replaces
/// filter_complex concat (spliced), per-chunk files plus the concat demuxer (chunks), and the single encode.
/// </summary>
internal static unsafe class PreviewEncoder
{
    private const string What = "preview";

    public static IReadOnlyList<PreviewSegment> Segments(PreviewPlan plan, double duration) => plan.Mode switch
    {
        PreviewMode.Single => [new(plan.UsableStart > 0 ? plan.UsableStart : null, plan.UsableDuration < duration ? plan.UsableDuration : null)],
        _ => plan.SeekTimes.Select(seek => new PreviewSegment(seek, plan.SegmentDuration)).ToList(),
    };

    public static void Encode(
        MediaSource source, double duration, PreviewPlan plan, PreviewSpec spec, string encoder, string output,
        DecodeSettings settings, Interrupt interrupt, ILogger log)
    {
        var setup = EncoderOptions.For(encoder, spec.Crf, spec.Preset);
        var codec = ffmpeg.avcodec_find_encoder_by_name(setup.Codec);
        if (codec is null)
            throw new EncodeException($"preview: no {setup.Codec} encoder");
        var upload = Upload(codec, setup, encoder);

        using var input = LibavInput.Open(source, settings, interrupt, log, What);
        using var audio = spec.Audio && plan.Mode != PreviewMode.Spliced ? AudioTrack.Open(source.Software, interrupt, log) : null;
        using var writer = new PreviewWriter(output, interrupt);
        AVBufferRef* uploadDevice = null;
        var filtered = ffmpeg.av_frame_alloc();
        try
        {
            if (encoder.EndsWith("_vaapi", StringComparison.Ordinal))
            {
                AVBufferRef* device = null;
                var ret = ffmpeg.av_hwdevice_ctx_create(&device, AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI, "/dev/dri/renderD128", null, 0);
                if (ret < 0)
                    throw new EncodeException($"preview: vaapi device: {Av.Error(ret)}");
                uploadDevice = device;
            }
            if (audio is not null)
                writer.AddAudio(audio);

            CfrClock? clock = null;
            foreach (var segment in Segments(plan, duration))
            {
                try
                {
                    var frames = EncodeSegment(segment);
                    if (audio is not null && frames > 0)
                        audio.WriteSegment(segment.Start ?? 0, frames / clock!.Fps, writer);
                }
                catch (MediaException ex) when (plan.Mode == PreviewMode.Chunks && ex is not (EncodeException or HardwareDecodeException))
                {
                    // Like a failed chunk on the command line: the other segments still make a preview.
                    log.LogDebug("preview segment at {Start} s failed: {Error}", segment.Start, ex.Message);
                }
            }
            if (clock is null || clock.Next == 0)
                throw new MediaException(plan.Mode == PreviewMode.Chunks ? "preview: no usable chunks" : "preview: no frames");
            audio?.Flush(writer);
            writer.Finish();

            // One segment's frames, from its start to its end, into the encoder; returns how many were written.
            long EncodeSegment(PreviewSegment segment)
            {
                var startUs = segment.Start is { } start ? SeekPlanner.TargetMicroseconds(start, 2, input.StartMicroseconds) : (long?)null;
                long? endUs = segment.Duration is { } length
                    ? (startUs ?? input.StartMicroseconds) + (long)Math.Round(double.Parse(Timing.Fixed(length, 2), CultureInfo.InvariantCulture) * 1_000_000)
                    : null;
                var frame = startUs is { } s ? input.FrameAt(s, What) : input.NextFrame(What);
                if (frame is null)
                    return 0;

                var chain = FilterChain.Join(
                    input.SoftwareFormatFilter(frame), AutoRotate.Filters(input.DisplayMatrix(frame)), PreviewGenerator.ScaleFilter(spec),
                    plan.Mode == PreviewMode.Spliced ? "setsar=1" : null, upload);
                using var filters = FilterChain.Create(frame, input.TimeBase, chain, 0, What, GuessFrameRate(input), uploadDevice);
                clock ??= new CfrClock(OutputRate(filters, input));
                clock.StartSegment();
                // The command line keeps a frame landing exactly on the end (-ss 1.25 -t 0.75 keeps the one at 2.00).
                var endPts = endUs is { } e ? input.ToStream(e) : long.MaxValue;
                while (frame is not null && (frame->pts == ffmpeg.AV_NOPTS_VALUE || frame->pts <= endPts))
                {
                    filters.Push(frame, What);
                    Drain(filters);
                    frame = input.NextFrame(What);
                }
                filters.Close(What);
                Drain(filters);
                return clock.SegmentFrames;
            }

            void Drain(FilterChain graph)
            {
                while (graph.Pull(filtered, What))
                {
                    if (!writer.VideoOpen)
                        writer.OpenVideo(codec, setup, filtered, clock!.Rate, graph.OutputHwFrames);
                    var seconds = filtered->pts == ffmpeg.AV_NOPTS_VALUE ? 0 : Av.Seconds(filtered->pts, graph.OutputTimeBase);
                    var slot = clock!.Next;
                    for (var copies = clock.Place(seconds); copies > 0; copies--)
                        writer.WriteVideo(filtered, slot++);
                    ffmpeg.av_frame_unref(filtered);
                }
            }
        }
        finally
        {
            var frameToFree = filtered;
            ffmpeg.av_frame_free(&frameToFree);
            var deviceToFree = uploadDevice;
            ffmpeg.av_buffer_unref(&deviceToFree);
        }
    }

    /// <summary>The command line's upload chain (<c>format=yuv420p</c>, or nv12 + hwupload for vaapi), with the pixel
    /// format changed to one the encoder takes when it does not take that one (fftools converts implicitly).</summary>
    private static string Upload(AVCodec* codec, EncoderSetup setup, string encoder)
    {
        var chain = EncoderArgs.UploadChain(encoder).TrimStart(',');
        if (encoder.EndsWith("_vaapi", StringComparison.Ordinal))
            return chain;
        var wanted = setup.PixelFormat ?? chain["format=".Length..];
        var supported = Supported<AVPixelFormat>(codec, AVCodecConfig.AV_CODEC_CONFIG_PIX_FORMAT)
            .Select(f => ffmpeg.av_get_pix_fmt_name(f)).Where(n => n is not null).ToList();
        return supported.Count == 0 || supported.Contains(wanted) ? $"format={wanted}" : $"format={supported[0]}";
    }

    /// <summary>What an encoder accepts (pixel formats, sample rates …); empty when it accepts anything.</summary>
    internal static List<T> Supported<T>(AVCodec* codec, AVCodecConfig config) where T : unmanaged
    {
        void* values = null;
        var count = 0;
        var list = new List<T>();
        if (ffmpeg.avcodec_get_supported_config(null, codec, config, 0, &values, &count) < 0 || values is null)
            return list;
        for (var i = 0; i < count; i++)
            list.Add(((T*)values)[i]);
        return list;
    }

    /// <summary>fftools' input frame rate: libav's guess from the container and stream.</summary>
    private static AVRational GuessFrameRate(LibavInput input) => ffmpeg.av_guess_frame_rate(input.Format, input.Stream, null);

    private static AVRational OutputRate(FilterChain filters, LibavInput input)
    {
        var rate = filters.OutputFrameRate;
        if (rate.num <= 0 || rate.den <= 0)
            rate = GuessFrameRate(input);
        return rate.num > 0 && rate.den > 0 ? rate : new AVRational { num = 25, den = 1 };
    }
}

/// <summary>The MP4 a preview is written to: one H.264 stream, optionally one AAC stream.</summary>
internal sealed unsafe class PreviewWriter : IDisposable
{
    private readonly string _path;
    private readonly Interrupt _interrupt;
    private AVFormatContext* _output;
    private AVCodecContext* _video;
    private AVStream* _videoStream;
    private AVCodecContext* _audio;
    private AVStream* _audioStream;
    private AVPacket* _packet;
    private bool _finished;

    public PreviewWriter(string path, Interrupt interrupt)
    {
        _path = path;
        _interrupt = interrupt;
        AVFormatContext* output = null;
        ffmpeg.avformat_alloc_output_context2(&output, null, "mp4", path).Check("preview: mp4 muxer");
        _output = output;
        _packet = ffmpeg.av_packet_alloc();
        // Video is stream 0, as on the command line; its parameters come when the encoder opens on the first frame.
        _videoStream = ffmpeg.avformat_new_stream(_output, null);
    }

    public bool VideoOpen => _video is not null;

    public AVCodecContext* AudioEncoder => _audio;

    private bool GlobalHeader => (_output->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0;

    /// <summary>The AAC stream (added before the video, so the header is written once both exist).</summary>
    public void AddAudio(AudioTrack track)
    {
        var codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_AAC);
        if (codec is null)
            throw new EncodeException("preview: no aac encoder");
        _audio = ffmpeg.avcodec_alloc_context3(codec);
        _audio->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        _audio->sample_rate = track.SampleRate;
        ffmpeg.av_channel_layout_copy(&_audio->ch_layout, track.Layout).Check("preview: audio layout");
        _audio->time_base = new AVRational { num = 1, den = track.SampleRate };
        if (GlobalHeader)
            _audio->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
        var ret = ffmpeg.avcodec_open2(_audio, codec, null);
        if (ret < 0)
            throw new EncodeException($"preview: aac encoder: {Av.Error(ret)}");
        _audioStream = ffmpeg.avformat_new_stream(_output, null);
        ffmpeg.avcodec_parameters_from_context(_audioStream->codecpar, _audio).Check("preview: audio stream");
        _audioStream->time_base = _audio->time_base;
    }

    /// <summary>Opens the video encoder for frames like <paramref name="first"/> at <paramref name="rate"/>, then the file.</summary>
    public void OpenVideo(AVCodec* codec, EncoderSetup setup, AVFrame* first, AVRational rate, AVBufferRef* hwFrames)
    {
        _video = ffmpeg.avcodec_alloc_context3(codec);
        _video->width = first->width;
        _video->height = first->height;
        _video->sample_aspect_ratio = first->sample_aspect_ratio;
        _video->pix_fmt = (AVPixelFormat)first->format;
        _video->color_range = first->color_range;
        _video->colorspace = first->colorspace;
        _video->color_primaries = first->color_primaries;
        _video->color_trc = first->color_trc;
        _video->framerate = rate;
        _video->time_base = ffmpeg.av_inv_q(rate);
        if (hwFrames is not null)
            _video->hw_frames_ctx = ffmpeg.av_buffer_ref(hwFrames);
        if (setup.QScale is { } q)
        {
            _video->flags |= ffmpeg.AV_CODEC_FLAG_QSCALE;
            _video->global_quality = q * ffmpeg.FF_QP2LAMBDA;
        }
        if (GlobalHeader)
            _video->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

        var options = Av.Dictionary(setup.Options);
        var ret = ffmpeg.avcodec_open2(_video, codec, &options);
        Av.Free(options);
        if (ret < 0)
            throw new EncodeException($"preview: cannot open {setup.Codec}: {Av.Error(ret)}");

        ffmpeg.avcodec_parameters_from_context(_videoStream->codecpar, _video).Check("preview: video stream");
        _videoStream->time_base = _video->time_base;
        _videoStream->avg_frame_rate = rate;

        if ((_output->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
        {
            AVIOContext* io = null;
            ffmpeg.avio_open(&io, _path, ffmpeg.AVIO_FLAG_WRITE).Check("preview: cannot create the file");
            _output->pb = io;
        }
        ffmpeg.avformat_write_header(_output, null).Check("preview: mp4 header");
    }

    public void WriteVideo(AVFrame* frame, long slot)
    {
        frame->pts = slot;
        frame->pict_type = AVPictureType.AV_PICTURE_TYPE_NONE;
        Send(_video, _videoStream, frame, "video");
    }

    /// <summary>One AAC frame; <c>pts</c> counts samples.</summary>
    public void WriteAudio(AVFrame* frame) => Send(_audio, _audioStream, frame, "audio");

    private void Send(AVCodecContext* encoder, AVStream* stream, AVFrame* frame, string kind)
    {
        _interrupt.ThrowIfStopped();
        var ret = ffmpeg.avcodec_send_frame(encoder, frame);
        if (ret < 0 && ret != Av.Eof)
            throw new EncodeException($"preview: {kind} encoding: {Av.Error(ret)}");
        while (true)
        {
            ret = ffmpeg.avcodec_receive_packet(encoder, _packet);
            if (ret == Av.Again || ret == Av.Eof)
                return;
            if (ret < 0)
                throw new EncodeException($"preview: {kind} encoding: {Av.Error(ret)}");
            ffmpeg.av_packet_rescale_ts(_packet, encoder->time_base, stream->time_base);
            _packet->stream_index = stream->index;
            ffmpeg.av_interleaved_write_frame(_output, _packet).Check("preview: writing the file", _interrupt);
        }
    }

    /// <summary>Flushes the encoders and writes the MP4 index.</summary>
    public void Finish()
    {
        if (_video is null)
            throw new MediaException("preview: no frames");
        Send(_video, _videoStream, null, "video");
        if (_audio is not null)
            Send(_audio, _audioStream, null, "audio");
        ffmpeg.av_write_trailer(_output).Check("preview: finishing the file");
        _finished = true;
    }

    public void Dispose()
    {
        var packet = _packet;
        ffmpeg.av_packet_free(&packet);
        _packet = null;
        var video = _video;
        ffmpeg.avcodec_free_context(&video);
        _video = null;
        var audio = _audio;
        ffmpeg.avcodec_free_context(&audio);
        _audio = null;
        if (_output is not null)
        {
            if (_output->pb is not null)
                ffmpeg.avio_closep(&_output->pb);
            ffmpeg.avformat_free_context(_output);
            _output = null;
        }
        if (!_finished)
            Outputs.RemoveQuietly(_path);
    }
}

/// <summary>
/// The source's audio for a preview with sound: per segment, the samples from the segment's start for as long as its
/// video lasts (padded with silence or cut), resampled for AAC and encoded with continuous timestamps.
/// </summary>
internal sealed unsafe class AudioTrack : IDisposable
{
    private const string What = "preview audio";

    private readonly Interrupt _interrupt;
    private readonly AVChannelLayout* _layout;
    private AVFormatContext* _fmt;
    private AVCodecContext* _dec;
    private AVStream* _stream;
    private SwrContext* _swr;
    private AVAudioFifo* _pending;
    private AVAudioFifo* _segment;
    private AVPacket* _packet;
    private AVFrame* _decoded;
    private AVFrame* _resampled;
    private AVFrame* _chunk;
    private long _samplesWritten;
    private bool _disposed;

    private AudioTrack(Interrupt interrupt)
    {
        _interrupt = interrupt;
        // Native memory: libav keeps pointers into the layout, which a managed object could not guarantee.
        _layout = (AVChannelLayout*)NativeMemory.AllocZeroed((nuint)sizeof(AVChannelLayout));
    }

    public int SampleRate { get; private set; }

    public AVChannelLayout* Layout => _layout;

    /// <summary>The source's main audio stream; null when it has none (the preview is then silent, as on the command line).</summary>
    public static AudioTrack? Open(MediaSource source, Interrupt interrupt, ILogger log)
    {
        var track = new AudioTrack(interrupt);
        try
        {
            if (track.OpenCore(source))
                return track;
            log.LogDebug("{What}: the source has no audio", What);
            track.Dispose();
            return null;
        }
        catch
        {
            track.Dispose();
            throw;
        }
    }

    private bool OpenCore(MediaSource source)
    {
        _fmt = ffmpeg.avformat_alloc_context();
        _fmt->interrupt_callback = _interrupt.Native;
        var options = Av.Dictionary(source.ProtocolOptions());
        var fmt = _fmt;
        var ret = ffmpeg.avformat_open_input(&fmt, source.Url, null, &options);
        _fmt = fmt;
        Av.Free(options);
        ret.Check($"{What}: cannot open the source", _interrupt);
        ffmpeg.avformat_find_stream_info(_fmt, null).Check($"{What}: stream info", _interrupt);

        AVCodec* codec = null;
        var index = ffmpeg.av_find_best_stream(_fmt, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, &codec, 0);
        if (index < 0)
            return false;
        _stream = _fmt->streams[index];
        for (var i = 0; i < (int)_fmt->nb_streams; i++)
        {
            if (i != index)
                _fmt->streams[i]->discard = AVDiscard.AVDISCARD_ALL;
        }
        _dec = ffmpeg.avcodec_alloc_context3(codec);
        ffmpeg.avcodec_parameters_to_context(_dec, _stream->codecpar).Check($"{What}: decoder parameters");
        _dec->pkt_timebase = _stream->time_base;
        ffmpeg.avcodec_open2(_dec, codec, null).Check($"{What}: decoder");

        // The source's rate and layout when AAC takes them (as the command line keeps them), else 48 kHz / stereo.
        var aac = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_AAC);
        var rates = PreviewEncoder.Supported<int>(aac, AVCodecConfig.AV_CODEC_CONFIG_SAMPLE_RATE);
        SampleRate = _dec->sample_rate > 0 && (rates.Count == 0 || rates.Contains(_dec->sample_rate)) ? _dec->sample_rate : 48000;
        if (_dec->ch_layout.nb_channels is > 0 and <= 8)
            ffmpeg.av_channel_layout_copy(_layout, &_dec->ch_layout).Check($"{What}: channel layout");
        else
            ffmpeg.av_channel_layout_default(_layout, 2);

        SwrContext* swr = null;
        ffmpeg.swr_alloc_set_opts2(&swr, _layout, AVSampleFormat.AV_SAMPLE_FMT_FLTP, SampleRate,
            &_dec->ch_layout, _dec->sample_fmt, _dec->sample_rate, 0, null).Check($"{What}: resampler");
        _swr = swr;
        ffmpeg.swr_init(_swr).Check($"{What}: resampler");
        _pending = ffmpeg.av_audio_fifo_alloc(AVSampleFormat.AV_SAMPLE_FMT_FLTP, _layout->nb_channels, 1024);
        _segment = ffmpeg.av_audio_fifo_alloc(AVSampleFormat.AV_SAMPLE_FMT_FLTP, _layout->nb_channels, 1024);
        _packet = ffmpeg.av_packet_alloc();
        _decoded = ffmpeg.av_frame_alloc();
        _resampled = ffmpeg.av_frame_alloc();
        _chunk = ffmpeg.av_frame_alloc();
        if (_pending is null || _segment is null || _packet is null || _decoded is null || _resampled is null || _chunk is null)
            throw new MediaException($"{What}: out of memory");
        return true;
    }

    /// <summary>Encodes <paramref name="seconds"/> of audio from <paramref name="start"/> (as <c>-ss</c>).</summary>
    public void WriteSegment(double start, double seconds, PreviewWriter writer)
    {
        var wanted = (long)Math.Round(seconds * SampleRate);
        var startUs = SeekPlanner.TargetMicroseconds(start, 2, _fmt->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : _fmt->start_time);
        ffmpeg.avformat_seek_file(_fmt, -1, long.MinValue, startUs, startUs, 0);
        ffmpeg.avcodec_flush_buffers(_dec);
        ffmpeg.swr_init(_swr).Check($"{What}: resampler");
        ffmpeg.av_audio_fifo_reset(_segment);

        // Decode until the segment has its samples (counted from its start) or the audio ends.
        long? skip = null;
        var flushed = false;
        while (true)
        {
            _interrupt.ThrowIfStopped();
            if (skip is { } s && ffmpeg.av_audio_fifo_size(_segment) - s >= wanted)
                break;
            var ret = ffmpeg.avcodec_receive_frame(_dec, _decoded);
            if (ret == 0)
            {
                if (skip is null)
                {
                    // Samples before the start (the decoder resumes at a packet boundary) are dropped.
                    var pts = _decoded->best_effort_timestamp;
                    var first = pts == ffmpeg.AV_NOPTS_VALUE ? startUs / 1e6 : Av.Seconds(pts, _stream->time_base);
                    skip = Math.Max(0, (long)Math.Round((startUs / 1e6 - first) * SampleRate));
                }
                Resample();
                continue;
            }
            if (ret == Av.Eof)
                break;
            if (ret != Av.Again)
                ret.Check($"{What}: decoding");
            if (flushed)
                break;
            ret = ffmpeg.av_read_frame(_fmt, _packet);
            if (ret == Av.Eof)
            {
                flushed = true;
                ffmpeg.avcodec_send_packet(_dec, null);
                continue;
            }
            ret.Check($"{What}: reading the source", _interrupt);
            if (_packet->stream_index == _stream->index)
                ffmpeg.avcodec_send_packet(_dec, _packet);
            ffmpeg.av_packet_unref(_packet);
        }

        // Exactly `wanted` samples from the start: drop what came before it, pad silence after the end.
        ffmpeg.av_audio_fifo_drain(_segment, (int)Math.Min(skip ?? 0, ffmpeg.av_audio_fifo_size(_segment)));
        var available = (int)Math.Min(wanted, ffmpeg.av_audio_fifo_size(_segment));
        if (available > 0)
        {
            var frame = Chunk(available);
            ffmpeg.av_audio_fifo_read(_segment, (void**)frame->extended_data, available);
            ffmpeg.av_audio_fifo_write(_pending, (void**)frame->extended_data, available);
        }
        if (wanted > available)
        {
            var silence = (int)(wanted - available);
            var frame = Chunk(silence);
            ffmpeg.av_samples_set_silence(frame->extended_data, 0, silence, _layout->nb_channels, AVSampleFormat.AV_SAMPLE_FMT_FLTP);
            ffmpeg.av_audio_fifo_write(_pending, (void**)frame->extended_data, silence);
        }
        Encode(writer, all: false);
    }

    /// <summary>The samples still pending, including a short last frame.</summary>
    public void Flush(PreviewWriter writer) => Encode(writer, all: true);

    private void Resample()
    {
        ffmpeg.av_frame_unref(_resampled);
        _resampled->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        _resampled->sample_rate = SampleRate;
        ffmpeg.av_channel_layout_copy(&_resampled->ch_layout, _layout);
        ffmpeg.swr_convert_frame(_swr, _resampled, _decoded).Check($"{What}: resampling");
        if (_resampled->nb_samples > 0)
            ffmpeg.av_audio_fifo_write(_segment, (void**)_resampled->extended_data, _resampled->nb_samples);
    }

    /// <summary>A reusable frame with room for <paramref name="samples"/> samples.</summary>
    private AVFrame* Chunk(int samples)
    {
        ffmpeg.av_frame_unref(_chunk);
        _chunk->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        _chunk->sample_rate = SampleRate;
        _chunk->nb_samples = samples;
        ffmpeg.av_channel_layout_copy(&_chunk->ch_layout, _layout);
        ffmpeg.av_frame_get_buffer(_chunk, 0).Check($"{What}: out of memory");
        return _chunk;
    }

    /// <summary>Whole encoder frames from the pending samples; with <paramref name="all"/> also the short rest.</summary>
    private void Encode(PreviewWriter writer, bool all)
    {
        var frameSize = writer.AudioEncoder->frame_size > 0 ? writer.AudioEncoder->frame_size : 1024;
        while (ffmpeg.av_audio_fifo_size(_pending) >= frameSize || (all && ffmpeg.av_audio_fifo_size(_pending) > 0))
        {
            var samples = Math.Min(frameSize, ffmpeg.av_audio_fifo_size(_pending));
            var frame = Chunk(samples);
            ffmpeg.av_audio_fifo_read(_pending, (void**)frame->extended_data, samples);
            frame->pts = _samplesWritten;
            _samplesWritten += samples;
            writer.WriteAudio(frame);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        var packet = _packet;
        ffmpeg.av_packet_free(&packet);
        var decoded = _decoded;
        ffmpeg.av_frame_free(&decoded);
        var resampled = _resampled;
        ffmpeg.av_frame_free(&resampled);
        var chunk = _chunk;
        ffmpeg.av_frame_free(&chunk);
        _packet = null;
        _decoded = _resampled = _chunk = null;
        if (_pending is not null)
            ffmpeg.av_audio_fifo_free(_pending);
        if (_segment is not null)
            ffmpeg.av_audio_fifo_free(_segment);
        _pending = _segment = null;
        var swr = _swr;
        ffmpeg.swr_free(&swr);
        _swr = null;
        var dec = _dec;
        ffmpeg.avcodec_free_context(&dec);
        _dec = null;
        var fmt = _fmt;
        if (fmt is not null)
            ffmpeg.avformat_close_input(&fmt);
        _fmt = null;
        ffmpeg.av_channel_layout_uninit(_layout);
        NativeMemory.Free(_layout);
    }
}
