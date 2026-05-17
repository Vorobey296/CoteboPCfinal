using System;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace CoteboPC;

internal class Program
{
    private static Mutex? _singleInstanceMutex;

    [STAThread]
    public static void Main(string[] args)
    {
        const string mutexName = @"Global\CoteboPC_Avalonia_SingleInstance";
        bool createdNew;
        _singleInstanceMutex = new Mutex(true, mutexName, out createdNew);
        if (!createdNew)
        {
            Logger.Log("[App] Another CoteboPC instance is already running.");
            return;
        }

        try
        {
            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        }
        catch (Exception ex)
        {
            Logger.Log($"[App] Критическая ошибка при запуске: {ex}");
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}