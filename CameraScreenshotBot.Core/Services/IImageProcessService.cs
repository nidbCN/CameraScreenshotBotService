using FFmpeg.AutoGen;

namespace CameraCaptureBot.Core.Services;

public interface IImageProcessService
{
    /// <summary>
    /// 处理帧
    /// </summary>
    /// <param name="inputFrame">传入帧，如果和传出不是同一指针则应当在最后销毁</param>
    /// <param name="outputFrame">传出帧，可以和传入是同一指针，不应销毁</param>
    /// <returns>转换是否成功</returns>
    public unsafe bool TryProcessImage(AVFrame* inputFrame, out AVFrame* outputFrame);
}
