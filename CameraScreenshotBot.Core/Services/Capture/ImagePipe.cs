namespace CameraScreenshotBot.Core.Services.Capture;
public class ImagePipe
{
    private readonly IList<Func<bool, MemoryStream[]>> _functions;

}
