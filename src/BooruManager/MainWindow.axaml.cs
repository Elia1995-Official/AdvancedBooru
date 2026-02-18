using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using BooruManager.Models;
using BooruManager.Services;
using BooruManager.ViewModels;

namespace BooruManager;

public partial class MainWindow : Window
{
    private readonly ImageLoaderService _imageLoader = new();

    public MainWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private async void PostsScrollViewer_OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        if (sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        vm.PrioritizeVisiblePreviews(
            scrollViewer.Offset.Y,
            scrollViewer.Viewport.Height,
            scrollViewer.Viewport.Width,
            scrollViewer.Extent.Height);

        var distanceToBottom = scrollViewer.Extent.Height - (scrollViewer.Offset.Y + scrollViewer.Viewport.Height);
        if (distanceToBottom <= 700)
        {
            await vm.TryLoadMoreAsync();
        }
    }

    private void SearchTextBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        vm.SearchCommand.Execute(null);
        e.Handled = true;
    }

    private async void PostCard_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (!TryGetPostFromSender(sender, out var post))
        {
            return;
        }

        await OpenPostAsync(post);
    }

    private async void FavoriteButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!TryGetPostFromSender(sender, out var post))
        {
            return;
        }

        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        await vm.ToggleFavoriteAsync(post);
        e.Handled = true;
    }

    private void OpenPostButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!TryGetPostFromSender(sender, out var post))
        {
            return;
        }

        OpenUrl(post.PostUrl);
        e.Handled = true;
    }

    private void PostContextMenu_OnOpened(object? sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || menu.Items.Count < 2)
        {
            return;
        }

        if (!TryGetPostFromSender(sender, out var post))
        {
            return;
        }

        if (menu.Items[1] is MenuItem favoriteItem)
        {
            favoriteItem.Header = post.IsFavorite ? "Rimuovi dai preferiti" : "Aggiungi ai preferiti";
        }
    }

    private async void ContextView_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!TryGetPostFromSender(sender, out var post))
        {
            return;
        }

        await OpenPostAsync(post);
        e.Handled = true;
    }

    private async void ContextToggleFavorite_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!TryGetPostFromSender(sender, out var post))
        {
            return;
        }

        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        await vm.ToggleFavoriteAsync(post);
        e.Handled = true;
    }

    private async void ContextViewTags_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!TryGetPostFromSender(sender, out var post))
        {
            return;
        }

        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        await vm.EnsurePostTagsResolvedAsync(post);

        var selector = new TagSelectorWindow(post);
        var selectedTags = await selector.ShowDialog<IReadOnlyList<string>?>(this);
        if (selectedTags is { Count: > 0 })
        {
            vm.SearchText = string.Join(' ', selectedTags);
            vm.SearchCommand.Execute(null);
        }

        e.Handled = true;
    }

    private async void ContextCopyPostUrl_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!TryGetPostFromSender(sender, out var post))
        {
            return;
        }

        await CopyToClipboardAsync(post.PostUrl);
        e.Handled = true;
    }

    private async void ContextCopyMediaUrl_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!TryGetPostFromSender(sender, out var post))
        {
            return;
        }

        var mediaUrl = !string.IsNullOrWhiteSpace(post.FullImageUrl)
            ? post.FullImageUrl
            : post.PreviewUrl;
        await CopyToClipboardAsync(mediaUrl);
        e.Handled = true;
    }

    private async void ContextCopyTags_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!TryGetPostFromSender(sender, out var post))
        {
            return;
        }

        await CopyToClipboardAsync(post.Tags);
        e.Handled = true;
    }

    private async Task OpenPostAsync(ImagePost post)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            await vm.EnsurePostMediaResolvedAsync(post);
        }

        if (IsVideoUrl(post.FullImageUrl))
        {
            var videoWindow = new VideoPlayerWindow(post.SourceSite, post.Id, post.FullImageUrl);
            videoWindow.Show();
            return;
        }

        var imageWindow = new ImageViewerWindow(post, _imageLoader);
        imageWindow.Show();
    }

    private static bool TryGetPostFromSender(object? sender, out ImagePost post)
    {
        if (sender is MenuItem menuItem && menuItem.CommandParameter is ImagePost menuPost)
        {
            post = menuPost;
            return true;
        }

        if (sender is Control control && control.DataContext is ImagePost controlPost)
        {
            post = controlPost;
            return true;
        }

        if (sender is ContextMenu contextMenu
            && contextMenu.PlacementTarget is Control placementTarget
            && placementTarget.DataContext is ImagePost placementPost)
        {
            post = placementPost;
            return true;
        }

        post = null!;
        return false;
    }

    private static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }

    private static bool IsVideoUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var path = url;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            path = uri.AbsolutePath;
        }

        return path.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);
    }

    private void CheckUpdatesButton_OnClick(object? sender, RoutedEventArgs e)
    {
        Program.CheckForUpdates();
    }

    private async Task CopyToClipboardAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard is null)
        {
            return;
        }

        try
        {
            await topLevel.Clipboard.SetTextAsync(text);
        }
        catch
        {
        }
    }
}
