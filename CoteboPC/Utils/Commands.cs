using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using CoteboPC;

#if WINDOWS
using System.Windows.Forms;
#endif


namespace CoteboPC.Utils;

public static class Commands
{
    /// <summary>
    /// 4. Управление медиаплеером
    /// </summary>
    public static async Task<string> MediaControl(string action)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
#if WINDOWS
                var cmd = action.ToLower() switch
                {
                    "play" or "pause" => VK_MEDIA_PLAY_PAUSE,
                    "next" => VK_MEDIA_NEXT_TRACK,
                    "prev" => VK_MEDIA_PREV_TRACK,
                    "focus" => (ushort)0,
                    _ => (ushort)0
                };

                if (action.Equals("focus", StringComparison.OrdinalIgnoreCase))
                {
                    var success = FocusMediaPlayerWindow();
                    if (!success)
                        SendMediaKey(VK_MEDIA_PLAY_PAUSE);

                    return $"✅ Плеер: {action}";
                }

                if (cmd == 0)
                    return "❌ Неизвестное действие плеера";

                SendMediaKey(cmd);
                return $"✅ Плеер: {action}";
#else
                return "❌ Управление плеером не реализовано для Windows";
#endif
            }

            if (!await HasTool("playerctl"))
                return "❌ Утилита playerctl не установлена. Установите: sudo apt install playerctl";

            string linuxCmd = action.ToLower() switch
            {
                "play" => "playerctl play",
                "pause" => "playerctl pause",
                "next" => "playerctl next",
                "prev" => "playerctl previous",
                "focus" => "playerctl -l | head -1 | xargs -I {} xdotool search --name {} windowactivate",
                _ => string.Empty
            };

            if (string.IsNullOrEmpty(linuxCmd))
                return "❌ Неизвестное действие плеера";

            await ExecuteConsole(linuxCmd);
            return $"✅ Плеер: {action}";
        }
        catch (Exception ex)
        {
            return $"❌ Ошибка управления плеером: {ex.Message}";
        }
    }

    private static bool IsWayland => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    private static async Task<bool> HasTool(string tool)
    {
        try
        {
            var output = await ExecuteCommandRaw($"which {tool}");
            return !string.IsNullOrWhiteSpace(output) && output.Trim().EndsWith(tool, StringComparison.Ordinal);
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
            FileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "/bin/bash",
            Arguments = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? $"/c {cmd}" : $"-c \"{cmd}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null)
            throw new InvalidOperationException("Не удалось запустить команду.");

        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? $"Команда завершилась кодом {process.ExitCode}" : error.Trim());

        return output.Trim();
    }

    private static async Task<(int Width, int Height)?> GetScreenGeometry()
    {
        if (await HasTool("xdotool"))
        {
            var output = await ExecuteCommandRaw("xdotool getdisplaygeometry");
            var parts = output.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && int.TryParse(parts[0], out var width) && int.TryParse(parts[1], out var height))
                return (width, height);
        }

        return null;
    }

    private static async Task<string> ExecuteInputCommand(string xdotoolCommand, string kdotoolCommand)
    {
        if (IsWayland && await HasTool("kdotool"))
            return await ExecuteConsole(kdotoolCommand);

        if (await HasTool("xdotool"))
            return await ExecuteConsole(xdotoolCommand);

        throw new InvalidOperationException("Не найдены инструменты для управления вводом (xdotool или kdotool). Установите один из них.");
    }

    /// <summary>
    /// 3. Отправка скриншота текущего экрана
    /// </summary>
    public static async Task<string> TakeScreenshot(ITelegramBotClient bot, long chatId)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
#if WINDOWS
                // Захват всего экрана на Windows
                var primaryScreen = Screen.PrimaryScreen;
                if (primaryScreen == null)
                    return "❌ Не удалось получить экран для скриншота.";

                var screenBounds = primaryScreen.Bounds;
                var bmp = new Bitmap(screenBounds.Width, screenBounds.Height);
                using var graphics = Graphics.FromImage(bmp);
                graphics.CopyFromScreen(0, 0, 0, 0, bmp.Size);

                using var ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Png);
                ms.Position = 0;

                await bot.SendPhoto(chatId, new InputFileStream(ms, "screenshot.png"));
                return "✅ Скриншот текущего экрана отправлен";
#endif
            }

            string tool;
            string args;
            var tempFile = Path.Combine(Path.GetTempPath(), $"cotebo_screenshot_{DateTime.Now.Ticks}.png");

            if (await HasTool("spectacle"))
            {
                tool = "spectacle";
                args = $"-b -n -o {tempFile}";
            }
            else if (await HasTool("grim"))
            {
                tool = "grim";
                args = tempFile;
            }
            else if (await HasTool("scrot"))
            {
                tool = "scrot";
                args = $"--silent {tempFile}";
            }
            else
            {
                return "❌ Не установлены утилиты для скриншота. Установите spectacle, grim или scrot.";
            }

            var process = Process.Start(new ProcessStartInfo
            {
                FileName = tool,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (process == null)
                return "❌ Не удалось запустить утилиту для скриншота.";

            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync();
                return $"❌ Ошибка при создании скриншота: {error.Trim()}";
            }

            if (!File.Exists(tempFile))
                return "❌ Не удалось сохранить скриншот.";

            using var fs = File.OpenRead(tempFile);
            await bot.SendPhoto(chatId, new InputFileStream(fs, "screenshot.png"));
            File.Delete(tempFile);
            return "✅ Скриншот текущего экрана отправлен";
        }
        catch (Exception ex)
        {
            return $"❌ Ошибка при создании скриншота: {ex.Message}";
        }
    }

    /// <summary>
    /// 5. Вывод информации о системе
    /// </summary>
    public static async Task<string> GetSystemInfo()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"🖥 Операционная система: {RuntimeInformation.OSDescription}");
        sb.AppendLine($"🖥 Имя хоста: {Dns.GetHostName()}");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                sb.AppendLine($"🖥 CPU: {await GetWindowsCpuUsage()}%");
            }
            catch
            {
                sb.AppendLine("🖥 CPU: Н/Д");
            }

            try
            {
                sb.AppendLine($"💾 RAM: {GetWindowsMemoryInfo()}");
            }
            catch
            {
                sb.AppendLine("💾 RAM: Н/Д");
            }

            try
            {
                sb.AppendLine($"💿 Disk: {GetWindowsDiskInfo()}");
            }
            catch
            {
                sb.AppendLine("💿 Disk: Н/Д");
            }

            sb.AppendLine($"⏱ Uptime: {TimeSpan.FromMilliseconds(Environment.TickCount64).ToString("dd\\:hh\\:mm\\:ss")}" );
        }
        else
        {
            try
            {
                var cpuUsage = await ExecuteConsole("top -bn1 | grep 'Cpu(s)' | sed 's/.* , *\\([0-9.]*\\)%* id.*/\\1/' | awk '{print 100 - $1}'");
                sb.AppendLine($"🖥 CPU: {cpuUsage.Trim()}%");
            }
            catch
            {
                sb.AppendLine("🖥 CPU: Н/Д");
            }

            try
            {
                var memInfo = await ExecuteConsole("free -h | grep Mem | awk '{print $3 \"/\" $2}'");
                sb.AppendLine($"💾 RAM: {memInfo.Trim()}");
            }
            catch
            {
                sb.AppendLine($"💾 RAM: {GC.GetTotalMemory(false) / (1024 * 1024)} МБ (GC)");
            }

            try
            {
                var diskInfo = await ExecuteConsole("df -h / | tail -1 | awk '{print $3 \"/\" $2 \" (\" $5 \")\"}'");
                sb.AppendLine($"💿 Disk: {diskInfo.Trim()}");
            }
            catch
            {
                sb.AppendLine("💿 Disk: Н/Д");
            }

            sb.AppendLine($"⏱ Uptime: {Environment.TickCount64 / 3600000} часов");
        }

        sb.AppendLine($"🌐 .NET версия: {RuntimeInformation.FrameworkDescription}");
        return sb.ToString();
    }

    /// <summary>
    /// 6. Управление мышью
    /// </summary>
    public static async Task<string> MouseControl(string action, int x = 0, int y = 0)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
#if WINDOWS
                switch (action.ToLower())
                {
                    case "move":
                        if (x < 0 || y < 0)
                            return "❌ Укажите координаты для перемещения, например: 100 200";

                        SetCursorPos(x, y);
                        break;
                    case "center":
                    {
                        var primary = Screen.PrimaryScreen;
                        if (primary == null)
                            return "❌ Не удалось получить экран для центрирования мыши.";

                        var centerX = primary.Bounds.Width / 2;
                        var centerY = primary.Bounds.Height / 2;
                        SetCursorPos(centerX, centerY);
                        break;
                    }
                    case "click":
                        MouseClick();
                        break;
                    case "rightclick":
                        MouseRightClick();
                        break;
                    case "scrollup":
                        MouseWheel(120);
                        break;
                    case "scrolldown":
                        MouseWheel(-120);
                        break;
                    default:
                        return "❌ Неизвестное действие мыши";
                }

                return $"✅ Мышь: {action}";
#else
                return "❌ Управление мышью не реализовано для Windows";
#endif
            }

            if (!await HasTool("xdotool") && !await HasTool("kdotool"))
                return "❌ Не найдены инструменты управления мышью. Установите xdotool или kdotool.";

            switch (action.ToLower())
            {
                case "move":
                    if (x < 0 || y < 0)
                        return "❌ Укажите координаты для перемещения, например: 100 200";

                    if (IsWayland && await HasTool("kdotool"))
                        await ExecuteConsole($"kdotool mouse move {x} {y}");
                    else
                        await ExecuteConsole($"xdotool mousemove {x} {y}");
                    break;
                case "center":
                {
                    var geometry = await GetScreenGeometry();
                    if (!geometry.HasValue)
                        return "❌ Не удалось получить разрешение экрана для центрирования мыши.";

                    var centerX = geometry.Value.Width / 2;
                    var centerY = geometry.Value.Height / 2;
                    if (IsWayland && await HasTool("kdotool"))
                        await ExecuteConsole($"kdotool mouse move {centerX} {centerY}");
                    else
                        await ExecuteConsole($"xdotool mousemove {centerX} {centerY}");
                    break;
                }
                case "click":
                    await ExecuteInputCommand("xdotool click 1", "kdotool mouse click 1");
                    break;
                case "rightclick":
                    await ExecuteInputCommand("xdotool click 3", "kdotool mouse click 3");
                    break;
                case "scrollup":
                    await ExecuteInputCommand("xdotool click 4", "kdotool mouse wheel up 1");
                    break;
                case "scrolldown":
                    await ExecuteInputCommand("xdotool click 5", "kdotool mouse wheel down 1");
                    break;
                default:
                    return "❌ Неизвестное действие мыши";
            }

            return $"✅ Мышь: {action}";
        }
        catch (Exception ex)
        {
            return $"❌ Ошибка управления мышью: {ex.Message}";
        }
    }

    /// <summary>
    /// 7. Отображение диалоговых уведомлений
    /// </summary>
    public static async Task<string> ShowNotification(string message)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
#if WINDOWS
                var notifyIcon = new NotifyIcon
                {
                    Visible = true,
                    Icon = SystemIcons.Application,
                    BalloonTipTitle = "CoteboPC",
                    BalloonTipText = message,
                    BalloonTipIcon = ToolTipIcon.Info
                };

                notifyIcon.ShowBalloonTip(5000);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(6000);
                    notifyIcon.Dispose();
                });

                return $"✅ Уведомление показано: {message}";
#else
                return "❌ Уведомления не реализованы для Windows";
#endif
            }

            if (await HasTool("notify-send"))
            {
                await ExecuteConsole($"notify-send 'CoteboPC' '{message}'");
                return $"✅ Уведомление показано: {message}";
            }

            if (await HasTool("kdialog"))
            {
                await ExecuteConsole($"kdialog --passivepopup '{message}' 5");
                return $"✅ Уведомление показано: {message}";
            }

            return "❌ Не найдена утилита для уведомлений. Установите libnotify-bin или kdialog.";
        }
        catch (Exception ex)
        {
            return $"❌ Ошибка показа уведомления: {ex.Message}";
        }
    }

    /// <summary>
    /// 9. Загрузка файла по прямой ссылке
    /// </summary>
    public static async Task<string> DownloadFile(string url, string destPath = "")
    {
        try
        {
            if (string.IsNullOrEmpty(destPath))
                destPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", Path.GetFileName(new Uri(url).LocalPath));

            using var client = new HttpClient();
            var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            await using var fs = new FileStream(destPath, FileMode.Create);
            await response.Content.CopyToAsync(fs);

            return $"✅ Файл загружен: {destPath}";
        }
        catch (Exception ex)
        {
            return $"❌ Ошибка загрузки файла: {ex.Message}";
        }
    }

    /// <summary>
    /// 14. Вставка текста в активное окно
    /// </summary>
    public static async Task<string> TypeText(string text)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
#if WINDOWS
                if (string.IsNullOrWhiteSpace(text))
                    return "❌ Укажите текст для ввода.";

                SendUnicodeText(text);
                return "✅ Текст вставлен";
#else
                return "❌ Вставка текста не реализована для Windows";
#endif
            }

            if (!await HasTool("xdotool") && !await HasTool("kdotool"))
                return "❌ Не найдены инструменты для вставки текста. Установите xdotool или kdotool.";

            // Switch to English layout (assuming 1 is English)
            int currentLayout = await GetCurrentKeyboardLayout();
            await SetKeyboardLayout(1);

            if (IsWayland && await HasTool("kdotool"))
                await ExecuteConsole($"kdotool keyboard type '{text}'");
            else
                await ExecuteConsole($"xdotool type '{text}'");

            // Switch back
            await SetKeyboardLayout(currentLayout);

            return "✅ Текст вставлен";
        }
        catch (Exception ex)
        {
            return $"❌ Ошибка вставки текста: {ex.Message}";
        }
    }

    private static async Task<int> GetCurrentKeyboardLayout()
    {
        try
        {
            if (await HasTool("gdbus") || await HasTool("qdbus") || await HasTool("qdbus-qt5"))
            {
                string command;
                if (await HasTool("gdbus"))
                    command = "gdbus call --session --dest org.kde.keyboard --object-path /Keyboard --method org.kde.keyboard.Keyboard.layout";
                else if (await HasTool("qdbus"))
                    command = "qdbus org.kde.keyboard /Keyboard org.kde.keyboard.Keyboard.layout";
                else
                    command = "qdbus-qt5 org.kde.keyboard /Keyboard org.kde.keyboard.Keyboard.layout";

                string result = await ExecuteConsole(command);
                // Result is like (uint32 1,)
                var match = System.Text.RegularExpressions.Regex.Match(result, @"uint32 (\d+)");
                if (match.Success && int.TryParse(match.Groups[1].Value, out int layout))
                    return layout;
            }
        }
        catch { }
        return 0; // Default to 0
    }

    private static async Task SetKeyboardLayout(int layout)
    {
        try
        {
            if (await HasTool("gdbus") || await HasTool("qdbus") || await HasTool("qdbus-qt5"))
            {
                string command;
                if (await HasTool("gdbus"))
                    command = $"gdbus call --session --dest org.kde.keyboard --object-path /Keyboard --method org.kde.keyboard.Keyboard.setLayout {layout}";
                else if (await HasTool("qdbus"))
                    command = $"qdbus org.kde.keyboard /Keyboard org.kde.keyboard.Keyboard.setLayout {layout}";
                else
                    command = $"qdbus-qt5 org.kde.keyboard /Keyboard org.kde.keyboard.Keyboard.setLayout {layout}";

                await ExecuteConsole(command);
            }
        }
        catch { }
    }

    /// <summary>
    /// 13. Включение/выключение Bluetooth
    /// </summary>
    public static async Task<string> BluetoothControl(string action)
    {
        try
        {
            var normalizedAction = action.Trim().ToLowerInvariant();
            if (normalizedAction == "bluetooth on" || normalizedAction == "on bluetooth")
                normalizedAction = "on";
            else if (normalizedAction == "bluetooth off" || normalizedAction == "off bluetooth")
                normalizedAction = "off";

            if (normalizedAction != "on" && normalizedAction != "off")
                return "❌ Укажите on или off для Bluetooth";

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
#if WINDOWS
                if (!IsAdministrator())
                {
                    return "❌ Управление Bluetooth требует запуска приложения от имени администратора.";
                }

                string lastError = string.Empty;
                var adapterNames = await GetBluetoothNetAdapterNames();
                if (adapterNames.Length > 0)
                {
                    var list = string.Join(",", adapterNames.Select(n => $"\"{n}\""));
                    var adapterCommand = normalizedAction == "on"
                        ? $"Enable-NetAdapter -Name {list} -Confirm:$false -ErrorAction Stop"
                        : $"Disable-NetAdapter -Name {list} -Confirm:$false -ErrorAction Stop";

                    lastError = await ExecutePowerShellCommand(adapterCommand);
                    if (string.IsNullOrWhiteSpace(lastError))
                        return $"✅ Bluetooth {normalizedAction}";
                }

                string psCommand = normalizedAction == "on"
                    ? "Get-PnpDevice -Class Bluetooth | Where-Object { $_.ConfigManagerErrorCode -eq 22 } | Enable-PnpDevice -Confirm:$false -ErrorAction Stop"
                    : "Get-PnpDevice -Class Bluetooth | Where-Object { $_.ConfigManagerErrorCode -eq 0 } | Disable-PnpDevice -Confirm:$false -ErrorAction Stop";

                lastError = await ExecutePowerShellCommand(psCommand);
                if (string.IsNullOrWhiteSpace(lastError))
                    return $"✅ Bluetooth {normalizedAction}";

                return $"❌ Bluetooth {normalizedAction} не удалось: {lastError}";
#else
                return "❌ Bluetooth не реализован для Windows";
#endif
            }

            if (!await HasTool("bluetoothctl"))
                return "❌ Утилита bluetoothctl не установлена. Установите: sudo apt install bluez";

            string cmd = normalizedAction == "on"
                ? "printf 'power on\\nquit\\n' | bluetoothctl"
                : "printf 'power off\\nquit\\n' | bluetoothctl";

            await ExecuteConsole(cmd);
            return $"✅ Bluetooth {normalizedAction}";
        }
        catch (Exception ex)
        {
            return $"❌ Ошибка управления Bluetooth: {ex.Message}";
        }
    }

#if WINDOWS
    private static async Task<string[]> GetBluetoothNetAdapterNames()
    {
        try
        {
            var output = await ExecuteCommandRaw("powershell -NoProfile -NonInteractive -Command \"Get-NetAdapter | Where-Object { $_.InterfaceDescription -match 'Bluetooth' -or $_.Name -match 'Bluetooth' } | Select-Object -ExpandProperty Name\"");
            return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(n => n.Trim())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static async Task<string> ExecutePowerShellCommand(string command)
    {
        try
        {
            await ExecuteCommandRaw($"powershell -NoProfile -NonInteractive -Command \"{command}\"");
            return string.Empty;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
#endif

#if WINDOWS
    private static async Task<string> ExecutePowerShell(string command)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{command}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null)
            throw new InvalidOperationException("Не удалось запустить PowerShell.");

        await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return error.Trim();
    }

    private static bool IsAdministrator()
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
#endif

    private static async Task<string> SendKdeDesktopCommand(string action)
    {
        string command;
        if (await HasTool("gdbus"))
        {
            command = action.ToLower() switch
            {
                "next" => "gdbus call --session --dest org.kde.KWin --object-path /KWin --method org.kde.KWin.nextDesktop",
                "prev" => "gdbus call --session --dest org.kde.KWin --object-path /KWin --method org.kde.KWin.previousDesktop",
                _ => string.Empty
            };
        }
        else if (await HasTool("qdbus"))
        {
            command = action.ToLower() switch
            {
                "next" => "qdbus org.kde.KWin /KWin org.kde.KWin.nextDesktop",
                "prev" => "qdbus org.kde.KWin /KWin org.kde.KWin.previousDesktop",
                _ => string.Empty
            };
        }
        else if (await HasTool("qdbus-qt5"))
        {
            command = action.ToLower() switch
            {
                "next" => "qdbus-qt5 org.kde.KWin /KWin org.kde.KWin.nextDesktop",
                "prev" => "qdbus-qt5 org.kde.KWin /KWin org.kde.KWin.previousDesktop",
                _ => string.Empty
            };
        }
        else
        {
            throw new InvalidOperationException("Не найдена утилита для KDE D-Bus (gdbus или qdbus). Установите qdbus-qt5 или qdbus.");
        }

        if (string.IsNullOrEmpty(command))
            throw new InvalidOperationException("Создание нового рабочего стола в KDE через D-Bus не поддерживается. Используйте wmctrl или настройте вручную.");

        await ExecuteCommandRaw(command);
        return string.Empty;
    }

    /// <summary>
    /// 16. Переключение рабочих столов
    /// </summary>
    public static async Task<string> DesktopControl(string action)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
#if WINDOWS
                switch (action.ToLower())
                {
                    case "new":
                        SendKeyCombo(VK_LWIN, VK_CONTROL, VK_D);
                        break;
                    case "next":
                        SendKeyCombo(VK_LWIN, VK_CONTROL, VK_RIGHT);
                        break;
                    case "prev":
                        SendKeyCombo(VK_LWIN, VK_CONTROL, VK_LEFT);
                        break;
                    default:
                        return "❌ Неизвестное действие десктопа";
                }

                return $"✅ Десктоп: {action}";
#else
                return "❌ Управление десктопами не реализовано для Windows";
#endif
            }

            if (action.ToLower() == "new")
            {
                if (await HasTool("gdbus") || await HasTool("qdbus") || await HasTool("qdbus-qt5"))
                {
                    string command;
                    if (await HasTool("gdbus"))
                    {
                        command = "gdbus call --session --dest org.kde.KWin --object-path /VirtualDesktopManager --method org.kde.KWin.VirtualDesktopManager.Create ''";
                    }
                    else if (await HasTool("qdbus"))
                    {
                        command = "qdbus org.kde.KWin /VirtualDesktopManager org.kde.KWin.VirtualDesktopManager.Create ''";
                    }
                    else
                    {
                        command = "qdbus-qt5 org.kde.KWin /VirtualDesktopManager org.kde.KWin.VirtualDesktopManager.Create ''";
                    }

                    await ExecuteCommandRaw(command);
                    return $"✅ Десктоп: {action}";
                }

                if (!await HasTool("wmctrl"))
                    return "❌ Утилита wmctrl не установлена. Установите: sudo apt install wmctrl";

                await ExecuteConsole("wmctrl -n $(($(wmctrl -d | wc -l) + 1))");
                return $"✅ Десктоп: {action}";
            }

            if (await HasTool("gdbus") || await HasTool("qdbus") || await HasTool("qdbus-qt5"))
            {
                await SendKdeDesktopCommand(action);
                return $"✅ Десктоп: {action}";
            }

            if (!await HasTool("wmctrl"))
                return "❌ Утилита wmctrl не установена. Установите: sudo apt install wmctrl";

            string cmd = action.ToLower() switch
            {
                "next" => "xdotool key ctrl+alt+Right",
                "prev" => "xdotool key ctrl+alt+Left",
                _ => string.Empty
            };

            if (string.IsNullOrEmpty(cmd))
                return "❌ Неизвестное действие десктопа";

            await ExecuteConsole(cmd);
            return $"✅ Десктоп: {action}";
        }
        catch (Exception ex)
        {
            return $"❌ Ошибка управления десктопами: {ex.Message}";
        }
    }

    public static async Task<string> GetIpAddresses()
    {
        try
        {
            var addresses = Dns.GetHostAddresses(Dns.GetHostName())
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.ToString())
                .Distinct()
                .ToArray();

            if (!addresses.Any())
                return "❌ Не удалось получить IP-адреса.";

            return "🌐 IP-адреса: " + string.Join(", ", addresses);
        }
        catch (Exception ex)
        {
            return $"❌ Ошибка при получении IP: {ex.Message}";
        }
    }

    public static async Task<string> GetProcessesList()
    {
        try
        {
            var processes = Process.GetProcesses()
                .OrderBy(p => p.ProcessName)
                .Take(16)
                .Select(p => $"{p.Id}: {p.ProcessName}")
                .ToArray();

            return processes.Length == 0
                ? "❌ Не удалось получить список процессов."
                : "🧠 Текущие процессы:\n" + string.Join("\n", processes);
        }
        catch (Exception ex)
        {
            return $"❌ Ошибка получения процессов: {ex.Message}";
        }
    }

    public static async Task<string> OpenPath(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return "❌ Укажите путь или URL для открытия.";

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c start \"\" \"{path}\"") { CreateNoWindow = true, UseShellExecute = false });
            }
            else
            {
                Process.Start(new ProcessStartInfo("xdg-open", path) { UseShellExecute = false });
            }

            return $"✅ Открываю: {path}";
        }
        catch (Exception ex)
        {
            return $"❌ Не удалось открыть путь: {ex.Message}";
        }
    }

    /// <summary>
    /// 11. Выключение ПК
    /// </summary>
    public static async Task<string> Shutdown()
    {
        try
        {
            RunPowerCommand("shutdown");
            return "✅ Команда выключения отправлена";
        }
        catch { return "❌ Не удалось выполнить выключение"; }
    }

    /// <summary>
    /// 11. Перезагрузка ПК
    /// </summary>
    public static async Task<string> Reboot()
    {
        try
        {
            RunPowerCommand("reboot");
            return "✅ Команда перезагрузки отправлена";
        }
        catch { return "❌ Не удалось выполнить перезагрузку"; }
    }

    /// <summary>
    /// 11. Спящий режим
    /// </summary>
    public static async Task<string> Sleep()
    {
        try
        {
            RunPowerCommand("suspend");
            return "✅ Команда спящего режима отправлена";
        }
        catch { return "❌ Не удалось выполнить спящий режим"; }
    }

    private static void RunPowerCommand(string action)
    {
        ProcessStartInfo psi;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            psi = action switch
            {
                "reboot" => new ProcessStartInfo("shutdown", "/r /t 0"),
                "shutdown" => new ProcessStartInfo("shutdown", "/s /t 0"),
                "suspend" => new ProcessStartInfo("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0"),
                _ => new ProcessStartInfo("shutdown", "/s /t 0")
            };
        }
        else
        {
            psi = new ProcessStartInfo("bash", $"-c \"{action}\"");
        }

        psi.UseShellExecute = false;
        Process.Start(psi);
    }

    /// <summary>
    /// 12. Выполнение произвольной консольной команды
    /// Используется после выбора кнопки Exec и ввода текста команды.
    /// </summary>
    public static async Task<string> ExecuteConsole(string cmd)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "/bin/bash",
                Arguments = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? $"/c {cmd}" : $"-c \"{cmd}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                throw new InvalidOperationException("Не удалось запустить команду.");

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            var waitTask = process.WaitForExitAsync();
            var completedTask = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(20)));

            if (completedTask != waitTask)
            {
                try
                {
                    process.Kill(true);
                }
                catch { }

                return "❌ Время выполнения команды истекло.";
            }

            string output = await outputTask;
            string error = await errorTask;

            return string.IsNullOrWhiteSpace(error) 
                ? output.Trim() + "\n✅ Команда выполнена" 
                : $"Вывод:\n{output}\nОшибка:\n{error}";
        }
        catch (Exception ex)
        {
            return $"❌ Ошибка выполнения команды: {ex.Message}";
        }
    }

    /// <summary>
    /// 15. Скрытие всех окон (минимизация)
    /// </summary>
    public static string MinimizeAll()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start("powershell", "(New-Object -ComObject Shell.Application).MinimizeAll()");
            }
            else
            {
                Process.Start("xdotool", "key super+d");
            }
            return "✅ Все окна свёрнуты";
        }
        catch
        {
            return "❌ Не удалось свернуть окна";
        }
    }

    /// <summary>
    /// 17. Управление презентацией
    /// </summary>
    public static async Task<string> PresentationControl(string action)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
#if WINDOWS
                bool focused = FocusPresentationWindow();
                if (!focused && action.ToLower() != "start")
                {
                    // Если презентация не сфокусирована, всё равно посылаем клавишу.
                    Logger.Log("[Commands] Presentation window not focused before key send.");
                }

                switch (action.ToLower())
                {
                    case "next":
                        SendKey(VK_SPACE);
                        break;
                    case "prev":
                        SendKey(VK_LEFT);
                        break;
                    case "start":
                        SendKey(VK_F5);
                        break;
                    case "end":
                        SendKey(VK_ESCAPE);
                        break;
                    default:
                        return "❌ Неизвестное действие презентации";
                }

                return $"✅ Презентация: {action}";
#else
                return "❌ Управление презентацией не реализовано для Windows";
#endif
            }

            if (!await HasTool("xdotool") && !await HasTool("kdotool"))
                return "❌ Не найдены инструменты управления презентацией. Установите xdotool или kdotool.";

            switch (action.ToLower())
            {
                case "next":
                    await ExecuteInputCommand("xdotool key space", "kdotool keyboard key space");
                    break;
                    case "prev":
                    await ExecuteInputCommand("xdotool key Page_Up", "kdotool keyboard key Page_Up");
                    break;
                case "start":
                    await ExecuteInputCommand("xdotool key F5", "kdotool keyboard key F5");
                    break;
                case "end":
                    await ExecuteInputCommand("xdotool key Escape", "kdotool keyboard key Escape");
                    break;
                default:
                    return "❌ Неизвестное действие презентации";
            }

            return $"✅ Презентация: {action}";
        }
        catch (Exception ex)
        {
            return $"❌ Ошибка управления презентацией: {ex.Message}";
        }
    }

#if WINDOWS
    private const ushort VK_MEDIA_PLAY_PAUSE = 0xB3;
    private const ushort VK_MEDIA_NEXT_TRACK = 0xB0;
    private const ushort VK_MEDIA_PREV_TRACK = 0xB1;
    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_LEFT = 0x25;
    private const ushort VK_RIGHT = 0x27;
    private const ushort VK_D = 0x44;
    private const ushort VK_SPACE = 0x20;
    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_F5 = 0x74;
    private const ushort VK_ESCAPE = 0x1B;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, int dwData, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private static void SendMediaKey(ushort key)
    {
        var inputs = new[]
        {
            new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = key, wScan = 0, dwFlags = 0, dwExtraInfo = UIntPtr.Zero } } },
            new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = key, wScan = 0, dwFlags = KEYEVENTF_KEYUP, dwExtraInfo = UIntPtr.Zero } } }
        };

        SendInput((uint)inputs.Length, inputs, INPUT.Size);
    }

    private static bool FocusMediaPlayerWindow()
    {
        var names = new[] { "spotify", "vlc", "wmplayer", "itunes", "chrome", "msedge", "firefox", "audacity", "powerpnt" };

        foreach (var process in Process.GetProcesses().Where(p => names.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase) && p.MainWindowHandle != IntPtr.Zero))
        {
            if (SetForegroundWindow(process.MainWindowHandle))
                return true;
        }

        return false;
    }

    private static bool FocusPresentationWindow()
    {
        foreach (var process in Process.GetProcessesByName("POWERPNT").Where(p => p.MainWindowHandle != IntPtr.Zero))
        {
            if (SetForegroundWindow(process.MainWindowHandle))
                return true;
        }

        return false;
    }

    private static void SendUnicodeText(string text)
    {
        var inputs = new List<INPUT>();
        foreach (var ch in text)
        {
            inputs.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = KEYEVENTF_UNICODE, dwExtraInfo = UIntPtr.Zero } } });
            inputs.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP, dwExtraInfo = UIntPtr.Zero } } });
        }

        SendInput((uint)inputs.Count, inputs.ToArray(), INPUT.Size);
    }

    private static void SendKey(ushort vk)
    {
        var inputs = new[] {
            new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = 0, dwFlags = 0, dwExtraInfo = UIntPtr.Zero } } },
            new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = 0, dwFlags = KEYEVENTF_KEYUP, dwExtraInfo = UIntPtr.Zero } } }
        };

        SendInput((uint)inputs.Length, inputs, INPUT.Size);
    }

    private static void SendKeyCombo(params ushort[] keys)
    {
        var inputs = new List<INPUT>();
        foreach (var key in keys)
            inputs.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = key, wScan = 0, dwFlags = 0, dwExtraInfo = UIntPtr.Zero } } });

        foreach (var key in keys.Reverse())
            inputs.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = key, wScan = 0, dwFlags = KEYEVENTF_KEYUP, dwExtraInfo = UIntPtr.Zero } } });

        SendInput((uint)inputs.Count, inputs.ToArray(), INPUT.Size);
    }

    private static void MouseClick() => mouse_event(MOUSEEVENTF_LEFTDOWN | MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
    private static void MouseRightClick() => mouse_event(MOUSEEVENTF_RIGHTDOWN | MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);
    private static void MouseWheel(int delta) => mouse_event(MOUSEEVENTF_WHEEL, 0, 0, delta, UIntPtr.Zero);

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
        public static int Size => Marshal.SizeOf<INPUT>();
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }
#endif

    private static async Task<string> GetWindowsCpuUsage()
    {
        if (!GetSystemTimes(out var idle1, out var kernel1, out var user1))
            return "0.0";

        await Task.Delay(500);

        if (!GetSystemTimes(out var idle2, out var kernel2, out var user2))
            return "0.0";

        var idle = FileTimeToUInt64(idle2) - FileTimeToUInt64(idle1);
        var kernel = FileTimeToUInt64(kernel2) - FileTimeToUInt64(kernel1);
        var user = FileTimeToUInt64(user2) - FileTimeToUInt64(user1);
        var total = kernel + user;

        if (total == 0)
            return "0.0";

        var usage = (double)(total - idle) * 100.0 / total;
        return usage.ToString("0.0", CultureInfo.InvariantCulture);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    private static ulong FileTimeToUInt64(FILETIME time)
    {
        return ((ulong)time.dwHighDateTime << 32) | time.dwLowDateTime;
    }

    private static string GetWindowsMemoryInfo()
    {
        var memStatus = new MEMORYSTATUSEX();
        memStatus.dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
        GlobalMemoryStatusEx(ref memStatus);
        var total = memStatus.ullTotalPhys;
        var avail = memStatus.ullAvailPhys;
        return $"{FormatBytes(total - avail)} / {FormatBytes(total)}";
    }

    private static string GetWindowsDiskInfo()
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var drive = new DriveInfo(root);
        if (!drive.IsReady)
            return "Н/Д";

        var total = (ulong)drive.TotalSize;
        var free = (ulong)drive.TotalFreeSpace;
        var used = total - free;
        return $"{FormatBytes(used)} / {FormatBytes(total)} ({(int)(free * 100.0 / total)}% свободно)";
    }

    private static string FormatBytes(ulong bytes)
    {
        string[] suffixes = { "Б", "КБ", "МБ", "ГБ", "ТБ" };
        var index = 0;
        double value = bytes;
        while (value >= 1024 && index < suffixes.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return $"{value:0.##} {suffixes[index]}";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    /// <summary>
    /// Основная клавиатура команд
    /// </summary>
    public static ReplyKeyboardMarkup GetCommandsKeyboard()
    {
        return new ReplyKeyboardMarkup(new[]
        {
            new[]
            {
                new KeyboardButton("🔍 Ping"),
                new KeyboardButton("📸 Screenshot"),
                new KeyboardButton("🖥 Sysinfo")
            },
            new[]
            {
                new KeyboardButton("🎵 Media"),
                new KeyboardButton("🖱 Mouse"),
                new KeyboardButton("⚡ Power")
            },
            new[]
            {
                new KeyboardButton("📁 Files"),
                new KeyboardButton("🔧 More"),
                new KeyboardButton("📋 Minimize")
            }
        })
        {
            ResizeKeyboard = true,
            OneTimeKeyboard = false,
            Selective = false
        };
    }

    /// <summary>
    /// Клавиатура для медиа
    /// </summary>
    public static ReplyKeyboardMarkup GetMediaKeyboard()
    {
        return new ReplyKeyboardMarkup(new[]
        {
            new[]
            {
                new KeyboardButton("🎵 Play"),
                new KeyboardButton("⏸ Pause"),
                new KeyboardButton("⏭ Next")
            },
            new[]
            {
                new KeyboardButton("⏮ Prev"),
                new KeyboardButton("🎯 Focus"),
                new KeyboardButton("⬅ Back")
            }
        })
        {
            ResizeKeyboard = true,
            OneTimeKeyboard = false,
            Selective = false
        };
    }

    /// <summary>
    /// Клавиатура для мыши
    /// </summary>
    public static ReplyKeyboardMarkup GetMouseKeyboard()
    {
        return new ReplyKeyboardMarkup(new[]
        {
            new[]
            {
                new KeyboardButton("👆 Click"),
                new KeyboardButton("👉 Right Click"),
                new KeyboardButton("⬆ Scroll Up")
            },
            new[]
            {
                new KeyboardButton("⬇ Scroll Down"),
                new KeyboardButton("📍 Move Center"),
                new KeyboardButton("📍 Move Custom")
            },
            new[]
            {
                new KeyboardButton("⬅ Back")
            }
        })
        {
            ResizeKeyboard = true,
            OneTimeKeyboard = false,
            Selective = false
        };
    }

    /// <summary>
    /// Клавиатура для питания
    /// </summary>
    public static ReplyKeyboardMarkup GetPowerKeyboard()
    {
        return new ReplyKeyboardMarkup(new[]
        {
            new[]
            {
                new KeyboardButton("⚡ Shutdown"),
                new KeyboardButton("🔄 Reboot"),
                new KeyboardButton("😴 Sleep")
            },
            new[]
            {
                new KeyboardButton("⬅ Back")
            }
        })
        {
            ResizeKeyboard = true,
            OneTimeKeyboard = false,
            Selective = false
        };
    }

    /// <summary>
    /// Клавиатура для файлов
    /// </summary>
    public static ReplyKeyboardMarkup GetFilesKeyboard()
    {
        return new ReplyKeyboardMarkup(new[]
        {
            new[]
            {
                new KeyboardButton("📂 Open Path"),
                new KeyboardButton("⬇ Download URL"),
                new KeyboardButton("⬅ Back")
            }
        })
        {
            ResizeKeyboard = true,
            OneTimeKeyboard = false,
            Selective = false
        };
    }

    /// <summary>
    /// Клавиатура для дополнительных команд
    /// </summary>
    public static ReplyKeyboardMarkup GetMoreKeyboard()
    {
        return new ReplyKeyboardMarkup(new[]
        {
            new[]
            {
                new KeyboardButton("🌐 IP-Address"),
                new KeyboardButton("🧠 Processes"),
                new KeyboardButton("💻 Exec")
            },
            new[]
            {
                new KeyboardButton("🔔 Notify"),
                new KeyboardButton("⌨ Type Text"),
                new KeyboardButton("📶 Bluetooth")
            },
            new[]
            {
                new KeyboardButton("🖥 Desktop Menu"),
                new KeyboardButton("📊 Presentation Menu"),
                new KeyboardButton("⬅ Back")
            }
        })
        {
            ResizeKeyboard = true,
            OneTimeKeyboard = false,
            Selective = false
        };
    }

    /// <summary>
    /// Клавиатура для Bluetooth управления
    /// </summary>
    public static ReplyKeyboardMarkup GetBluetoothKeyboard()
    {
        return new ReplyKeyboardMarkup(new[]
        {
            new[]
            {
                new KeyboardButton("🔵 Bluetooth On"),
                new KeyboardButton("⚫ Bluetooth Off")
            },
            new[]
            {
                new KeyboardButton("⬅ Back")
            }
        })
        {
            ResizeKeyboard = true,
            OneTimeKeyboard = false,
            Selective = false
        };
    }

    /// <summary>
    /// Клавиатура для десктопов
    /// </summary>
    public static ReplyKeyboardMarkup GetDesktopKeyboard()
    {
        return new ReplyKeyboardMarkup(new[]
        {
            new[]
            {
                new KeyboardButton("🖥 Next Desktop"),
                new KeyboardButton("🖥 Prev Desktop"),
                new KeyboardButton("🖥 New Desktop")
            },
            new[]
            {
                new KeyboardButton("⬅ Back")
            }
        })
        {
            ResizeKeyboard = true,
            OneTimeKeyboard = false,
            Selective = false
        };
    }

    /// <summary>
    /// Клавиатура для презентаций
    /// </summary>
    public static ReplyKeyboardMarkup GetPresentationKeyboard()
    {
        return new ReplyKeyboardMarkup(new[]
        {
            new[]
            {
                new KeyboardButton("📊 Next Slide"),
                new KeyboardButton("📊 Prev Slide"),
                new KeyboardButton("📊 Start Presentation")
            },
            new[]
            {
                new KeyboardButton("📊 End Presentation"),
                new KeyboardButton("⬅ Back")
            }
        })
        {
            ResizeKeyboard = true,
            OneTimeKeyboard = false,
            Selective = false
        };
    }

    public static string Default(string command)
    {
        return $"✅ Команда '{command}' получена.\n\n" +
               "Используйте кнопки меню для навигации.\n\n" +
               "Основные возможности:\n" +
               "📸 Screenshot — скриншот\n" +
               "🖥 Sysinfo — информация о системе\n" +
               "📁 Files — работа с файлами\n" +
               "🎵 Media — управление музыкой\n\n" +
               "Возвращайтесь в главное меню кнопкой Back.";
    }
}