using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace AstroHybrid.Linux.Platform;

/// <summary>
/// The launcher's message boxes and folder picker. Upstream uses WPF's MessageBox, which blocks until
/// answered; here every dialog is awaited instead, owned by the main window.
/// </summary>
public static class Dialogs
{
    private static Window Owner =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public static Task ShowAsync(string message, string title = "AstroHybrid") => AskAsync(message, title, "OK");

    /// <returns>True for the first (affirmative) button.</returns>
    public static async Task<bool> ConfirmAsync(string message, string title = "AstroHybrid", string yes = "Yes", string no = "No") =>
        await AskAsync(message, title, yes, no) == 0;

    /// <returns>Index of the pressed button, -1 when the window was closed.</returns>
    public static async Task<int> AskAsync(string message, string title, params string[] buttons)
    {
        var owner = Owner;
        if (owner == null) { Console.Error.WriteLine($"{title}: {message}"); return -1; }
        var result = -1;
        var dialog = new Window
        {
            Title = title,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Classes = { "dialog" },
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        for (var i = 0; i < buttons.Length; i++)
        {
            var index = i;
            var b = new Button { Content = buttons[i], MinWidth = 96 };
            if (i == 0) b.Classes.Add("accent");
            b.Click += (_, _) => { result = index; dialog.Close(); };
            row.Children.Add(b);
        }
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 20,
            Children =
            {
                new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                row,
            },
        };
        await dialog.ShowDialog(owner);
        return result;
    }

    public static async Task<string> PickFolderAsync(string title)
    {
        var owner = Owner;
        if (owner == null) return null;
        var picked = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return picked.FirstOrDefault()?.TryGetLocalPath();
    }

    /// <summary>Opens a folder or web page with the desktop's default program (xdg-open).</summary>
    public static void Open(string target)
    {
        try
        {
            var psi = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
            psi.ArgumentList.Add(target);
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            _ = ShowAsync($"Could not open {target}\n{ex.Message}");
        }
    }
}
