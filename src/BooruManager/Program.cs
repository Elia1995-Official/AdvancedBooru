using System;
using Avalonia;
using Avalonia.ReactiveUI;
using BooruManager.Services;

namespace BooruManager;

internal class Program
{
    private static UpdateService? _updateService;

    public static void Main(string[] args)
    {
        Environment.SetEnvironmentVariable("AVALONIA_GLOBAL_SCALE_FACTOR", "0.8");

        _updateService = new UpdateService();
        _updateService.Initialize();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace()
            .UseReactiveUI();

    public static void CheckForUpdates()
    {
        _updateService?.CheckForUpdates();
    }
}
