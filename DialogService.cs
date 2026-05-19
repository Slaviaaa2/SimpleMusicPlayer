using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace SimpleMusicPlayer;

internal static class DialogService
{
    public static async Task ShowInfoAsync(Window owner, string title, string message)
        => _ = await ShowDialogAsync(owner, title, message, "OK", null);

    public static Task<bool> ShowConfirmationAsync(
        Window owner,
        string title,
        string message,
        string confirmText = "OK",
        string cancelText = "Cancel")
        => ShowDialogAsync(owner, title, message, confirmText, cancelText);

    private static Task<bool> ShowDialogAsync(
        Window owner,
        string title,
        string message,
        string confirmText,
        string? cancelText)
    {
        var confirmButton = new Button
        {
            Content = confirmText,
            MinWidth = 96,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10
        };

        Button? cancelButton = null;
        if (!string.IsNullOrWhiteSpace(cancelText))
        {
            cancelButton = new Button
            {
                Content = cancelText,
                MinWidth = 96
            };
            buttons.Children.Add(cancelButton);
        }

        buttons.Children.Add(confirmButton);

        var dialog = new Window
        {
            Title = title,
            Width = 440,
            MinWidth = 360,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#161616")),
            Foreground = new SolidColorBrush(Color.Parse("#F3F3F3")),
            Content = new Border
            {
                Padding = new Thickness(20),
                Child = new StackPanel
                {
                    Spacing = 18,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = message,
                            TextWrapping = TextWrapping.Wrap,
                            MaxWidth = 380
                        },
                        buttons
                    }
                }
            }
        };

        confirmButton.Click += (_, _) => dialog.Close(true);
        if (cancelButton is not null)
        {
            cancelButton.Click += (_, _) => dialog.Close(false);
        }

        return dialog.ShowDialog<bool>(owner);
    }
}
