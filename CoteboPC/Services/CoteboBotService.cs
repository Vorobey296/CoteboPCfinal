using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CoteboPC.Utils;

namespace CoteboPC;

public static class CoteboBotService
{
    private static TelegramBotClient? _bot;
    private static AppConfig? _config;
    private static CancellationTokenSource? _cts;
    private static readonly object StartLock = new();
    private static readonly Dictionary<long, string> PendingActions = new();
    private static readonly Dictionary<long, string> CurrentMenu = new();

    public static async Task StartAsync(AppConfig config)
    {
        var tokenPreview = string.IsNullOrEmpty(config.Token)
            ? "<empty>"
            : config.Token[..Math.Min(10, config.Token.Length)];

        Logger.Log($"[Bot] StartAsync entered. Token: {tokenPreview}... ChatId: {config.ChatId}");
        try
        {
            var proc = Process.GetCurrentProcess();
            Logger.Log($"[Bot] Process info: PID={proc.Id}, CmdLine={Environment.CommandLine}, CWD={Environment.CurrentDirectory}");
        }
        catch (Exception exProc)
        {
            Logger.Log($"[Bot] Failed to log process info: {exProc}");
        }
        Logger.Log($"[Bot] _bot is currently: {(_bot == null ? "NULL (новый запуск)" : "инициализирован (пропускаем)")}" );

        lock (StartLock)
        {
            if (_bot != null && _cts != null && !_cts.IsCancellationRequested)
            {
                Logger.Log("[Bot] Bot already running, skipping initialization.");
                return;
            }

            _config = config;
        }

        if (string.IsNullOrEmpty(config.Token))
        {
            Logger.Log("[Bot] Error: token is null or empty.");
            throw new ArgumentException("Telegram bot token is required.", nameof(config.Token));
        }

        try
        {
            _bot = new TelegramBotClient(config.Token);
            Logger.Log("[Bot] TelegramBotClient created.");
            
            var me = await _bot.GetMe();
            Logger.Log($"[Bot] Authorized as @{me.Username}");
            
            await ClearPendingUpdatesAsync();
            
            _cts = new CancellationTokenSource();
            Logger.Log("[Bot] Starting receiving...");
            _bot.StartReceiving(HandleUpdateAsync, HandleErrorAsync, new ReceiverOptions(), _cts.Token);
            Logger.Log("[Bot] StartReceiving called.");
            
            await _bot.SendMessage(config.ChatId, "✅ CoteboPC успешно запущен и работает в фоне.");
            Logger.Log("[Bot] Startup message sent.");
        }
        catch (Exception ex)
        {
            _bot = null; // Очищаем при ошибке
            Logger.Log($"[Bot] Exception in StartAsync: {ex}");
            throw;
        }
    }

    public static void Stop()
    {
        try
        {
            if (_cts != null)
            {
                _cts.Cancel();
                _cts.Dispose();
                _cts = null;
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"[Bot] Exception in Stop: {ex}");
        }
        finally
        {
            _bot = null;
            _config = null;
        }
    }

    private static async Task RestartAsync(AppConfig config)
    {
        try
        {
            Logger.Log("[Bot] Restarting bot after polling error...");
            Stop();
            await StartAsync(config);
        }
        catch (Exception ex)
        {
            Logger.Log($"[Bot] RestartAsync failed: {ex}");
        }
    }

    private static async Task ClearPendingUpdatesAsync()
    {
        try
        {
            if (_bot == null)
                return;

            var updates = await _bot.GetUpdates(offset: -1, limit: 1, timeout: 0, cancellationToken: CancellationToken.None);
            if (updates != null && updates.Any())
            {
                var lastId = updates.Max(u => u.Id);
                await _bot.GetUpdates(offset: lastId + 1, limit: 1, timeout: 0, cancellationToken: CancellationToken.None);
                Logger.Log($"[Bot] Cleared pending updates up to {lastId}.");
            }
            else
            {
                Logger.Log("[Bot] No pending updates to clear.");
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"[Bot] Failed to clear pending updates: {ex}");
        }
    }

    private static async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        Logger.Log($"[Bot] Update received: {update.Type}");

        if (update.Message is not { } message || message.Chat is null || _config == null)
        {
            // Обработка файлов
            if (update.Message?.Document is { } document && update.Message.Chat is { } chat && _config != null && chat.Id == _config.ChatId)
            {
                try
                {
                    var file = await botClient.GetFile(document.FileId);
                    var destPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", document.FileName ?? "uploaded_file");
                    using var client = new HttpClient();
                    var fileStream = await client.GetStreamAsync($"https://api.telegram.org/file/bot{_config.Token}/{file.FilePath}");
                    await using var fs = new FileStream(destPath, FileMode.Create);
                    await fileStream.CopyToAsync(fs);
                    await botClient.SendMessage(chat.Id, $"✅ Файл загружен: {destPath}");
                }
                catch (Exception ex)
                {
                    await botClient.SendMessage(chat.Id, $"❌ Ошибка загрузки файла: {ex.Message}");
                }
                return;
            }

            Logger.Log("[Bot] Update ignored (no valid message or config null).");
            return;
        }

        var chatId = message.Chat.Id;
        var text = message.Text?.Trim() ?? string.Empty;
        Logger.Log($"[Bot] Message from {chatId}: {text}");
        if (chatId != _config.ChatId)
        {
            Logger.Log($"[Bot] Unauthorized chat id: {message.Chat.Id}");
            return;
        }

        string response;
        var lower = text.ToLowerInvariant();

        try
        {
            if (lower == "/start" || lower == "/help")
            {
                response = "Выберите команду ниже";
                await botClient.SendMessage(chatId, response, replyMarkup: Commands.GetCommandsKeyboard());
                Logger.Log("[Bot] Main keyboard sent");
                return;
            }

            if (text == "🎵 Media")
            {
                response = "Управление медиаплеером";
                CurrentMenu[chatId] = "media";
                await botClient.SendMessage(chatId, response, replyMarkup: Commands.GetMediaKeyboard());
                return;
            }

            if (text == "🖱 Mouse")
            {
                response = "Управление мышью";
                CurrentMenu[chatId] = "mouse";
                await botClient.SendMessage(chatId, response, replyMarkup: Commands.GetMouseKeyboard());
                return;
            }

            if (text == "⚡ Power")
            {
                response = "Управление питанием";
                CurrentMenu[chatId] = "power";
                await botClient.SendMessage(chatId, response, replyMarkup: Commands.GetPowerKeyboard());
                return;
            }

            if (text == "📁 Files")
            {
                response = "Работа с файлами";
                CurrentMenu[chatId] = "files";
                await botClient.SendMessage(chatId, response, replyMarkup: Commands.GetFilesKeyboard());
                return;
            }

            if (text == "🔧 More")
            {
                response = "Дополнительные команды";
                CurrentMenu[chatId] = "more";
                await botClient.SendMessage(chatId, response, replyMarkup: Commands.GetMoreKeyboard());
                return;
            }

            if (text == "🖥 Desktop Menu")
            {
                response = "Управление рабочими столами";
                CurrentMenu[chatId] = "desktop";
                await botClient.SendMessage(chatId, response, replyMarkup: Commands.GetDesktopKeyboard());
                return;
            }

            if (text == "📊 Presentation Menu")
            {
                response = "Управление презентацией";
                CurrentMenu[chatId] = "presentation";
                await botClient.SendMessage(chatId, response, replyMarkup: Commands.GetPresentationKeyboard());
                return;
            }

            if (text == "⬅ Back")
            {
                response = "Главное меню";
                CurrentMenu[chatId] = "main";
                await botClient.SendMessage(chatId, response, replyMarkup: Commands.GetCommandsKeyboard());
                PendingActions.Remove(chatId);
                return;
            }

            if (text == "📂 Open Path")
            {
                PendingActions[chatId] = "OpenPath";
                response = "Отправьте путь или URL для открытия";
                await botClient.SendMessage(chatId, response, replyMarkup: GetCurrentKeyboard(CurrentMenu.GetValueOrDefault(chatId, "main")));
                return;
            }

            if (text == "⬇ Download URL")
            {
                PendingActions[chatId] = "DownloadUrl";
                response = "Отправьте ссылку для скачивания";
                await botClient.SendMessage(chatId, response, replyMarkup: GetCurrentKeyboard(CurrentMenu.GetValueOrDefault(chatId, "main")));
                return;
            }

            if (text == "💻 Exec")
            {
                PendingActions[chatId] = "Exec";
                response = "Отправьте команду для выполнения";
                await botClient.SendMessage(chatId, response, replyMarkup: GetCurrentKeyboard(CurrentMenu.GetValueOrDefault(chatId, "main")));
                return;
            }

            if (text == "🔔 Notify")
            {
                PendingActions[chatId] = "Notify";
                response = "Отправьте текст уведомления";
                await botClient.SendMessage(chatId, response, replyMarkup: GetCurrentKeyboard(CurrentMenu.GetValueOrDefault(chatId, "main")));
                return;
            }

            if (text == "⌨ Type Text")
            {
                PendingActions[chatId] = "TypeText";
                response = "Отправьте текст, который нужно вставить";
                await botClient.SendMessage(chatId, response, replyMarkup: GetCurrentKeyboard(CurrentMenu.GetValueOrDefault(chatId, "main")));
                return;
            }

            if (text == "📶 Bluetooth")
            {
                response = "Управление Bluetooth";
                CurrentMenu[chatId] = "bluetooth";
                await botClient.SendMessage(chatId, response, replyMarkup: Commands.GetBluetoothKeyboard());
                return;
            }

            if (text == "🔵 Bluetooth On" || text == "⚫ Bluetooth Off" ||
                (CurrentMenu.GetValueOrDefault(chatId) == "bluetooth" && (lower == "on" || lower == "off")) ||
                lower == "bluetooth on" || lower == "bluetooth off" || lower == "on bluetooth" || lower == "off bluetooth")
            {
                var action = text.Contains("On", StringComparison.OrdinalIgnoreCase) || lower == "on" || lower == "bluetooth on" || lower == "on bluetooth"
                    ? "on"
                    : "off";
                response = await Commands.BluetoothControl(action);
                CurrentMenu[chatId] = "bluetooth";
                await botClient.SendMessage(chatId, response, replyMarkup: Commands.GetBluetoothKeyboard());
                return;
            }

            if (text == "📍 Move Custom")
            {
                PendingActions[chatId] = "MouseMove";
                response = "Отправьте координаты x y для перемещения мыши, например: 100 200";
                await botClient.SendMessage(chatId, response, replyMarkup: GetCurrentKeyboard(CurrentMenu.GetValueOrDefault(chatId, "mouse")));
                return;
            }

            if (PendingActions.TryGetValue(chatId, out var pendingAction))
            {
                PendingActions.Remove(chatId);
                switch (pendingAction)
                {
                    case "OpenPath":
                        response = await Commands.OpenPath(text);
                        break;
                    case "DownloadUrl":
                        response = await Commands.DownloadFile(text);
                        break;
                    case "Exec":
                        response = await Commands.ExecuteConsole(text);
                        break;
                    case "Notify":
                        response = await Commands.ShowNotification(text);
                        break;
                    case "TypeText":
                        response = await Commands.TypeText(text);
                        break;
                    case "Bluetooth":
                        response = await Commands.BluetoothControl(text);
                        break;
                    case "MouseMove":
                        response = await ParseMouseMove(text);
                        break;
                    default:
                        response = "❌ Неизвестная задача. Возвращаемся в главное меню.";
                        break;
                }
                var currentMenu = CurrentMenu.GetValueOrDefault(chatId, "main");
                await botClient.SendMessage(chatId, response, replyMarkup: GetCurrentKeyboard(currentMenu));
                return;
            }

            response = lower switch
            {
                "🔍 ping" => "✅ CoteboPC работает",
                "📸 screenshot" => await Commands.TakeScreenshot(botClient, chatId),
                "🖥 sysinfo" => await Commands.GetSystemInfo(),
                "🌐 ip-address" => await Commands.GetIpAddresses(),
                "🧠 processes" => await Commands.GetProcessesList(),
                "⚡ shutdown" => await Commands.Shutdown(),
                "🔄 reboot" => await Commands.Reboot(),
                "📋 minimize" => Commands.MinimizeAll(),
                "😴 sleep" => await Commands.Sleep(),
                "🎵 play" => await Commands.MediaControl("play"),
                "⏸ pause" => await Commands.MediaControl("pause"),
                "⏭ next" => await Commands.MediaControl("next"),
                "⏮ prev" => await Commands.MediaControl("prev"),
                "🎯 focus" => await Commands.MediaControl("focus"),
                "👆 click" => await Commands.MouseControl("click"),
                "👉 right click" => await Commands.MouseControl("rightclick"),
                "⬆ scroll up" => await Commands.MouseControl("scrollup"),
                "⬇ scroll down" => await Commands.MouseControl("scrolldown"),
                "📍 move center" => await Commands.MouseControl("center"),
                "🖥 next desktop" => await Commands.DesktopControl("next"),
                "🖥 prev desktop" => await Commands.DesktopControl("prev"),
                "🖥 new desktop" => await Commands.DesktopControl("new"),
                "📊 next slide" => await Commands.PresentationControl("next"),
                "📊 prev slide" => await Commands.PresentationControl("prev"),
                "📊 start presentation" => await Commands.PresentationControl("start"),
                "📊 end presentation" => await Commands.PresentationControl("end"),
                _ => "❌ Команда не распознана. Используйте кнопки меню." 
            };
        }
        catch (Exception ex)
        {
            response = $"❌ Ошибка: {ex.Message}";
            Logger.Log($"[Bot] Command error: {ex}");
        }

        await botClient.SendMessage(chatId, response, replyMarkup: GetCurrentKeyboard(CurrentMenu.GetValueOrDefault(chatId, "main")));
        Logger.Log($"[Bot] Response sent: {response}");
    }

    private static async Task<string> ParseMouseMove(string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !int.TryParse(parts[0], out var x) || !int.TryParse(parts[1], out var y))
            return "❌ Неверный формат координат. Введите: 100 200";

        return await Commands.MouseControl("move", x, y);
    }

    private static ReplyMarkup GetCurrentKeyboard(string menu)
    {
        return menu switch
        {
            "media" => Commands.GetMediaKeyboard(),
            "mouse" => Commands.GetMouseKeyboard(),
            "power" => Commands.GetPowerKeyboard(),
            "files" => Commands.GetFilesKeyboard(),
            "more" => Commands.GetMoreKeyboard(),
            "desktop" => Commands.GetDesktopKeyboard(),
            "presentation" => Commands.GetPresentationKeyboard(),
            "bluetooth" => Commands.GetBluetoothKeyboard(),
            _ => Commands.GetCommandsKeyboard()
        };
    }

    private static Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        Logger.Log($"[Bot] Polling error: {exception}");

        if (_config != null)
        {
            var configCopy = _config;
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                await RestartAsync(configCopy);
            });
        }

        return Task.CompletedTask;
    }
}