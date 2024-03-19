using Lagrange.Core.Common;
using System.IO.IsolatedStorage;
using System.Text.Json;

namespace CameraScreenshotBotService.Services;

public class StorageService(ILogger<StorageService> logger)
{
    private readonly ILogger<StorageService> _logger = logger;
    private readonly IsolatedStorageFile _isoStore = IsolatedStorageFile.GetStore(
IsolatedStorageScope.User | IsolatedStorageScope.Application, null, null);

    private readonly string _devieInfoPath = Path.Combine(AppDomain.CurrentDomain.FriendlyName, "deviceInfo.json");
    private readonly string _keyStorePath = Path.Combine(AppDomain.CurrentDomain.FriendlyName, "key.json");

    public BotDeviceInfo? LoadDeviceInfo()
    {
        if (!_isoStore.DirectoryExists(AppDomain.CurrentDomain.FriendlyName))
        {
            _isoStore.CreateDirectory(AppDomain.CurrentDomain.FriendlyName);
        }

        if (!_isoStore.FileExists(_devieInfoPath))
        {
            return null;
        }

        using var infoStream = _isoStore.OpenFile(_devieInfoPath, FileMode.Open, FileAccess.Read);
        return JsonSerializer.Deserialize<BotDeviceInfo>(infoStream);
    }

    public void SaveDeviceInfo(BotDeviceInfo deviceInfo)
    {
        if (!_isoStore.DirectoryExists(AppDomain.CurrentDomain.FriendlyName))
        {
            _isoStore.CreateDirectory(AppDomain.CurrentDomain.FriendlyName);
        }

        using var infoStream = _isoStore.OpenFile(_devieInfoPath, FileMode.OpenOrCreate, FileAccess.Write);
        JsonSerializer.Serialize(infoStream, deviceInfo);
    }

    public  BotKeystore? LoadKeyStore()
    {
        if (!_isoStore.DirectoryExists(AppDomain.CurrentDomain.FriendlyName))
        {
            _isoStore.CreateDirectory(AppDomain.CurrentDomain.FriendlyName);
        }

        if (!_isoStore.FileExists(_keyStorePath))
        {
            return null;
        }

        using var keyStream = _isoStore.OpenFile(_keyStorePath, FileMode.Open, FileAccess.Read);
        return JsonSerializer.Deserialize<BotKeystore>(keyStream);
    }

    public void SaveKeyStore(BotKeystore keyStore)
    {
        if (!_isoStore.DirectoryExists(AppDomain.CurrentDomain.FriendlyName))
        {
            _isoStore.CreateDirectory(AppDomain.CurrentDomain.FriendlyName);
        }

        using var keyStream = _isoStore.OpenFile(_keyStorePath, FileMode.OpenOrCreate, FileAccess.Write);
        JsonSerializer.Serialize(keyStream, keyStore);
    }

}
