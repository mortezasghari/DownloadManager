using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using DownloadManager.Core.Domain;
using DownloadManager.UI.Services;

namespace DownloadManager.UI.Views;

/// <summary>Platform file picker for URL-list import, resolving the active window at call time.</summary>
internal sealed class AvaloniaFilePicker : IFilePicker
{
    public async Task<string?> PickListFileAsync()
    {
        if (MainWindow() is not { } window)
        {
            return null;
        }

        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import URL list",
            AllowMultiple = false,
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    internal static Window? MainWindow() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
}

/// <summary>
/// Modal credential prompt built in code (no extra XAML binding scope — keeps the compiled-binding/trim
/// surface minimal under the Avalonia 12 preview). Collects an Authorization header and a Cookie; returns
/// <c>null</c> on cancel. Values live only as long as the resulting <see cref="DownloadCredentials"/>.
/// </summary>
internal sealed class AvaloniaCredentialPrompt : ICredentialPrompt
{
    public async Task<DownloadCredentials?> PromptAsync(string downloadName)
    {
        if (AvaloniaFilePicker.MainWindow() is not { } owner)
        {
            return null;
        }

        var authorization = new TextBox { PlaceholderText = "Authorization header (e.g. Bearer …)" };
        var cookie = new TextBox { PlaceholderText = "Cookie (name=value; …)" };
        var ok = new Button { Content = "Resume", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true };

        var dialog = new Window
        {
            Title = $"Credentials — {downloadName}",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = "Re-supply credentials to resume from existing progress.", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    authorization,
                    cookie,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { cancel, ok },
                    },
                },
            },
        };

        DownloadCredentials? result = null;
        ok.Click += (_, _) =>
        {
            result = Build(authorization.Text, cookie.Text);
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(owner);
        return result;
    }

    private static DownloadCredentials Build(string? authorization, string? cookie)
    {
        string[] auth = string.IsNullOrWhiteSpace(authorization) ? [] : [authorization.Trim()];
        string[] cookies = string.IsNullOrWhiteSpace(cookie) ? [] : [cookie.Trim()];
        return new DownloadCredentials { AuthorizationHeaders = auth, Cookies = cookies };
    }
}