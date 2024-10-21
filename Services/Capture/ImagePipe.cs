using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FFmpeg.AutoGen;

namespace App.WindowsService.Services.Capture;
public class ImagePipe
{
    private readonly IList<Func<bool, MemoryStream[]>> _functions;

}
