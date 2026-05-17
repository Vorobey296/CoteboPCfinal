using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using System.Threading.Tasks;

namespace CoteboPC.Utils;

public static class SimpleMessageBox
{
    public static async Task ShowAsync(Window owner, string title, string message)
    {
        var button = new Button
        {
            Content = "OK",
            Width = 100,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0)
        };

        var textBlock = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };

        var panel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 8,
            Margin = new Thickness(16),
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    FontSize = 18,
                    FontWeight = FontWeight.SemiBold,
                    Margin = new Thickness(0, 0, 0, 8)
                },
                textBlock,
                button
            }
        };

        var dialog = new Window
        {
            Width = 420,
            MinWidth = 320,
            MinHeight = 180,
            Content = panel,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Title = title,
            Background = Brushes.White
        };

        button.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(owner);
    }
}
