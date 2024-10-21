using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FFmpeg.AutoGen;

namespace CameraScreenshotBot.Core.Services.Capture
{
    public class FaceMosaicProcessService : IImageProcessService
    {
        public unsafe bool TryProcessImage(AVFrame* inputFrame, out AVFrame* outputFrame)
        {
            throw new NotImplementedException();
        }
    }
}
