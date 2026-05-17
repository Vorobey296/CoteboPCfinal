// App.axaml.cs
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Controls;
using CoteboPC.Utils;
using CoteboPC.Views;

namespace CoteboPC;

public partial class App : Application
{
    private static System.Threading.Mutex? _singleInstanceMutex;

    public App()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        try
        {
            try
            {
                bool createdNew;
                _singleInstanceMutex = new System.Threading.Mutex(true, "CoteboPC_SingleInstance_Mutex", out createdNew);
                if (!createdNew)
                {
                    Logger.Log("[App] Another instance is already running. Exiting this instance.");
                    Environment.Exit(0);
                }
            }
            catch (Exception exMutex)
            {
                Logger.Log($"[App] Mutex check failed: {exMutex}");
            }

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                Logger.Log("[App] OnFrameworkInitializationCompleted started.");
                try
                {
                    var proc = System.Diagnostics.Process.GetCurrentProcess();
                    Logger.Log($"[App] Process info: PID={proc.Id}, CmdLine={Environment.CommandLine}, CWD={Environment.CurrentDirectory}");
                }
                catch (Exception exProc)
                {
                    Logger.Log($"[App] Failed to log process info: {exProc}");
                }
                
                desktop.MainWindow = CreateHiddenWindow();
                Logger.Log("[App] Hidden main window created.");

                // Не блокируем UI на время создания задачи планировщика / UAC
                try
                {
                    _ = PlatformHelpers.EnsureWindowsAdminAutostartAsync();
                    Logger.Log("[App] Scheduled EnsureWindowsAdminAutostartAsync (fire-and-forget).");
                }
                catch (Exception exAuto)
                {
                    Logger.Log($"[App] Failed to start autostart setup task: {exAuto}");
                }

                var configPath = ConfigService.GetConfigPath();
                Logger.Log($"[App] Config path: {configPath}");
                Logger.Log($"[App] Config exists: {System.IO.File.Exists(configPath)}");
                Logger.Log("[App] Loading config...");
                var config = ConfigService.LoadConfig().GetAwaiter().GetResult();
                Logger.Log($"[App] Config loaded result: {(config == null ? "null" : "present")}");

                if (config == null || string.IsNullOrEmpty(config.Token))
                {
                    Logger.Log("[App] No config found, showing FirstSetupWindow.");
                    var setupWindow = new FirstSetupWindow();
                    setupWindow.Show();
                }
                else
                {
                    Logger.Log("[App] Config found, starting bot...");
                    StartBotInBackground(config);
                }

                desktop.Exit += OnExit;
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"[App] Exception in OnFrameworkInitializationCompleted: {ex}");
        }
        finally
        {
            base.OnFrameworkInitializationCompleted();
        }
    }

    private void StartBotInBackground(AppConfig config)
    {
        _ = Task.Run(async () =>
        {
            const int maxAttempts = 3;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    Logger.Log($"[App] Bot startup attempt {attempt}/{maxAttempts}.");
                    await CoteboBotService.StartAsync(config);
                    Logger.Log("[App] Bot started successfully.");
                    return;
                }
                catch (Exception ex)
                {
                    Logger.Log($"[App] Bot startup attempt {attempt} failed: {ex}");
                    if (attempt < maxAttempts)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(10));
                    }
                }
            }

            Logger.Log("[App] Bot failed to start after multiple attempts. Shutting down application.");
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
            else
            {
                Environment.Exit(1);
            }
        });
    }

    private void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        CoteboBotService.Stop();
        try
        {
            if (_singleInstanceMutex != null)
            {
                try { _singleInstanceMutex.ReleaseMutex(); } catch { }
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"[App] Error releasing mutex: {ex}");
        }
    }

    private Window CreateHiddenWindow()
    {
        var hiddenWindow = new Window
        {
            Width = 0,
            Height = 0,
            WindowState = WindowState.Minimized,
            ShowInTaskbar = false,
            ShowActivated = false,
            IsVisible = false,
            CanResize = false,
            Topmost = false,
            Opacity = 0,
            Content = null,
            WindowDecorations = WindowDecorations.None,
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent }
        };

        return hiddenWindow;
    }
}