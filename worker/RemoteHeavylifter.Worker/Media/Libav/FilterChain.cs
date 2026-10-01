using FFmpeg.AutoGen.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace RemoteHeavylifter.Worker.Media.Libav;

/// <summary>
/// A libavfilter graph from decoded frames to <c>chain</c>'s output: the same filter strings the command line passes to
/// <c>-vf</c>, with fftools' bicubic scaler and automatic format conversion, so libav and the command line scale alike.
/// </summary>
internal sealed unsafe class FilterChain : IDisposable
{
    private AVFilterGraph* _graph;
    private AVFilterContext* _source;
    private AVFilterContext* _sink;

    private FilterChain()
    {
    }

    /// <summary>Joins filters, skipping empty ones; an empty chain passes frames through.</summary>
    public static string Join(params string?[] filters) => string.Join(",", filters.Where(f => !string.IsNullOrEmpty(f)));

    /// <summary>A graph whose input matches <paramref name="frame"/> (format, size, aspect, colour) in <paramref name="timeBase"/>.</summary>
    /// <param name="frameRate">The stream's frame rate, as fftools passes it to the buffer source (0/0 when unknown).</param>
    /// <param name="hwDevice">For filters that upload to a GPU (vaapi's hwupload): the device they use.</param>
    public static FilterChain Create(
        AVFrame* frame, AVRational timeBase, string chain, int threads, string what, AVRational frameRate = default, AVBufferRef* hwDevice = null)
    {
        var filters = new FilterChain();
        try
        {
            filters.Build(frame, timeBase, chain, threads, what, frameRate, hwDevice);
            return filters;
        }
        catch
        {
            filters.Dispose();
            throw;
        }
    }

    private void Build(AVFrame* frame, AVRational timeBase, string chain, int threads, string what, AVRational frameRate, AVBufferRef* hwDevice)
    {
        _graph = ffmpeg.avfilter_graph_alloc();
        if (_graph is null)
            throw new MediaException($"{what}: out of memory");
        ffmpeg.av_opt_set(_graph, "scale_sws_opts", "flags=bicubic", 0);
        _graph->nb_threads = threads;

        _source = ffmpeg.avfilter_graph_alloc_filter(_graph, ffmpeg.avfilter_get_by_name("buffer"), "in");
        if (_source is null)
            throw new MediaException($"{what}: out of memory");
        var parameters = ffmpeg.av_buffersrc_parameters_alloc();
        parameters->format = frame->format;
        parameters->width = frame->width;
        parameters->height = frame->height;
        parameters->time_base = timeBase;
        parameters->sample_aspect_ratio = frame->sample_aspect_ratio;
        parameters->color_space = frame->colorspace;
        parameters->color_range = frame->color_range;
        parameters->frame_rate = frameRate;
        var ret = ffmpeg.av_buffersrc_parameters_set(_source, parameters);
        ffmpeg.av_free(parameters);
        ret.Check($"{what}: filter input");
        ffmpeg.avfilter_init_str(_source, null).Check($"{what}: filter input");

        AVFilterContext* sink = null;
        ffmpeg.avfilter_graph_create_filter(&sink, ffmpeg.avfilter_get_by_name("buffersink"), "out", null, null, _graph)
            .Check($"{what}: filter output");
        _sink = sink;

        var outputs = ffmpeg.avfilter_inout_alloc();
        var inputs = ffmpeg.avfilter_inout_alloc();
        try
        {
            outputs->name = ffmpeg.av_strdup("in");
            outputs->filter_ctx = _source;
            outputs->pad_idx = 0;
            outputs->next = null;
            inputs->name = ffmpeg.av_strdup("out");
            inputs->filter_ctx = _sink;
            inputs->pad_idx = 0;
            inputs->next = null;
            ffmpeg.avfilter_graph_parse_ptr(_graph, chain.Length == 0 ? "null" : chain, &inputs, &outputs, null)
                .Check($"{what}: filter \"{chain}\"");
        }
        finally
        {
            ffmpeg.avfilter_inout_free(&inputs);
            ffmpeg.avfilter_inout_free(&outputs);
        }
        if (hwDevice is not null)
        {
            for (var i = 0; i < (int)_graph->nb_filters; i++)
                _graph->filters[i]->hw_device_ctx = ffmpeg.av_buffer_ref(hwDevice);
        }
        ffmpeg.avfilter_graph_config(_graph, null).Check($"{what}: filter \"{chain}\"");
    }

    public AVRational OutputTimeBase => ffmpeg.av_buffersink_get_time_base(_sink);

    public AVRational OutputFrameRate => ffmpeg.av_buffersink_get_frame_rate(_sink);

    /// <summary>The GPU frames context frames leave the graph in (after hwupload); null for frames in memory.</summary>
    public AVBufferRef* OutputHwFrames => ffmpeg.av_buffersink_get_hw_frames_ctx(_sink);

    /// <summary>Feeds a frame; the caller keeps its own reference.</summary>
    public void Push(AVFrame* frame, string what) =>
        ffmpeg.av_buffersrc_add_frame_flags(_source, frame, Av.BufferSrcKeepRef).Check($"{what}: filtering");

    /// <summary>Signals the end of input, so frames a filter still holds come out.</summary>
    public void Close(string what) => ffmpeg.av_buffersrc_add_frame_flags(_source, null, 0).Check($"{what}: filtering");

    /// <summary>The next filtered frame into <paramref name="output"/>: true, or false when the graph needs input (or ended).</summary>
    public bool Pull(AVFrame* output, string what)
    {
        var ret = ffmpeg.av_buffersink_get_frame(_sink, output);
        if (ret == Av.Again || ret == Av.Eof)
            return false;
        ret.Check($"{what}: filtering");
        return true;
    }

    /// <summary>Runs one frame through a fresh graph and returns the single output frame (caller frees it).</summary>
    public static AVFrame* RunOnce(AVFrame* frame, AVRational timeBase, string chain, int threads, string what)
    {
        using var filters = Create(frame, timeBase, chain, threads, what);
        filters.Push(frame, what);
        filters.Close(what);
        var output = ffmpeg.av_frame_alloc();
        if (filters.Pull(output, what))
            return output;
        ffmpeg.av_frame_free(&output);
        throw new MediaException($"{what}: the filter \"{chain}\" produced no frame");
    }

    public void Dispose()
    {
        var graph = _graph;
        ffmpeg.avfilter_graph_free(&graph);
        _graph = null;
        _source = _sink = null;
    }
}

internal static unsafe class Frames
{
    /// <summary>Copies an rgb24 frame into an image.</summary>
    public static Image<Rgb24> ToImage(AVFrame* frame)
    {
        if ((AVPixelFormat)frame->format != AVPixelFormat.AV_PIX_FMT_RGB24)
            throw new InvalidOperationException($"expected an rgb24 frame, got {(AVPixelFormat)frame->format}");
        int width = frame->width, height = frame->height, stride = frame->linesize[0];
        var data = frame->data[0];
        var image = new Image<Rgb24>(width, height);
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < height; y++)
                new ReadOnlySpan<byte>(data + (long)y * stride, width * 3).CopyTo(System.Runtime.InteropServices.MemoryMarshal.AsBytes(rows.GetRowSpan(y)));
        });
        return image;
    }

    /// <summary>One frame as a JPEG with the command line's <c>-q:v</c> scale (mjpeg encoder, fixed quantiser).</summary>
    public static byte[] EncodeJpeg(AVFrame* frame, int qscale, string what)
    {
        var codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_MJPEG);
        if (codec is null)
            throw new MediaException($"{what}: no mjpeg encoder");
        var ctx = ffmpeg.avcodec_alloc_context3(codec);
        var packet = ffmpeg.av_packet_alloc();
        try
        {
            ctx->width = frame->width;
            ctx->height = frame->height;
            ctx->pix_fmt = (AVPixelFormat)frame->format;
            ctx->color_range = frame->color_range;
            ctx->sample_aspect_ratio = frame->sample_aspect_ratio;
            ctx->time_base = new AVRational { num = 1, den = 25 };
            ctx->flags |= ffmpeg.AV_CODEC_FLAG_QSCALE;
            ctx->global_quality = qscale * ffmpeg.FF_QP2LAMBDA;
            ffmpeg.avcodec_open2(ctx, codec, null).Check($"{what}: jpeg encoder");

            frame->quality = ctx->global_quality;
            frame->pict_type = AVPictureType.AV_PICTURE_TYPE_NONE;
            frame->pts = 0;
            ffmpeg.avcodec_send_frame(ctx, frame).Check($"{what}: jpeg encoding");
            ffmpeg.avcodec_send_frame(ctx, null).Check($"{what}: jpeg encoding");
            ffmpeg.avcodec_receive_packet(ctx, packet).Check($"{what}: jpeg encoding");
            return new ReadOnlySpan<byte>(packet->data, packet->size).ToArray();
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
            ffmpeg.avcodec_free_context(&ctx);
        }
    }
}
