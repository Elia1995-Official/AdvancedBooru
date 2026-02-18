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

    private void PostCard_OnTapped(object? sender, TappedEventArgs e)
    {
        if (!TryGetPostFromSender(sender, out var post))
        {
            return;
        }

        var keyModifiers = e.KeyModifiers;
        if (keyModifiers.HasFlag(KeyModifiers.Control))
        {
            post.IsSelected = !post.IsSelected;
        }
        else
        {
            ClearAllSelections();
            post.IsSelected = true;
        }
    }

    private void ClearAllSelections()
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        foreach (var image in vm.Images)
        {
            image.IsSelected = false;
        }

        foreach (var image in vm.FavoriteImages)
        {
            image.IsSelected = false;
        }
    }

    private IReadOnlyList<ImagePost> GetSelectedPosts()
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return Array.Empty<ImagePost>();
        }

        var selected = new List<ImagePost>();
        selected.AddRange(vm.Images.Where(p => p.IsSelected));
        selected.AddRange(vm.FavoriteImages.Where(p => p.IsSelected));
        return selected;
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
        var selectedPosts = GetSelectedPosts();
        if (selectedPosts.Count == 0)
        {
            if (!TryGetPostFromSender(sender, out var post))
            {
                return;
            }

            selectedPosts = new List<ImagePost> { post };
        }

        foreach (var post in selectedPosts)
        {
            await OpenPostAsync(post);
        }

        e.Handled = true;
    }

    private async void ContextToggleFavorite_OnClick(object? sender, RoutedEventArgs e)
    {
        var selectedPosts = GetSelectedPosts();
        if (selectedPosts.Count == 0)
        {
            if (!TryGetPostFromSender(sender, out var post))
            {
                return;
            }

            selectedPosts = new List<ImagePost> { post };
        }

        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        foreach (var post in selectedPosts)
        {
            await vm.ToggleFavoriteAsync(post);
        }

        e.Handled = true;
    }

    private async void ContextViewTags_OnClick(object? sender, RoutedEventArgs e)
    {
        var selectedPosts = GetSelectedPosts();
        if (selectedPosts.Count == 0)
        {
            if (!TryGetPostFromSender(sender, out var post))
            {
                return;
            }

            selectedPosts = new List<ImagePost> { post };
        }

        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        List<string> allSelectedTags = new();

        foreach (var post in selectedPosts)
        {
            await vm.EnsurePostTagsResolvedAsync(post);

            var selector = new TagSelectorWindow(post);
            var selectedTags = await selector.ShowDialog<IReadOnlyList<string>?>(this);
            if (selectedTags is { Count: > 0 })
            {
                allSelectedTags.AddRange(selectedTags);
            }
        }

        if (allSelectedTags.Count > 0)
        {
            vm.SearchText = string.Join(' ', allSelectedTags.Distinct());
            vm.SearchCommand.Execute(null);
        }

        e.Handled = true;
    }

    private async void ContextCopyPostUrl_OnClick(object? sender, RoutedEventArgs e)
    {
        var selectedPosts = GetSelectedPosts();
        if (selectedPosts.Count == 0)
        {
            if (!TryGetPostFromSender(sender, out var post))
            {
                return;
            }

            selectedPosts = new List<ImagePost> { post };
        }

        var urls = string.Join(Environment.NewLine, selectedPosts.Select(p => p.PostUrl));
        await CopyToClipboardAsync(urls);
        e.Handled = true;
    }

    private async void ContextCopyMediaUrl_OnClick(object? sender, RoutedEventArgs e)
    {
        var selectedPosts = GetSelectedPosts();
        if (selectedPosts.Count == 0)
        {
            if (!TryGetPostFromSender(sender, out var post))
            {
                return;
            }

            selectedPosts = new List<ImagePost> { post };
        }

        var urls = string.Join(Environment.NewLine, selectedPosts.Select(p =>
            !string.IsNullOrWhiteSpace(p.FullImageUrl) ? p.FullImageUrl : p.PreviewUrl));
        await CopyToClipboardAsync(urls);
        e.Handled = true;
    }

    private async void ContextCopyTags_OnClick(object? sender, RoutedEventArgs e)
    {
        var selectedPosts = GetSelectedPosts();
        if (selectedPosts.Count == 0)
        {
            if (!TryGetPostFromSender(sender, out var post))
            {
                return;
            }

            selectedPosts = new List<ImagePost> { post };
        }

        var allTags = selectedPosts
            .SelectMany(p => p.Tags?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>())
            .Distinct()
            .OrderBy(t => t);
        var tagsString = string.Join(' ', allTags);
        await CopyToClipboardAsync(tagsString);
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
