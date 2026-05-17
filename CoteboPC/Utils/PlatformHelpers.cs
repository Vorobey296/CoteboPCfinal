using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading.Tasks;

#if WINDOWS
using Microsoft.Win32;
#endif

namespace CoteboPC.Utils;

public static class PlatformHelpers
{
    public static async Task SetupWindowsAutostart()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

#if WINDOWS
        var created = await EnsureWindowsAdminAutostartAsync();
        if (!created)
        {
            // Фолбэк: создаём запись в HKCU Run, чтобы приложение запускалось без прав администратора
            try
            {
                var exe = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                key?.SetValue("CoteboPC", exe);
            }
            catch (Exception ex)
            {
                Logger.Log($"[PlatformHelpers] Failed to create HKCU Run entry: {ex}");
            }
        }
#endif
    }

    public static async Task RemoveWindowsAutostart()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

#if WINDOWS
        try
        {
            await ExecuteCommandRaw("schtasks /Delete /TN \"CoteboPC\" /F");
        }
        catch
        {
        }

        using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
        key?.DeleteValue("CoteboPC", false);
#endif
        await Task.CompletedTask;
    }

    public static async Task<bool> EnsureWindowsAdminAutostartAsync()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;

#if WINDOWS
        // Если уже есть задача планировщика — удаляем возможную запись в Run (чтобы не запускались оба механизма)
        if (await WindowsScheduledTaskExists())
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
                key?.DeleteValue("CoteboPC", false);
            }
            catch { }

            return true;
        }

        // Попробуем создать задачу планировщика с повышенными правами
        var created = await CreateWindowsScheduledTaskElevatedAsync();
        if (created)
        {
            try
            {
                using var key2 = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
                key2?.DeleteValue("CoteboPC", false);
            }
            catch { }
            return true;
        }

        return false;
#else
        return false;
#endif
    }

#if WINDOWS
    private static async Task<bool> WindowsScheduledTaskExists()
    {
        try
        {
            await ExecuteCommandRaw("schtasks /Query /TN \"CoteboPC\"");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> CreateWindowsScheduledTaskElevatedAsync()
    {
        var command = GetWindowsAutostartCommand();
        var filePart = command.FileName.Contains(" ") ? $"\"{command.FileName}\"" : command.FileName;
        var taskAction = string.IsNullOrWhiteSpace(command.Arguments)
            ? filePart
            : $"{filePart} {command.Arguments}";

        var schtasksCommand = $"/Create /RL HIGHEST /SC ONLOGON /TN \"CoteboPC\" /TR \"{taskAction}\" /F";
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = schtasksCommand,
            Verb = "runas",
            UseShellExecute = true,
            CreateNoWindow = true
        };

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null)
                return false;

            await proc.WaitForExitAsync();
            if (proc.ExitCode != 0)
                return false;

            return true;
        }
        catch
        {
            // Пользователь мог отказаться от UAC, либо команда не поддерживается.
            return false;
        }
    }

    private static (string FileName, string Arguments) GetWindowsAutostartCommand()
    {
        var exe = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
        var entryAssembly = System.Reflection.Assembly.GetEntryAssembly();
        var entryAssemblyPath = Path.Combine(AppContext.BaseDirectory, Path.GetFileName(exe) ?? string.Empty);
        if (entryAssembly != null)
        {
            var assemblyName = entryAssembly.GetName().Name;
            if (!string.IsNullOrWhiteSpace(assemblyName))
            {
                entryAssemblyPath = Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");
            }
        }

        if (exe.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase) || exe.EndsWith("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return (exe, $"\"{entryAssemblyPath}\"");
        }

        if (exe.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return ("dotnet", $"\"{exe}\"");
        }

        return (exe, string.Empty);
    }

    private static bool IsWindowsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> ExecuteCommandRaw(string cmd)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c {cmd}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var p = Process.Start(psi);
        if (p == null)
            throw new InvalidOperationException("Не удалось запустить команду.");

        string output = await p.StandardOutput.ReadToEndAsync();
        string error = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();

        if (p.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim());

        return output.Trim();
    }
#endif

    public static async Task SetupLinuxAutostart()
    {
        // Выполняем только на Linux
        if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux))
            return;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var servicePath = Path.Combine(home, ".config", "systemd", "user", "cotebopc.service");
        var exe = Process.GetCurrentProcess().MainModule?.FileName ?? "";
        var entryAssembly = System.Reflection.Assembly.GetEntryAssembly();
        var assemblyPath = AppContext.BaseDirectory;
        if (entryAssembly != null)
        {
            var assemblyName = entryAssembly.GetName().Name;
            if (!string.IsNullOrEmpty(assemblyName))
                assemblyPath = Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");
        }

        var execStart = exe.EndsWith("dotnet", StringComparison.OrdinalIgnoreCase) || exe.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase)
            ? $"dotnet \"{assemblyPath}\""
            : $"\"{exe}\"";
        var workingDirectory = Path.GetDirectoryName(assemblyPath) ?? home;

        var content = $@"[Unit]
Description=CoteboPC Telegram Remote Control
After=network.target

[Service]
Type=simple
WorkingDirectory={workingDirectory}
ExecStart={execStart}
Restart=always
RestartSec=10

[Install]
WantedBy=default.target";

        Directory.CreateDirectory(Path.GetDirectoryName(servicePath)!);
        await File.WriteAllTextAsync(servicePath, content);

        await RunCommand("systemctl --user daemon-reload");
        await RunCommand("systemctl --user enable cotebopc.service");
        await RunCommand("systemctl --user start cotebopc.service");
    }

    public static async Task RemoveLinuxAutostart()
    {
        // Выполняем только на Linux
        if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux))
            return;

        await RunCommand("systemctl --user stop cotebopc.service");
        await RunCommand("systemctl --user disable cotebopc.service");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var servicePath = Path.Combine(home, ".config", "systemd", "user", "cotebopc.service");
        if (File.Exists(servicePath))
            File.Delete(servicePath);

        await RunCommand("systemctl --user daemon-reload");
    }

    private static async Task RunCommand(string cmd)
    {
        var psi = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new ProcessStartInfo("cmd.exe", $"/c {cmd}") { RedirectStandardOutput = true, UseShellExecute = false }
            : new ProcessStartInfo("bash", $"-c \"{cmd}\"") { RedirectStandardOutput = true, UseShellExecute = false };

        using var p = Process.Start(psi);
        if (p != null)
            await p.WaitForExitAsync();
    }
}
