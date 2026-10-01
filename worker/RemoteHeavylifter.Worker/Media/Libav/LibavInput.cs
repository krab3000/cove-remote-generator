using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Logging;

namespace RemoteHeavylifter.Worker.Media.Libav;

/// <summary>How a <see cref="LibavInput"/> opens and decodes its source.</summary>
/// <param name="Threads">Decoder threads; 0 lets libav choose.</param>
/// <param name="SliceThreads">Slice threading only: frame threading delays every frame after a seek by a frame per thread.</param>
/// <param name="Lenient">The cover's <c>-fflags +discardcorrupt -err_detect ignore_err</c>.</param>
/// <param name="FormatOptions">Extra demuxer/protocol options (from HL_FFMPEG_INPUT_ARGS).</param>
internal sealed record DecodeSettings(
    int Threads, bool SliceThreads, bool Lenient = false, IReadOnlyList<KeyValuePair<string, string>>? FormatOptions = null);

/// <summary>
/// One opened source and a decoder for its main video stream (on the GPU when the source asks for it). Frames come out
/// in system memory with <c>pts</c> set to libav's best-effort timestamp. Not thread-safe: one step, one thread.
/// </summary>
internal sealed unsafe class LibavInput : IDisposable
{
    private static readonly AVCodecContext_get_format GetHwFormat = (ctx, formats) =>
    {
        var wanted = (AVPixelFormat)(int)(nint)ctx->opaque;
        for (var f = formats; *f != AVPixelFormat.AV_PIX_FMT_NONE; f++)
        {
            if (*f == wanted)
                return wanted;
        }
        // The GPU cannot take this stream (profile, size): decode in software, as the command line does.
        return ffmpeg.avcodec_default_get_format(ctx, formats);
    };

    private readonly Interrupt _interrupt;
    private readonly ILogger _log;
    private AVFormatContext* _fmt;
    private AVCodecContext* _dec;
    private AVPacket* _packet;
    private AVFrame* _decoded;
    private AVFrame* _transfer;
    private AVBufferRef* _hwDevice;
    private AVPixelFormat _hwFormat = AVPixelFormat.AV_PIX_FMT_NONE;
    private bool _demuxEnded, _flushSent, _decoderEnded;

    private LibavInput(Interrupt interrupt, ILogger log)
    {
        _interrupt = interrupt;
        _log = log;
    }

    public AVStream* Stream { get; private set; }
    public int StreamIndex { get; private set; }
    public AVRational TimeBase => Stream->time_base;

    /// <summary>The container's start time in microseconds (0 when unknown), which <c>-ss</c> positions are relative to.</summary>
    public long StartMicroseconds => _fmt->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : _fmt->start_time;

    /// <summary>Whether frames are being decoded on the GPU right now.</summary>
    public bool OnGpu => _hwFormat != AVPixelFormat.AV_PIX_FMT_NONE;

    /// <summary>Timestamp of the last frame handed out; null before the first one and after a seek.</summary>
    public long? Position { get; private set; }

    public AVFormatContext* Format => _fmt;

    /// <summary>Opens <paramref name="source"/>. When it asks for hardware decoding and the GPU cannot be set up, the
    /// result is a <see cref="HardwareDecodeException"/>; reopen with <see cref="MediaSource.Software"/>.</summary>
    public static LibavInput Open(MediaSource source, DecodeSettings settings, Interrupt interrupt, ILogger log, string what)
    {
        var input = new LibavInput(interrupt, log);
        try
        {
            input.OpenFormat(source, settings, what);
            input.OpenDecoder(source, settings, what);
            return input;
        }
        catch
        {
            input.Dispose();
            throw;
        }
    }

    /// <summary>Only the container: enough for the duration.</summary>
    public static LibavInput OpenFormatOnly(MediaSource source, Interrupt interrupt, ILogger log, string what)
    {
        var input = new LibavInput(interrupt, log);
        try
        {
            input.OpenFormat(source, new DecodeSettings(1, true), what);
            return input;
        }
        catch
        {
            input.Dispose();
            throw;
        }
    }

    private void OpenFormat(MediaSource source, DecodeSettings settings, string what)
    {
        _fmt = ffmpeg.avformat_alloc_context();
        if (_fmt is null)
            throw new MediaException($"{what}: out of memory");
        _fmt->interrupt_callback = _interrupt.Native;

        var entries = source.ProtocolOptions().Concat(settings.FormatOptions ?? []).ToList();
        if (settings.Lenient)
            entries.Add(new("fflags", "+discardcorrupt"));
        var options = Av.Dictionary(entries);
        var fmt = _fmt;
        var ret = ffmpeg.avformat_open_input(&fmt, source.Url, null, &options);
        _fmt = fmt; // freed (and nulled) by libav on failure
        var unused = Av.Free(options);
        ret.Check($"{what}: cannot open the source", _interrupt);
        if (unused.Count > 0)
            _log.LogDebug("libav ignored input options {Options}", string.Join(", ", unused));
        ffmpeg.avformat_find_stream_info(_fmt, null).Check($"{what}: cannot read the stream info", _interrupt);
    }

    private void OpenDecoder(MediaSource source, DecodeSettings settings, string what)
    {
        AVCodec* codec = null;
        StreamIndex = ffmpeg.av_find_best_stream(_fmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0)
            .Check($"{what}: no video stream");
        Stream = _fmt->streams[StreamIndex];
        for (var i = 0; i < (int)_fmt->nb_streams; i++)
        {
            if (i != StreamIndex)
                _fmt->streams[i]->discard = AVDiscard.AVDISCARD_ALL;
        }

        _dec = ffmpeg.avcodec_alloc_context3(codec);
        if (_dec is null)
            throw new MediaException($"{what}: out of memory");
        ffmpeg.avcodec_parameters_to_context(_dec, Stream->codecpar).Check($"{what}: decoder parameters");
        _dec->pkt_timebase = Stream->time_base;

        if (source.HwDecode is { } hw)
            SetUpHardware(codec, hw.Accel, hw.Device, what);
        if (!OnGpu)
        {
            _dec->thread_count = settings.Threads;
            if (settings.SliceThreads)
                _dec->thread_type = ffmpeg.FF_THREAD_SLICE;
        }

        var options = Av.Dictionary(settings.Lenient ? [new("err_detect", "ignore_err")] : []);
        var ret = ffmpeg.avcodec_open2(_dec, codec, &options);
        Av.Free(options);
        if (ret < 0 && OnGpu)
            throw new HardwareDecodeException($"{what}: cannot open the {Name(codec)} decoder on the GPU: {Av.Error(ret)}");
        ret.Check($"{what}: cannot open the {Name(codec)} decoder");

        _packet = ffmpeg.av_packet_alloc();
        _decoded = ffmpeg.av_frame_alloc();
        _transfer = ffmpeg.av_frame_alloc();
        if (_packet is null || _decoded is null || _transfer is null)
            throw new MediaException($"{what}: out of memory");
    }

    private void SetUpHardware(AVCodec* codec, string accel, string? device, string what)
    {
        var type = ffmpeg.av_hwdevice_find_type_by_name(accel);
        if (type == AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
            throw new HardwareDecodeException($"{what}: unknown hwaccel {accel}");
        for (var i = 0; ; i++)
        {
            var config = ffmpeg.avcodec_get_hw_config(codec, i);
            if (config is null)
            {
                // Like the command line: a codec the GPU cannot decode is decoded in software.
                _log.LogDebug("{Codec} cannot be decoded with {Accel}; decoding in software", Name(codec), accel);
                return;
            }
            if ((config->methods & Av.HwConfigDeviceCtx) != 0 && config->device_type == type)
            {
                _hwFormat = config->pix_fmt;
                break;
            }
        }

        AVBufferRef* hwDevice = null;
        var ret = ffmpeg.av_hwdevice_ctx_create(&hwDevice, type, device, null, 0);
        if (ret < 0)
        {
            _hwFormat = AVPixelFormat.AV_PIX_FMT_NONE;
            throw new HardwareDecodeException($"{what}: cannot open {accel} device {device ?? "(default)"}: {Av.Error(ret)}");
        }
        _hwDevice = hwDevice;
        _dec->hw_device_ctx = ffmpeg.av_buffer_ref(_hwDevice);
        _dec->opaque = (void*)(nint)(int)_hwFormat;
        _dec->get_format = GetHwFormat;
    }

    private static string Name(AVCodec* codec) => codec is null ? "video" : new string((sbyte*)codec->name);

    /// <summary>Seeks like <c>-ss</c>: to the keyframe at or before <paramref name="targetUs"/> (microseconds, container
    /// start included), then drops the decoder's state.</summary>
    public void Seek(long targetUs, string what)
    {
        _interrupt.ThrowIfStopped();
        var ret = ffmpeg.avformat_seek_file(_fmt, -1, long.MinValue, targetUs, targetUs, 0);
        if (ret < 0)
            _log.LogDebug("{What}: seek to {Target} µs failed ({Error}); decoding on", what, targetUs, Av.Error(ret));
        ffmpeg.avcodec_flush_buffers(_dec);
        _demuxEnded = _flushSent = _decoderEnded = false;
        Position = null;
    }

    /// <summary>The stream timestamp for a position in microseconds.</summary>
    public long ToStream(long microseconds) => ffmpeg.av_rescale_q(microseconds, Av.TimeBaseQ, TimeBase);

    /// <summary>The timestamp of the last index entry at or before <paramref name="pts"/>; null without an index.</summary>
    public long? KeyframeAtOrBefore(long pts)
    {
        var index = ffmpeg.av_index_search_timestamp(Stream, pts, ffmpeg.AVSEEK_FLAG_BACKWARD);
        if (index < 0)
            return null;
        var entry = ffmpeg.avformat_index_get_entry(Stream, index);
        return entry is null ? null : entry->timestamp;
    }

    /// <summary>
    /// The first frame at or after <paramref name="targetUs"/>, seeking only when decoding on from the current position
    /// would mean decoding a GOP twice (<see cref="SeekPlanner.ShouldSeek"/>). Null past the end. The frame stays valid
    /// until the next call.
    /// </summary>
    public AVFrame* FrameAt(long targetUs, string what)
    {
        var target = ToStream(targetUs);
        var reach = ToStream((long)(SeekPlanner.NoIndexForwardSeconds * 1_000_000)) - ToStream(0);
        if (SeekPlanner.ShouldSeek(Position, KeyframeAtOrBefore(target), target, reach))
            Seek(targetUs, what);
        AVFrame* frame;
        // Frames on the way to the target stay on the GPU: only the one handed out is copied to memory.
        while ((frame = NextFrame(what, download: false)) is not null)
        {
            if (frame->pts == ffmpeg.AV_NOPTS_VALUE || frame->pts >= target)
                return Download(frame, what);
        }
        return null;
    }

    /// <summary>The next decoded frame, or null at the end of the stream. With <paramref name="download"/> (the
    /// default) it is in system memory; otherwise a GPU frame stays on the GPU (see <see cref="Download"/>).</summary>
    public AVFrame* NextFrame(string what, bool download = true)
    {
        while (true)
        {
            _interrupt.ThrowIfStopped();
            if (_decoderEnded)
                return null;

            var ret = ffmpeg.avcodec_receive_frame(_dec, _decoded);
            if (ret == 0)
            {
                _decoded->pts = _decoded->best_effort_timestamp;
                if (_decoded->pts != ffmpeg.AV_NOPTS_VALUE)
                    Position = _decoded->pts;
                return download ? Download(_decoded, what) : _decoded;
            }
            if (ret == Av.Eof)
            {
                _decoderEnded = true;
                return null;
            }
            if (ret != Av.Again)
                Fail(ret, what, "decoding");

            if (_demuxEnded)
            {
                if (_flushSent)
                {
                    _decoderEnded = true;
                    return null;
                }
                _flushSent = true;
                ffmpeg.avcodec_send_packet(_dec, null);
                continue;
            }

            ret = ffmpeg.av_read_frame(_fmt, _packet);
            if (ret == Av.Eof)
            {
                _demuxEnded = true;
                continue;
            }
            ret.Check($"{what}: reading the source", _interrupt);
            if (_packet->stream_index != StreamIndex)
            {
                ffmpeg.av_packet_unref(_packet);
                continue;
            }
            ret = ffmpeg.avcodec_send_packet(_dec, _packet);
            ffmpeg.av_packet_unref(_packet);
            // Corrupt packets are skipped, as the command line does without -xerror.
            if (ret < 0 && ret != Av.Again && ret != ffmpeg.AVERROR_INVALIDDATA)
                Fail(ret, what, "decoding");
        }
    }

    /// <summary>The frame in system memory: a GPU frame is copied (once), anything else returned as is.</summary>
    public AVFrame* Download(AVFrame* frame, string what)
    {
        if (!OnGpu || (AVPixelFormat)frame->format != _hwFormat)
            return frame;
        ffmpeg.av_frame_unref(_transfer);
        var ret = ffmpeg.av_hwframe_transfer_data(_transfer, frame, 0);
        if (ret < 0)
            throw new HardwareDecodeException($"{what}: copying a frame from the GPU: {Av.Error(ret)}");
        ffmpeg.av_frame_copy_props(_transfer, frame);
        return _transfer;
    }

    private void Fail(int ret, string what, string doing)
    {
        if (OnGpu && ret != Av.Exit)
            throw new HardwareDecodeException($"{what}: {doing} on the GPU: {Av.Error(ret)}");
        ret.Check($"{what}: {doing}", _interrupt);
    }

    /// <summary>
    /// For a frame copied from the GPU (NV12 where software decoding gives YUV420P): the filter that repacks it to the
    /// software decoder's format first, so both convert to RGB through the same swscale path (NV12 → RGB rounds up
    /// to 2 levels differently). Null when no repack is needed.
    /// </summary>
    public string? SoftwareFormatFilter(AVFrame* frame)
    {
        var software = _dec->sw_pix_fmt;
        if (software == AVPixelFormat.AV_PIX_FMT_NONE || (AVPixelFormat)frame->format == software)
            return null;
        var name = ffmpeg.av_get_pix_fmt_name(software);
        return name is null ? null : $"format={name}";
    }

    /// <summary>The display matrix fftools autorotates by: the frame's, else the stream's.</summary>
    public int[]? DisplayMatrix(AVFrame* frame)
    {
        var side = ffmpeg.av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_DISPLAYMATRIX);
        if (side is not null && side->size >= 36)
            return new ReadOnlySpan<int>(side->data, 9).ToArray();
        var par = Stream->codecpar;
        var packetSide = ffmpeg.av_packet_side_data_get(par->coded_side_data, par->nb_coded_side_data, AVPacketSideDataType.AV_PKT_DATA_DISPLAYMATRIX);
        return packetSide is not null && packetSide->size >= 36 ? new ReadOnlySpan<int>(packetSide->data, 9).ToArray() : null;
    }

    public void Dispose()
    {
        var packet = _packet;
        ffmpeg.av_packet_free(&packet);
        _packet = null;
        var decoded = _decoded;
        ffmpeg.av_frame_free(&decoded);
        _decoded = null;
        var transfer = _transfer;
        ffmpeg.av_frame_free(&transfer);
        _transfer = null;
        var dec = _dec;
        ffmpeg.avcodec_free_context(&dec);
        _dec = null;
        var device = _hwDevice;
        ffmpeg.av_buffer_unref(&device);
        _hwDevice = null;
        var fmt = _fmt;
        if (fmt is not null)
            ffmpeg.avformat_close_input(&fmt);
        _fmt = null;
    }
}
