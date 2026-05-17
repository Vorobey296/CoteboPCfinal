using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CoteboPC.Utils;

namespace CoteboPC.Views;

public partial class SecondSetupWindow : Window
{
    public SecondSetupWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        var tb = new TextBlock
        {
            FontSize = 15,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Colors.LightGray)
        };

        if (isWindows)
        {
            tb.Text = "Windows\n\nПриложение будет добавлено в автозагрузку как задача планировщика с правами администратора.\nПервый запуск потребует подтверждения UAC один раз, затем оно будет автоматически стартовать от имени администратора после перезагрузки.\n\nДля удаления из автозагрузки: используйте функцию в приложении или удалите задачу CoteboPC в планировщике задач.";
            ActionButton.Content = "Добавить в автозагрузку Windows";
        }
        else
        {
            tb.Text = "Linux\n\nБудет создан пользовательский systemd-сервис.\nПриложение будет запускаться автоматически после входа в систему.\n\nДля удаления из сервисов: выполните команды:\nsystemctl --user stop cotebopc.service\nsystemctl --user disable cotebopc.service\nrm ~/.config/systemd/user/cotebopc.service\nsystemctl --user daemon-reload";
            ActionButton.Content = "Настроить автозагрузку (systemd)";
        }

        ContentPanel.Children.Add(tb);
    }

    private async void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        try
        {
            if (isWindows)
                await PlatformHelpers.SetupWindowsAutostart();
            else
                await PlatformHelpers.SetupLinuxAutostart();

            // Закрываем окно настройки
            WindowState = WindowState.Minimized;
            ShowInTaskbar = false;
            Hide();
        }
        catch (Exception ex)
        {
            await SimpleMessageBox.ShowAsync(this, "Ошибка", $"Не удалось настроить автозагрузку:\n{ex.Message}");
        }
    }
}