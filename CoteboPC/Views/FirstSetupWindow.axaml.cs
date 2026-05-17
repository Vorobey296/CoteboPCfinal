using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using CoteboPC;
using CoteboPC.Utils;
using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace CoteboPC.Views;

public partial class FirstSetupWindow : Window
{
    public FirstSetupWindow()
    {
        InitializeComponent();
    }

    private async void SaveAndNext_Click(object sender, RoutedEventArgs e)
    {
        var token = TokenBox.Text?.Trim();
        var userIdText = UserIdBox.Text?.Trim();

        if (string.IsNullOrWhiteSpace(token) || !long.TryParse(userIdText, out long userId))
        {
            await SimpleMessageBox.ShowAsync(this, "Ошибка", "Пожалуйста, введите корректный Bot Token и User ID");
            return;
        }

        await ConfigService.SaveConfig(token, userId);

        try
        {
            await CoteboBotService.StartAsync(new AppConfig { Token = token, ChatId = userId });
        }
        catch (Exception ex)
        {
            await SimpleMessageBox.ShowAsync(this, "Ошибка", $"Не удалось запустить бота: {ex.Message}");
            return;
        }

        await ShowSecondSetupAndHide();
    }

    private async Task ShowSecondSetupAndHide()
    {
        var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        if (lifetime?.Windows.OfType<SecondSetupWindow>().Any() == true)
            return;

        var secondWindow = new SecondSetupWindow();
        secondWindow.Show();

        Close();
        await Task.CompletedTask;
    }

    private void BotTokenHelp_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("https://t.me/botfather") { UseShellExecute = true });
    }

    private void UserIdHelp_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("https://t.me/userinfobot") { UseShellExecute = true });
    }
}