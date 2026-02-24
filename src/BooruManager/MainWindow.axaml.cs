using System;
using System.IO;
using System.Text.Json;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using BooruManager.Models;
using BooruManager.Services;
using BooruManager.ViewModels;

namespace BooruManager;

public partial class MainWindow : Window
{
    private readonly ImageLoaderService _imageLoader = new();
    private readonly DispatcherTimer _slideshowTimer;
    private ImageViewerWindow? _slideshowWindow;
    private ImageViewerWindow? _imageViewerWindow;

    public MainWindow()
    {
        InitializeComponent();
        Title = LocalizationService.Instance["AppTitle"];
        LocalizationService.Instance.LanguageChanged += () => Title = LocalizationService.Instance["AppTitle"];

        _slideshowTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _slideshowTimer.Tick += SlideshowTimer_OnTick;

        KeyDown += MainWindow_OnKeyDown;
        DataContextChanged += MainWindow_DataContextChanged;
    }

    private void MainWindow_DataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.RequestSettingsWindow -= OpenSettingsWindow;
            vm.RequestSettingsWindow += OpenSettingsWindow;
        }
    }

    private async void OpenSettingsWindow()
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var settingsWindow = new SettingsWindow(
            vm.DownloadFolder,
            vm.SelectedLanguage,
            vm.SlideshowIntervalSeconds,
            vm.MaxConcurrentDownloads,
            vm.RequestTimeoutSeconds,
            vm.CardWidth,
            vm.CardHeight,
            vm.DownloadCreateSubfolders,
            vm.DownloadPreserveFilenames,
            vm.ConfirmBeforeDownload,
            vm.ShowDownloadNotifications,
            vm.AutoStartSlideshow,
            vm.CustomUserAgent,
            vm.ShowGridLines,
            vm.DetectDuplicates);

        await settingsWindow.ShowDialog(this);

        if (settingsWindow.SettingsChanged)
        {
            vm.ApplySettings(
                settingsWindow.SelectedDownloadFolder,
                settingsWindow.SelectedLanguage,
                settingsWindow.SlideshowInterval,
                settingsWindow.MaxConcurrentDownloads,
                settingsWindow.RequestTimeout,
                settingsWindow.CardWidth,
                settingsWindow.CardHeight,
                settingsWindow.CreateSubfolders,
                settingsWindow.PreserveFilenames,
                settingsWindow.ConfirmDownload,
                settingsWindow.ShowNotifications,
                settingsWindow.AutoStartSlideshow,
                settingsWindow.CustomUserAgent,
                settingsWindow.SelectedShowGridLines,
                settingsWindow.SelectedDetectDuplicates);
        }
    }

    private async void ContextSaveMetadata_OnClick(object? sender, RoutedEventArgs e)
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

        var downloadService = new DownloadService();
        var destFolder = string.IsNullOrWhiteSpace(vm.DownloadFolder)
            ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
            : vm.DownloadFolder;

        try
        {
            Directory.CreateDirectory(destFolder);
            foreach (var post in selectedPosts)
            {
                var url = !string.IsNullOrWhiteSpace(post.FullImageUrl) ? post.FullImageUrl : post.PreviewUrl;
                var baseName = downloadService.GenerateFileName(post.SourceSite, post.Id, url);
                var jsonPath = Path.Combine(destFolder, baseName + ".json");

                var payload = new
                {
                    post.Id,
                    post.SourceSite,
                    post.PostUrl,
                    post.FullImageUrl,
                    post.PreviewUrl,
                    post.Tags,
                    post.Width,
                    post.Height,
                    post.Score,
                    post.Md5Hash
                };

                var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(jsonPath, json);
            }

            if (selectedPosts.Count == 1)
            {
                vm.StatusText = "Saved metadata";
            }
            else
            {
                vm.StatusText = $"Saved metadata for {selectedPosts.Count} posts";
            }
        }
        catch (Exception)
        {
            vm.StatusText = "Failed to save metadata";
        }

        e.Handled = true;
    }

    private async void ContextCopyMarkdown_OnClick(object? sender, RoutedEventArgs e)
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

        var lines = selectedPosts.Select(p =>
            $"![{p.Id}]({(!string.IsNullOrWhiteSpace(p.FullImageUrl) ? p.FullImageUrl : p.PreviewUrl)})");
        var markdown = string.Join(Environment.NewLine, lines);
        await CopyToClipboardAsync(markdown);
        e.Handled = true;
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

    private void InsightsTag_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not TagStatistic stat)
        {
            return;
        }

        if (DataContext is not MainWindowViewModel vm || string.IsNullOrWhiteSpace(stat.Tag))
        {
            return;
        }

        vm.QuickTagSearchAppend(stat.Tag);
        vm.StatusText = $"Added '{stat.Tag}' to search";
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

        foreach (var image in vm.FilteredFavoriteImages)
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
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var post in vm.Images.Where(p => p.IsSelected)
                     .Concat(vm.FilteredFavoriteImages.Where(p => p.IsSelected)))
        {
            var key = $"{post.SourceSite.Trim().ToLowerInvariant()}::{post.Id.Trim()}";
            if (!seenKeys.Add(key))
            {
                continue;
            }

            selected.Add(post);
        }

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

        foreach (var post in selectedPosts)
        {
            await vm.EnsurePostTagsResolvedAsync(post);
        }

        var selector = new TagSelectorWindow(selectedPosts);
        var selectedTags = await selector.ShowDialog<IReadOnlyList<string>?>(this);
        if (selectedTags is { Count: > 0 })
        {
            vm.SearchText = string.Join(' ', selectedTags.Distinct());
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

        post.FullImageUrl = PromoteLegacyBooruThumbUrl(post.FullImageUrl, post.SourceSite);
        if (string.IsNullOrWhiteSpace(post.FullImageUrl))
        {
            post.FullImageUrl = PromoteLegacyBooruThumbUrl(post.PreviewUrl, post.SourceSite);
        }

        if (IsVideoUrl(post.FullImageUrl))
        {
            var videoWindow = new VideoPlayerWindow(post.SourceSite, post.Id, post.FullImageUrl);
            videoWindow.Show();
            return;
        }

        IReadOnlyList<ImagePost> posts;
        int currentIndex;
        if (DataContext is MainWindowViewModel vm2 && vm2.FilteredFavoriteImages.Contains(post))
        {
            posts = vm2.FilteredFavoriteImages;
            currentIndex = vm2.FilteredFavoriteImages.IndexOf(post);
        }
        else if (DataContext is MainWindowViewModel vm3)
        {
            posts = vm3.Images;
            currentIndex = vm3.Images.IndexOf(post);
        }
        else
        {
            posts = new List<ImagePost> { post };
            currentIndex = 0;
        }

        var imageWindow = new ImageViewerWindow(post, _imageLoader, posts, currentIndex);
        imageWindow.Closed += (_, _) => _imageViewerWindow = null;
        imageWindow.Show();
        imageWindow.Activate();
        _imageViewerWindow = imageWindow;
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

    private static string PromoteLegacyBooruThumbUrl(string url, string sourceSite)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return url;
        }

        var normalizedSource = (sourceSite ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedSource is not ("tab.booru.org" or "allgirl.booru.org" or "the-collection.booru.org"))
        {
            return url;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        if (!uri.Host.Equals("thumbs.booru.org", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length < 4)
        {
            return url;
        }

        var siteKey = segments[0];
        var bucket = segments[1];
        var directory = segments[2];
        var fileName = segments[3];

        if (!bucket.Equals("thumbnails", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        if (!fileName.StartsWith("thumbnail_", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        var fullName = fileName["thumbnail_".Length..];
        if (string.IsNullOrWhiteSpace(fullName) || string.IsNullOrWhiteSpace(siteKey) || string.IsNullOrWhiteSpace(directory))
        {
            return url;
        }

        return $"https://img.booru.org/{siteKey}//images/{directory}/{fullName}";
    }

    private void CheckUpdatesButton_OnClick(object? sender, RoutedEventArgs e)
    {
        Program.CheckForUpdates();
    }

    private void SettingsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        OpenSettingsWindow();
    }

    private async void MainWindow_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_imageViewerWindow != null)
        {
            await _imageViewerWindow.HandleKeyDownAsync(e);
            return;
        }

        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Right:
                if (vm.SlideshowMode || e.KeyModifiers.HasFlag(KeyModifiers.Control))
                {
                    await vm.NavigateNextAsync();
                    e.Handled = true;
                }
                break;
            case Key.Left:
                if (vm.SlideshowMode || e.KeyModifiers.HasFlag(KeyModifiers.Control))
                {
                    await vm.NavigatePreviousAsync();
                    e.Handled = true;
                }
                break;
            case Key.Escape:
                if (vm.SlideshowMode)
                {
                    vm.SlideshowMode = false;
                    StopSlideshow();
                    e.Handled = true;
                }
                break;
            case Key.Space:
                if (vm.SlideshowMode)
                {
                    if (_slideshowTimer.IsEnabled)
                    {
                        _slideshowTimer.Stop();
                    }
                    else
                    {
                        _slideshowTimer.Start();
                    }
                    e.Handled = true;
                }
                break;
            case Key.A when e.KeyModifiers == KeyModifiers.Control:
                SelectAllPosts();
                e.Handled = true;
                break;
            case Key.D when e.KeyModifiers == KeyModifiers.Control:
                await vm.ClearSelectionAsync();
                e.Handled = true;
                break;
            case Key.F5:
                vm.SearchCommand.Execute(null);
                e.Handled = true;
                break;
        }

        vm.UpdateSelectedCount();
    }

    private void SelectAllPosts()
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        foreach (var post in vm.Images)
        {
            post.IsSelected = true;
        }

        vm.UpdateSelectedCount();
    }

    private async void SlideshowTimer_OnTick(object? sender, EventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        if (!vm.SlideshowMode)
        {
            StopSlideshow();
            return;
        }

        _slideshowTimer.Interval = TimeSpan.FromSeconds(vm.SlideshowIntervalSeconds);

        await vm.NavigateNextAsync();

        var selectedPost = vm.Images.FirstOrDefault(p => p.IsSelected);
        if (selectedPost != null)
        {
            await ShowPostInSlideshowAsync(selectedPost);
        }
    }

    private async Task ShowPostInSlideshowAsync(ImagePost post)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        await vm.EnsurePostMediaResolvedAsync(post);

        post.FullImageUrl = PromoteLegacyBooruThumbUrl(post.FullImageUrl, post.SourceSite);
        if (string.IsNullOrWhiteSpace(post.FullImageUrl))
        {
            post.FullImageUrl = PromoteLegacyBooruThumbUrl(post.PreviewUrl, post.SourceSite);
        }

        if (IsVideoUrl(post.FullImageUrl))
        {
            return;
        }

        var index = vm.Images.IndexOf(post);
        _slideshowWindow?.Close();
        _slideshowWindow = new ImageViewerWindow(post, _imageLoader, vm.Images, index);
        _slideshowWindow.Show();
        _slideshowWindow.Activate();
    }

    private void StopSlideshow()
    {
        _slideshowTimer.Stop();
        _slideshowWindow?.Close();
        _slideshowWindow = null;
    }

    public void StartSlideshow()
    {
        if (DataContext is not MainWindowViewModel vm || vm.Images.Count == 0)
        {
            return;
        }

        vm.SlideshowMode = true;
        _slideshowTimer.Interval = TimeSpan.FromSeconds(vm.SlideshowIntervalSeconds);
        _slideshowTimer.Start();

        var firstPost = vm.Images.FirstOrDefault();
        if (firstPost != null)
        {
            firstPost.IsSelected = true;
            _ = ShowPostInSlideshowAsync(firstPost);
        }
    }

    private async void ContextHidePost_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!TryGetPostFromSender(sender, out var post))
        {
            return;
        }

        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        await vm.ToggleBlacklistForPostAsync(post);
        e.Handled = true;
    }

    private async void ContextSearchSimilar_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!TryGetPostFromSender(sender, out var post))
        {
            return;
        }

        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(post.Tags))
        {
            return;
        }

        var excludedTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "artist", "author" };
        var tags = post.Tags
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !excludedTags.Contains(t))
            .Take(5)
            .ToList();

        if (tags.Count == 0)
        {
            return;
        }

        vm.SearchText = string.Join(" ", tags);
        vm.SearchCommand.Execute(null);
        e.Handled = true;
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
