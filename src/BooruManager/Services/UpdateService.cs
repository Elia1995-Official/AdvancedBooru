using NetSparkleUpdater;
using NetSparkleUpdater.SignatureVerifiers;
using NetSparkleUpdater.UI.Avalonia;

namespace BooruManager.Services;

public class UpdateService : IDisposable
{
    private SparkleUpdater? _sparkle;
    private bool _disposed;

    public void Initialize()
    {
        _sparkle = new SparkleUpdater(
            "https://github.com/Elia1995-Official/AdvancedBooru/releases/latest/download/appcast.xml",
            new Ed25519Checker(NetSparkleUpdater.Enums.SecurityMode.Unsafe))
        {
            UIFactory = new UIFactory(),
            RelaunchAfterUpdate = true
        };

        _sparkle.StartLoop(true, true);
    }

    public void CheckForUpdates()
    {
        _sparkle?.CheckForUpdatesQuietly();
    }

    public string CurrentVersion => "1.0.0";

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _sparkle?.Dispose();
        }

        _disposed = true;
    }
}
