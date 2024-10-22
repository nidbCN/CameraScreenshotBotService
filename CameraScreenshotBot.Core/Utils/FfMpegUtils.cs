using System.Runtime.InteropServices;
using CameraCaptureBot.Core.Extensions;
using CameraCaptureBot.Core.Services;
using FFmpeg.AutoGen;

namespace CameraCaptureBot.Core.Utils
{
    public static class FfMpegUtils
    {

        #region 创建编码器
        /// <summary>
        /// 创建编解码器
        /// </summary>
        /// <param name="codecId">编解码器ID</param>
        /// <param name="config">配置</param>
        /// <param name="pixelFormat">像素格式</param>
        /// <returns>编解码器上下文</returns>
        public static unsafe AVCodecContext* CreateCodecCtx(AVCodecID codecId, Action<AvCodecContextWrapper>? config = null, AVPixelFormat? pixelFormat = null)
        {
            var codec = ffmpeg.avcodec_find_encoder(codecId);

            return CreateCodecCtx(codec, ctx =>
            {
                ctx.Value->pix_fmt = pixelFormat ?? codec->pix_fmts[0];
                config?.Invoke(ctx);
            });
        }

        public static unsafe AVCodecContext* CreateCodecCtx(string codecName, Action<AvCodecContextWrapper>? config = null, AVPixelFormat? pixelFormat = null)
        {
            var codec = ffmpeg.avcodec_find_encoder_by_name(codecName);

            return CreateCodecCtx(codec, ctx =>
            {
                ctx.Value->pix_fmt = pixelFormat ?? codec->pix_fmts[0];
                config?.Invoke(ctx);
            });
        }

        public static unsafe AVCodecContext* CreateCodecCtx(AVCodec* codec, Action<AvCodecContextWrapper>? config = null)
        {
            // 编解码器
            var ctx = ffmpeg.avcodec_alloc_context3(codec);

            ctx->time_base = new() { num = 1, den = 25 }; // 设置时间基准
            ctx->framerate = new() { num = 25, den = 1 };

            config?.Invoke(new(ctx));

            ffmpeg.avcodec_open2(ctx, codec, null).ThrowExceptionIfError();

            return ctx;
        }
        #endregion

        public static unsafe void WriteToStream(Stream stream, AVPacket* packet)
        {
            var buffer = new byte[packet->size];
            Marshal.Copy((IntPtr)packet->data, buffer, 0, packet->size);
            stream.Write(buffer, 0, packet->size);
        }
    }
}
