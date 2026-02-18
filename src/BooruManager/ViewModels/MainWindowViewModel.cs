using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Threading;
using BooruManager.Models;
using BooruManager.Services;

namespace BooruManager.ViewModels;

public class MainWindowViewModel : INotifyPropertyChanged
{
    private const int DefaultPageSize = 40;
    private const int MaxRecentSearches = 12;
    private const int PreviewWorkerCount = 6;
    private const int PreviewPriorityHigh = 0;
    private const int PreviewPriorityNormal = 1;
    private const double CardCellWidth = 368;
    private const double CardCellHeight = 424;
    private const int VisiblePreviewRowMargin = 1;
    private const string SortByDateDescKey = "date_desc";
    private const string SortBySizeDescKey = "size_desc";
    private const string SortByVotesDescKey = "votes_desc";
    private const string SortTypeImageKey = "type_image";
    private const string SortTypeAnimatedImageKey = "type_animated_image";
    private const string SortTypeWebmKey = "type_webm";
    private const string SortTypeMp4Key = "type_mp4";

    private const string MediaTypeAllKey = "media_all";
    private const string MediaTypeImagesKey = "media_images";
    private const string MediaTypeAnimatedKey = "media_animated";
    private const string MediaTypeVideoKey = "media_video";
    private const string MediaTypeWebmKey = "media_webm";
    private const string MediaTypeMp4Key = "media_mp4";

    private const string SizeFilterAllKey = "size_all";
    private const string SizeFilterLargeKey = "size_large";
    private const string SizeFilterMediumKey = "size_medium";
    private const string SizeFilterSmallKey = "size_small";

    private static readonly IReadOnlyList<ResultSortOption> SortOptionsInternal = new[]
    {
        new ResultSortOption(SortByDateDescKey, "Date (newest)"),
        new ResultSortOption(SortBySizeDescKey, "Size (largest)"),
        new ResultSortOption(SortByVotesDescKey, "Votes (highest)")
    };

    private static readonly IReadOnlyList<ResultSortOption> MediaTypeFilterOptionsInternal = new[]
    {
        new ResultSortOption(MediaTypeAllKey, "All media"),
        new ResultSortOption(MediaTypeImagesKey, "Images (static)"),
        new ResultSortOption(MediaTypeAnimatedKey, "Animated images (gif/apng)"),
        new ResultSortOption(MediaTypeVideoKey, "Videos"),
        new ResultSortOption(MediaTypeWebmKey, "WebM"),
        new ResultSortOption(MediaTypeMp4Key, "MP4")
    };

    private static readonly IReadOnlyList<ResultSortOption> SizeFilterOptionsInternal = new[]
    {
        new ResultSortOption(SizeFilterAllKey, "All sizes"),
        new ResultSortOption(SizeFilterLargeKey, "Large (>2000px)"),
        new ResultSortOption(SizeFilterMediumKey, "Medium (1000-2000px)"),
        new ResultSortOption(SizeFilterSmallKey, "Small (<1000px)")
    };

    private readonly BooruApiService _api = new();
    private readonly CredentialsStore _credentialsStore = new();
    private readonly ImageLoaderService _imageLoader = new();
    private readonly SemaphoreSlim _settingsSaveGate = new(1, 1);
    private readonly SemaphoreSlim _previewWorkSignal = new(0, int.MaxValue);
    private readonly SemaphoreSlim _visiblePreviewGate = new(3, 3);
    private readonly object _previewQueueGate = new();
    private readonly PriorityQueue<PreviewWorkItem, long> _previewQueue = new();
    private readonly Dictionary<ImagePost, int> _queuedPreviewPriority = new();
    private readonly HashSet<ImagePost> _loadingPreviewPosts = new();
    private readonly CancellationTokenSource _previewWorkersCts = new();

    private readonly ObservableCollection<ImagePost> _allImages = new();
    private readonly HashSet<string> _loadedPostKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _favoriteKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ImagePost> _favoritePostsByKey = new(StringComparer.OrdinalIgnoreCase);

    private AppSettings _settings = new();
    private CancellationTokenSource? _searchCts;

    private string _searchText = string.Empty;
    private string? _selectedRecentSearch;
    private BooruSite _selectedSite = BooruSite.Safebooru;
    private bool _includeSafe = true;
    private bool _includeQuestionable;
    private bool _includeAdult;
    private bool _showFavoritesOnly;
    private int _minimumScore;
    private int _minimumWidth;
    private int _minimumHeight;
    private string _requiredTags = string.Empty;
    private string _excludedTags = string.Empty;
    private string[] _requiredTagTokens = Array.Empty<string>();
    private string[] _excludedTagTokens = Array.Empty<string>();
    private int _selectedPageSize = DefaultPageSize;
    private ResultSortOption _selectedSortOption = SortOptionsInternal[0];
    private ResultSortOption _selectedMediaTypeFilter = MediaTypeFilterOptionsInternal[0];
    private ResultSortOption _selectedSizeFilter = SizeFilterOptionsInternal[0];
    private string _username = string.Empty;
    private string _secret = string.Empty;
    private bool _isLoggedIn;
    private bool _isLoading;
    private bool _hasStartedSearch;
    private string _statusText = "Ready";
    private int _nextPage = 1;
    private bool _hasMorePages = true;
    private int _maxPages = int.MaxValue;
    private bool _isInitialLoad = true;
    private long _previewQueueSequence;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ImagePost> Images { get; } = new();
    public ObservableCollection<ImagePost> FavoriteImages { get; } = new();
    public ObservableCollection<string> RecentSearches { get; } = new();

    public IReadOnlyList<BooruSite> Sites { get; } = Enum.GetValues<BooruSite>();
    public IReadOnlyList<int> PageSizeOptions { get; } = new[] { 20, 40, 80, 120 };
    public IReadOnlyList<int> MinimumScoreOptions { get; } = new[] { 0, 10, 25, 50, 100, 200, 500, 1000 };
    public IReadOnlyList<int> MinimumDimensionOptions { get; } = new[] { 0, 640, 1024, 1280, 1920, 2560, 3840 };
    public IReadOnlyList<ResultSortOption> SortOptions { get; } = SortOptionsInternal;
    public IReadOnlyList<ResultSortOption> MediaTypeFilterOptions { get; } = MediaTypeFilterOptionsInternal;
    public IReadOnlyList<ResultSortOption> SizeFilterOptions { get; } = SizeFilterOptionsInternal;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value)
            {
                return;
            }

            _searchText = value;
            OnPropertyChanged();
        }
    }

    public string? SelectedRecentSearch
    {
        get => _selectedRecentSearch;
        set
        {
            if (_selectedRecentSearch == value)
            {
                return;
            }

            _selectedRecentSearch = value;
            OnPropertyChanged();

            if (!string.IsNullOrWhiteSpace(value))
            {
                SearchText = value;
            }
        }
    }

    public BooruSite SelectedSite
    {
        get => _selectedSite;
        set
        {
            if (_selectedSite == value)
            {
                return;
            }

            _selectedSite = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(UsesApiKey));
            OnPropertyChanged(nameof(ShowRatingFilters));
            OnPropertyChanged(nameof(SecretLabel));

            if (value == BooruSite.Safebooru)
            {
                IncludeSafe = true;
                IncludeQuestionable = true;
                IncludeAdult = true;
            }

            ApplyCredentialsForSelectedSite();
            if (_hasStartedSearch)
            {
                _ = RefreshAsync();
            }
        }
    }

    public bool IncludeSafe
    {
        get => _includeSafe;
        set
        {
            if (_includeSafe == value)
            {
                return;
            }

            _includeSafe = value;
            OnPropertyChanged();
        }
    }

    public bool IncludeQuestionable
    {
        get => _includeQuestionable;
        set
        {
            if (_includeQuestionable == value)
            {
                return;
            }

            _includeQuestionable = value;
            OnPropertyChanged();
        }
    }

    public bool IncludeAdult
    {
        get => _includeAdult;
        set
        {
            if (_includeAdult == value)
            {
                return;
            }

            _includeAdult = value;
            OnPropertyChanged();
        }
    }

    public bool ShowFavoritesOnly
    {
        get => _showFavoritesOnly;
        set
        {
            if (_showFavoritesOnly == value)
            {
                return;
            }

            _showFavoritesOnly = value;
            OnPropertyChanged();
            ApplyVisibleFilter();
            SaveSettingsInBackground();
        }
    }

    public int MinimumScore
    {
        get => _minimumScore;
        set
        {
            var normalized = Math.Max(0, value);
            if (_minimumScore == normalized)
            {
                return;
            }

            _minimumScore = normalized;
            OnPropertyChanged();
            ApplyVisibleFilter();
            SaveSettingsInBackground();
        }
    }

    public int MinimumWidth
    {
        get => _minimumWidth;
        set
        {
            var normalized = Math.Max(0, value);
            if (_minimumWidth == normalized)
            {
                return;
            }

            _minimumWidth = normalized;
            OnPropertyChanged();
            ApplyVisibleFilter();
            SaveSettingsInBackground();
        }
    }

    public int MinimumHeight
    {
        get => _minimumHeight;
        set
        {
            var normalized = Math.Max(0, value);
            if (_minimumHeight == normalized)
            {
                return;
            }

            _minimumHeight = normalized;
            OnPropertyChanged();
            ApplyVisibleFilter();
            SaveSettingsInBackground();
        }
    }

    public string RequiredTags
    {
        get => _requiredTags;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (string.Equals(_requiredTags, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _requiredTags = normalized;
            RebuildTagFilterTokens();
            OnPropertyChanged();
            ApplyVisibleFilter();
            SaveSettingsInBackground();
        }
    }

    public string ExcludedTags
    {
        get => _excludedTags;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (string.Equals(_excludedTags, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _excludedTags = normalized;
            RebuildTagFilterTokens();
            OnPropertyChanged();
            ApplyVisibleFilter();
            SaveSettingsInBackground();
        }
    }

    public int SelectedPageSize
    {
        get => _selectedPageSize;
        set
        {
            if (_selectedPageSize == value)
            {
                return;
            }

            _selectedPageSize = value;
            OnPropertyChanged();
            SaveSettingsInBackground();

            if (_hasStartedSearch)
            {
                _ = RefreshAsync();
            }
        }
    }

    public ResultSortOption SelectedSortOption
    {
        get => _selectedSortOption;
        set
        {
            if (value is null || string.Equals(_selectedSortOption.Key, value.Key, StringComparison.Ordinal))
            {
                return;
            }

            _selectedSortOption = value;
            OnPropertyChanged();
            SaveSettingsInBackground();

            if (_hasStartedSearch)
            {
                _ = RefreshAsync();
            }
        }
    }

    public ResultSortOption SelectedMediaTypeFilter
    {
        get => _selectedMediaTypeFilter;
        set
        {
            if (value is null || string.Equals(_selectedMediaTypeFilter.Key, value.Key, StringComparison.Ordinal))
            {
                return;
            }

            _selectedMediaTypeFilter = value;
            OnPropertyChanged();
            ApplyVisibleFilter();
            SaveSettingsInBackground();
        }
    }

    public ResultSortOption SelectedSizeFilter
    {
        get => _selectedSizeFilter;
        set
        {
            if (value is null || string.Equals(_selectedSizeFilter.Key, value.Key, StringComparison.Ordinal))
            {
                return;
            }

            _selectedSizeFilter = value;
            OnPropertyChanged();
            ApplyVisibleFilter();
            SaveSettingsInBackground();
        }
    }

    public string Username
    {
        get => _username;
        set
        {
            if (_username == value)
            {
                return;
            }

            _username = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LoggedInAsText));
        }
    }

    public string Secret
    {
        get => _secret;
        set
        {
            if (_secret == value)
            {
                return;
            }

            _secret = value;
            OnPropertyChanged();
        }
    }

    public bool IsLoggedIn
    {
        get => _isLoggedIn;
        private set
        {
            if (_isLoggedIn == value)
            {
                return;
            }

            _isLoggedIn = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LoginButtonText));
            OnPropertyChanged(nameof(ShowCredentialInputs));
            OnPropertyChanged(nameof(ShowLoggedInInfo));
            OnPropertyChanged(nameof(LoggedInAsText));
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (_isLoading == value)
            {
                return;
            }

            _isLoading = value;
            OnPropertyChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value)
            {
                return;
            }

            _statusText = value;
            OnPropertyChanged();
        }
    }

    public int FavoritesCount => _favoritePostsByKey.Count;
    public string FavoritesSummary => $"Only favorites ({FavoritesCount})";
    public string FavoritesTabTitle => $"Favorites ({FavoriteImages.Count})";

    public bool UsesApiKey => SelectedSite is BooruSite.E621 or BooruSite.Danbooru or BooruSite.Gelbooru;
    public bool ShowRatingFilters => SelectedSite is not BooruSite.Safebooru;
    public string SecretLabel => UsesApiKey ? "API Key" : "Password";
    public string LoginButtonText => IsLoggedIn ? "Logout" : "Login";
    public bool ShowCredentialInputs => !IsLoggedIn;
    public bool ShowLoggedInInfo => IsLoggedIn;
    public string LoggedInAsText => IsLoggedIn ? $"Logged in as: {Username}" : string.Empty;

    public ICommand SearchCommand { get; }
    public ICommand LoadMoreCommand { get; }
    public ICommand LoginToggleCommand { get; }
    public ICommand ClearHistoryCommand { get; }
    public ICommand ResetLocalFiltersCommand { get; }
    public ICommand ShuffleVisibleCommand { get; }
    public ICommand ExportVisibleCommand { get; }

    public MainWindowViewModel()
    {
        SearchCommand = new AsyncRelayCommand(StartSearchAsync);
        LoadMoreCommand = new AsyncRelayCommand(LoadNextPageAsync);
        LoginToggleCommand = new AsyncRelayCommand(ToggleLoginAsync);
        ClearHistoryCommand = new AsyncRelayCommand(ClearHistoryAsync);
        ResetLocalFiltersCommand = new AsyncRelayCommand(ResetLocalFiltersAsync);
        ShuffleVisibleCommand = new AsyncRelayCommand(ShuffleVisibleAsync);
        ExportVisibleCommand = new AsyncRelayCommand(ExportVisibleAsync);

        StartPreviewWorkers();
        _ = InitializeAsync();
    }

    public async Task TryLoadMoreAsync()
    {
        if (IsLoading || !_hasMorePages)
        {
            return;
        }

        _maxPages += 10;
        _searchCts = new CancellationTokenSource();
        await LoadAllPagesAsync(_searchCts.Token);
    }

    public async Task ToggleFavoriteAsync(ImagePost post)
    {
        var key = BuildFavoriteKey(post);
        if (_favoriteKeys.Contains(key))
        {
            _favoriteKeys.Remove(key);
            post.IsFavorite = false;
            RemoveFavoriteSnapshot(key);
        }
        else
        {
            _favoriteKeys.Add(key);
            post.IsFavorite = true;
            AddOrUpdateFavoriteSnapshot(post);
        }

        UpdateLoadedFavoriteState(key, post.IsFavorite);
        NotifyFavoritesChanged();

        if (ShowFavoritesOnly)
        {
            ApplyVisibleFilter();
        }

        await SaveSettingsAsync();
    }

    public async Task EnsurePostMediaResolvedAsync(ImagePost post)
    {
        if (post is null || !NeedsMediaResolution(post) || !TryMapSourceSite(post.SourceSite, out var site))
        {
            return;
        }

        try
        {
            _settings.CredentialsBySite.TryGetValue(site, out var creds);
            var resolved = await _api.GetPostByIdAsync(site, post.Id, creds);
            if (resolved is null || string.IsNullOrWhiteSpace(resolved.FullImageUrl))
            {
                return;
            }

            ApplyResolvedPostDetails(post, resolved);

            if (post.IsFavorite)
            {
                AddOrUpdateFavoriteSnapshot(post);
            }
        }
        catch
        {
        }
    }

    public async Task EnsurePostTagsResolvedAsync(ImagePost post)
    {
        if (post is null || HasTagGroups(post) || !TryMapSourceSite(post.SourceSite, out var site))
        {
            return;
        }

        try
        {
            _settings.CredentialsBySite.TryGetValue(site, out var creds);
            var resolved = await _api.GetPostByIdAsync(site, post.Id, creds);
            if (resolved is null)
            {
                return;
            }

            ApplyResolvedPostDetails(post, resolved);

            if (post.IsFavorite)
            {
                AddOrUpdateFavoriteSnapshot(post);
            }
        }
        catch
        {
        }
    }

    public void PrioritizeVisiblePreviews(double verticalOffset, double viewportHeight, double viewportWidth, double extentHeight)
    {
        if (Images.Count == 0)
        {
            return;
        }

        var safeViewportWidth = Math.Max(viewportWidth, CardCellWidth);
        var columns = Math.Max(1, (int)Math.Floor(safeViewportWidth / CardCellWidth));
        var safeOffset = Math.Max(0, verticalOffset);
        var safeViewportHeight = Math.Max(0, viewportHeight);

        var firstVisibleRow = Math.Max(0, (int)Math.Floor(safeOffset / CardCellHeight));
        var lastVisibleRow = Math.Max(firstVisibleRow, (int)Math.Ceiling((safeOffset + safeViewportHeight) / CardCellHeight));

        var startRow = Math.Max(0, firstVisibleRow - VisiblePreviewRowMargin);
        var endRow = lastVisibleRow + VisiblePreviewRowMargin;

        var startIndex = startRow * columns;
        var endIndexExclusive = Math.Min(Images.Count, (endRow + 1) * columns);

        var prioritizedCount = 0;
        for (var i = startIndex; i < endIndexExclusive; i++)
        {
            BoostVisiblePreviewLoad(Images[i]);
            QueuePreviewLoad(Images[i], PreviewPriorityHigh);
            prioritizedCount++;
        }

        if (prioritizedCount > 0)
        {
            return;
        }

        var anchorIndex = 0;
        if (extentHeight > 0 && Images.Count > 1)
        {
            var progress = Math.Clamp(safeOffset / extentHeight, 0, 1);
            anchorIndex = (int)Math.Round(progress * (Images.Count - 1));
        }

        const int fallbackWindow = 60;
        for (var offset = 0; offset <= fallbackWindow; offset++)
        {
            var forwardIndex = anchorIndex + offset;
            if (forwardIndex >= 0 && forwardIndex < Images.Count)
            {
                BoostVisiblePreviewLoad(Images[forwardIndex]);
                QueuePreviewLoad(Images[forwardIndex], PreviewPriorityHigh);
            }

            if (offset == 0)
            {
                continue;
            }

            var backwardIndex = anchorIndex - offset;
            if (backwardIndex >= 0 && backwardIndex < Images.Count)
            {
                BoostVisiblePreviewLoad(Images[backwardIndex]);
                QueuePreviewLoad(Images[backwardIndex], PreviewPriorityHigh);
            }
        }
    }

    private async Task InitializeAsync()
    {
        _settings = await _credentialsStore.LoadAsync();
        ApplyCredentialsForSelectedSite();
        InitializePreferencesFromSettings();
        var missingFavoriteKeys = _favoriteKeys
            .Where(key => !_favoritePostsByKey.ContainsKey(key))
            .ToList();

        if (missingFavoriteKeys.Count > 0)
        {
            _ = HydrateMissingFavoritesAsync(missingFavoriteKeys);
        }

        if (FavoriteImages.Count > 0)
        {
            _ = LoadPreviewsAsync(FavoriteImages.Where(x => x.PreviewImage is null).ToList(), CancellationToken.None);
        }

        _isInitialLoad = true;
        _hasStartedSearch = true;
        await RefreshAsync();
    }

    private void InitializePreferencesFromSettings()
    {
        RecentSearches.Clear();
        foreach (var item in _settings.RecentSearches.Take(MaxRecentSearches))
        {
            if (!string.IsNullOrWhiteSpace(item))
            {
                RecentSearches.Add(item.Trim());
            }
        }

        _favoriteKeys.Clear();
        _favoritePostsByKey.Clear();
        FavoriteImages.Clear();

        foreach (var key in _settings.FavoritePostKeys)
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                _favoriteKeys.Add(key.Trim());
            }
        }

        foreach (var favorite in _settings.FavoritePosts)
        {
            if (string.IsNullOrWhiteSpace(favorite.Id) || string.IsNullOrWhiteSpace(favorite.SourceSite))
            {
                continue;
            }

            favorite.IsFavorite = true;
            var favoriteKey = BuildFavoriteKey(favorite);
            if (_favoritePostsByKey.ContainsKey(favoriteKey))
            {
                continue;
            }

            _favoritePostsByKey[favoriteKey] = favorite;
            FavoriteImages.Add(favorite);
            _favoriteKeys.Add(favoriteKey);
        }

        _selectedPageSize = PageSizeOptions.Contains(_settings.ResultsPerPage)
            ? _settings.ResultsPerPage
            : DefaultPageSize;
        _selectedSortOption = SortOptionsInternal.FirstOrDefault(x =>
                string.Equals(x.Key, _settings.SearchSortKey, StringComparison.Ordinal))
            ?? SortOptionsInternal[0];
        _showFavoritesOnly = _settings.ShowFavoritesOnly;
        _minimumScore = Math.Max(0, _settings.MinimumScore);
        _minimumWidth = Math.Max(0, _settings.MinimumWidth);
        _minimumHeight = Math.Max(0, _settings.MinimumHeight);
        _requiredTags = (_settings.RequiredTags ?? string.Empty).Trim();
        _excludedTags = (_settings.ExcludedTags ?? string.Empty).Trim();
        RebuildTagFilterTokens();

        OnPropertyChanged(nameof(SelectedPageSize));
        OnPropertyChanged(nameof(SelectedSortOption));
        OnPropertyChanged(nameof(ShowFavoritesOnly));
        OnPropertyChanged(nameof(MinimumScore));
        OnPropertyChanged(nameof(MinimumWidth));
        OnPropertyChanged(nameof(MinimumHeight));
        OnPropertyChanged(nameof(RequiredTags));
        OnPropertyChanged(nameof(ExcludedTags));
        NotifyFavoritesChanged();
    }

    private async Task StartSearchAsync()
    {
        _hasStartedSearch = true;
        AddCurrentSearchToHistory();
        await SaveSettingsAsync();
        await RefreshAsync();
    }

    private void AddCurrentSearchToHistory()
    {
        var query = SearchText.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        var existing = RecentSearches.FirstOrDefault(x => string.Equals(x, query, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            RecentSearches.Remove(existing);
        }

        RecentSearches.Insert(0, query);
        while (RecentSearches.Count > MaxRecentSearches)
        {
            RecentSearches.RemoveAt(RecentSearches.Count - 1);
        }
    }

    private void ApplyCredentialsForSelectedSite()
    {
        if (_settings.CredentialsBySite.TryGetValue(SelectedSite, out var credentials))
        {
            Username = credentials.Username;
            Secret = credentials.Secret;
            IsLoggedIn = !string.IsNullOrWhiteSpace(credentials.Username) && !string.IsNullOrWhiteSpace(credentials.Secret);
            StatusText = "Stored credentials loaded";
            return;
        }

        Username = string.Empty;
        Secret = string.Empty;
        IsLoggedIn = false;
    }

    private async Task ToggleLoginAsync()
    {
        if (IsLoggedIn)
        {
            _settings.CredentialsBySite.Remove(SelectedSite);
            IsLoggedIn = false;
            Secret = string.Empty;
            StatusText = "Logged out";
            await SaveSettingsAsync();
            return;
        }

        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Secret))
        {
            StatusText = "Insert username and credentials";
            return;
        }

        var credentials = new BooruCredentials
        {
            Username = Username.Trim(),
            Secret = Secret.Trim()
        };

        StatusText = "Validating credentials...";
        var valid = await _api.ValidateCredentialsAsync(SelectedSite, credentials);
        if (!valid)
        {
            StatusText = "Invalid credentials";
            return;
        }

        _settings.CredentialsBySite[SelectedSite] = credentials;
        IsLoggedIn = true;
        StatusText = "Login saved";
        await SaveSettingsAsync();
    }

    private async Task ClearHistoryAsync()
    {
        RecentSearches.Clear();
        SelectedRecentSearch = null;
        StatusText = "Search history cleared";
        await SaveSettingsAsync();
    }

    private async Task ResetLocalFiltersAsync()
    {
        var changed = false;

        if (_minimumScore != 0)
        {
            _minimumScore = 0;
            OnPropertyChanged(nameof(MinimumScore));
            changed = true;
        }

        if (_minimumWidth != 0)
        {
            _minimumWidth = 0;
            OnPropertyChanged(nameof(MinimumWidth));
            changed = true;
        }

        if (_minimumHeight != 0)
        {
            _minimumHeight = 0;
            OnPropertyChanged(nameof(MinimumHeight));
            changed = true;
        }

        if (!string.IsNullOrEmpty(_requiredTags))
        {
            _requiredTags = string.Empty;
            OnPropertyChanged(nameof(RequiredTags));
            changed = true;
        }

        if (!string.IsNullOrEmpty(_excludedTags))
        {
            _excludedTags = string.Empty;
            OnPropertyChanged(nameof(ExcludedTags));
            changed = true;
        }

        if (!changed)
        {
            StatusText = "Local filters are already clear";
            return;
        }

        RebuildTagFilterTokens();
        ApplyVisibleFilter();
        StatusText = $"Local filters reset ({Images.Count} shown)";
        await SaveSettingsAsync();
    }

    private async Task ShuffleVisibleAsync()
    {
        if (Images.Count <= 1)
        {
            StatusText = "Not enough posts to shuffle";
            await Task.CompletedTask;
            return;
        }

        var shuffled = Images.ToList();
        for (var i = shuffled.Count - 1; i > 0; i--)
        {
            var swapIndex = Random.Shared.Next(i + 1);
            (shuffled[i], shuffled[swapIndex]) = (shuffled[swapIndex], shuffled[i]);
        }

        Images.Clear();
        foreach (var post in shuffled)
        {
            Images.Add(post);
        }

        StatusText = $"Shuffled {Images.Count} visible posts";
        await Task.CompletedTask;
    }

    private async Task ExportVisibleAsync()
    {
        if (Images.Count == 0)
        {
            StatusText = "No visible posts to export";
            return;
        }

        try
        {
            var exportDir = ResolveExportDirectory();
            Directory.CreateDirectory(exportDir);

            var filePath = Path.Combine(
                exportDir,
                $"booru-visible-posts-{DateTime.Now:yyyyMMdd-HHmmss}.csv");

            var builder = new StringBuilder();
            builder.AppendLine("source,id,rating,score,width,height,post_url,media_url,tags");

            foreach (var post in Images)
            {
                var mediaUrl = !string.IsNullOrWhiteSpace(post.FullImageUrl)
                    ? post.FullImageUrl
                    : post.PreviewUrl;
                builder.AppendLine(string.Join(",",
                    EscapeCsv(post.SourceSite),
                    EscapeCsv(post.Id),
                    EscapeCsv(post.Rating),
                    post.Score.ToString(),
                    post.Width.ToString(),
                    post.Height.ToString(),
                    EscapeCsv(post.PostUrl),
                    EscapeCsv(mediaUrl),
                    EscapeCsv(post.Tags)));
            }

            await File.WriteAllTextAsync(filePath, builder.ToString());
            StatusText = $"Exported {Images.Count} visible posts to {filePath}";
        }
        catch (Exception ex)
        {
            StatusText = $"Export failed: {ex.Message}";
        }
    }

    private async Task RefreshAsync()
    {
        CancelCurrentSearch();
        _searchCts = new CancellationTokenSource();

        _allImages.Clear();
        _loadedPostKeys.Clear();
        Images.Clear();
        _nextPage = 1;
        _hasMorePages = true;
        _maxPages = _isInitialLoad ? 10 : int.MaxValue;
        _isInitialLoad = false;

        await LoadAllPagesAsync(_searchCts.Token);
    }

    private async Task LoadAllPagesAsync(CancellationToken cancellationToken)
    {
        while (_hasMorePages && !cancellationToken.IsCancellationRequested)
        {
            if (_nextPage > _maxPages)
            {
                _hasMorePages = false;
                break;
            }

            await LoadNextPageAsync(cancellationToken);
            await Task.Yield();
        }

        if (!cancellationToken.IsCancellationRequested && _allImages.Count > 0)
        {
            StatusText = $"Loaded {_allImages.Count} posts ({Images.Count} shown)";
        }
    }

    private void ApplyVisibleFilter()
    {
        Images.Clear();

        foreach (var post in GetOrderedBrowsePosts())
        {
            if (!ShouldShowPost(post))
            {
                continue;
            }

            Images.Add(post);
        }
    }

    private IEnumerable<ImagePost> GetOrderedBrowsePosts()
    {
        var filteredByType = _allImages.Where(MatchesSelectedTypeFilter);
        return SelectedSortOption.Key switch
        {
            SortBySizeDescKey => filteredByType
                .OrderByDescending(GetPostPixelArea)
                .ThenByDescending(GetPostDateRank),
            SortByVotesDescKey => filteredByType
                .OrderByDescending(x => x.Score)
                .ThenByDescending(GetPostDateRank),
            _ => filteredByType
                .OrderByDescending(GetPostDateRank)
        };
    }

    private bool ShouldShowPost(ImagePost post)
    {
        if (ShowFavoritesOnly && !post.IsFavorite)
        {
            return false;
        }

        return MatchesSelectedTypeFilter(post) && MatchesSizeFilter(post) && MatchesLocalFilters(post);
    }

    private bool MatchesLocalFilters(ImagePost post)
    {
        if (post.Score < MinimumScore)
        {
            return false;
        }

        if (MinimumWidth > 0 && post.Width < MinimumWidth)
        {
            return false;
        }

        if (MinimumHeight > 0 && post.Height < MinimumHeight)
        {
            return false;
        }

        if (_requiredTagTokens.Length == 0 && _excludedTagTokens.Length == 0)
        {
            return true;
        }

        var normalizedTags = NormalizeTagsForFiltering(post.Tags);
        foreach (var token in _requiredTagTokens)
        {
            if (!normalizedTags.Contains($" {token} ", StringComparison.Ordinal))
            {
                return false;
            }
        }

        foreach (var token in _excludedTagTokens)
        {
            if (normalizedTags.Contains($" {token} ", StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizeTagsForFiltering(string tags)
    {
        if (string.IsNullOrWhiteSpace(tags))
        {
            return " ";
        }

        return $" {string.Join(' ',
            tags.Split(new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim().ToLowerInvariant()))} ";
    }

    private static string[] ParseTagFilterTokens(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        return value
            .Split(new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim().ToLowerInvariant())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private void RebuildTagFilterTokens()
    {
        _requiredTagTokens = ParseTagFilterTokens(_requiredTags);
        _excludedTagTokens = ParseTagFilterTokens(_excludedTags);
    }

    private void AddPostsToVisibleCollection(IReadOnlyList<ImagePost> posts)
    {
        if (posts.Count == 0)
        {
            return;
        }

        var usesSortedInsert = SelectedSortOption.Key is SortBySizeDescKey or SortByVotesDescKey;
        foreach (var post in posts)
        {
            if (!ShouldShowPost(post))
            {
                continue;
            }

            if (usesSortedInsert)
            {
                InsertPostSorted(post);
                continue;
            }

            Images.Add(post);
        }
    }

    private void InsertPostSorted(ImagePost post)
    {
        var index = 0;
        while (index < Images.Count && ComparePostsForCurrentSort(Images[index], post) >= 0)
        {
            index++;
        }

        Images.Insert(index, post);
    }

    private int ComparePostsForCurrentSort(ImagePost left, ImagePost right)
    {
        var compare = SelectedSortOption.Key switch
        {
            SortBySizeDescKey => GetPostPixelArea(left).CompareTo(GetPostPixelArea(right)),
            SortByVotesDescKey => left.Score.CompareTo(right.Score),
            _ => GetPostDateRank(left).CompareTo(GetPostDateRank(right))
        };

        if (compare != 0)
        {
            return compare;
        }

        return GetPostDateRank(left).CompareTo(GetPostDateRank(right));
    }

    private bool MatchesSelectedTypeFilter(ImagePost post)
    {
        var path = GetMediaPath(post);
        var isWebm = path.EndsWith(".webm", StringComparison.OrdinalIgnoreCase);
        var isMp4 = path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);
        var isAnimatedImage = path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".apng", StringComparison.OrdinalIgnoreCase);

        return SelectedMediaTypeFilter.Key switch
        {
            MediaTypeImagesKey => !isWebm && !isMp4 && !isAnimatedImage,
            MediaTypeAnimatedKey => isAnimatedImage,
            MediaTypeVideoKey => isWebm || isMp4,
            MediaTypeWebmKey => isWebm,
            MediaTypeMp4Key => isMp4,
            _ => true
        };
    }

    private bool MatchesSizeFilter(ImagePost post)
    {
        var maxDimension = Math.Max(post.Width, post.Height);
        
        return SelectedSizeFilter.Key switch
        {
            SizeFilterLargeKey => maxDimension > 2000,
            SizeFilterMediumKey => maxDimension >= 1000 && maxDimension <= 2000,
            SizeFilterSmallKey => maxDimension > 0 && maxDimension < 1000,
            _ => true
        };
    }

    private static string GetMediaPath(ImagePost post)
    {
        var value = !string.IsNullOrWhiteSpace(post.FullImageUrl)
            ? post.FullImageUrl
            : post.PreviewUrl;

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return uri.AbsolutePath;
        }

        return value ?? string.Empty;
    }

    private static long GetPostDateRank(ImagePost post)
    {
        if (post.CreatedAtUnix > 0)
        {
            return post.CreatedAtUnix;
        }

        return long.TryParse(post.Id, out var idValue)
            ? idValue
            : 0;
    }

    private static long GetPostPixelArea(ImagePost post)
    {
        return post.Width > 0 && post.Height > 0
            ? (long)post.Width * post.Height
            : 0;
    }

    private async Task LoadNextPageAsync()
    {
        await LoadNextPageAsync(_searchCts?.Token ?? CancellationToken.None);
    }

    private async Task LoadNextPageAsync(CancellationToken cancellationToken)
    {
        if (!_hasStartedSearch)
        {
            return;
        }

        if (IsLoading || !_hasMorePages)
        {
            return;
        }

        IsLoading = true;
        StatusText = $"Loading page {_nextPage}...";

        try
        {
            _settings.CredentialsBySite.TryGetValue(SelectedSite, out var savedCreds);

            var results = await _api.SearchAsync(
                SelectedSite,
                SearchText,
                _nextPage,
                SelectedPageSize,
                IncludeSafe,
                IncludeQuestionable,
                IncludeAdult,
                savedCreds,
                cancellationToken);

            if (results.Count == 0)
            {
                _hasMorePages = false;
                StatusText = _allImages.Count == 0 ? "No results" : "No more results";
                return;
            }

            var addedPosts = new List<ImagePost>(results.Count);
            foreach (var post in results)
            {
                var postKey = BuildFavoriteKey(post);
                if (!_loadedPostKeys.Add(postKey))
                {
                    continue;
                }

                post.IsFavorite = _favoriteKeys.Contains(postKey);
                _allImages.Add(post);
                addedPosts.Add(post);
                if (post.IsFavorite)
                {
                    AddOrUpdateFavoriteSnapshot(post);
                }
            }

            if (addedPosts.Count == 0)
            {
                _hasMorePages = false;
                StatusText = _allImages.Count == 0 ? "No results" : $"Loaded {_allImages.Count} posts ({Images.Count} shown)";
                return;
            }

            AddPostsToVisibleCollection(addedPosts);
            _ = LoadPreviewsAsync(addedPosts, cancellationToken);

            _nextPage++;

            StatusText = $"Loaded {_allImages.Count} posts ({Images.Count} shown)";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Search canceled";
        }
        catch (Exception ex)
        {
            _hasMorePages = false;
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static string BuildFavoriteKey(ImagePost post)
    {
        return $"{post.SourceSite.Trim().ToLowerInvariant()}::{post.Id.Trim()}";
    }

    private static bool NeedsMediaResolution(ImagePost post)
    {
        if (string.IsNullOrWhiteSpace(post.FullImageUrl))
        {
            return true;
        }

        if (string.Equals(post.FullImageUrl, post.PreviewUrl, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsLikelySampleOrPreviewMediaUrl(post.FullImageUrl);
    }

    private static bool IsLikelySampleOrPreviewMediaUrl(string url)
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

        var normalized = path.Trim().ToLowerInvariant();
        return normalized.Contains("/samples/", StringComparison.Ordinal)
            || normalized.Contains("/sample/", StringComparison.Ordinal)
            || normalized.Contains("/thumbnails/", StringComparison.Ordinal)
            || normalized.Contains("/thumbnail/", StringComparison.Ordinal)
            || normalized.Contains("sample_", StringComparison.Ordinal)
            || normalized.Contains("thumbnail_", StringComparison.Ordinal);
    }

    private static bool HasTagGroups(ImagePost post)
    {
        return post.TagGroups.Count > 0
            && post.TagGroups.Any(g => g.Value is { Count: > 0 });
    }

    private static void ApplyResolvedPostDetails(ImagePost target, ImagePost resolved)
    {
        if (!string.IsNullOrWhiteSpace(resolved.FullImageUrl))
        {
            target.FullImageUrl = resolved.FullImageUrl;
        }

        if (!string.IsNullOrWhiteSpace(resolved.PreviewUrl))
        {
            target.PreviewUrl = resolved.PreviewUrl;
        }

        if (resolved.Width > 0)
        {
            target.Width = resolved.Width;
        }

        if (resolved.Height > 0)
        {
            target.Height = resolved.Height;
        }

        if (!string.IsNullOrWhiteSpace(resolved.Rating))
        {
            target.Rating = resolved.Rating;
        }

        if (!string.IsNullOrWhiteSpace(resolved.Tags))
        {
            target.Tags = resolved.Tags;
        }

        target.Score = resolved.Score;

        if (resolved.CreatedAtUnix > 0)
        {
            target.CreatedAtUnix = resolved.CreatedAtUnix;
        }

        if (resolved.TagGroups.Count > 0)
        {
            target.TagGroups = CloneTagGroups(resolved.TagGroups);
        }
    }

    private static bool TryMapSourceSite(string sourceSite, out BooruSite site)
    {
        site = BooruSite.Safebooru;
        var normalized = sourceSite?.Trim().ToLowerInvariant() ?? string.Empty;
        switch (normalized)
        {
            case "safebooru":
                site = BooruSite.Safebooru;
                return true;
            case "e621":
                site = BooruSite.E621;
                return true;
            case "danbooru":
                site = BooruSite.Danbooru;
                return true;
            case "gelbooru":
                site = BooruSite.Gelbooru;
                return true;
            case "xbooru":
                site = BooruSite.XBooru;
                return true;
            case "tabbooru":
            case "tab.booru.org":
                site = BooruSite.TabBooru;
                return true;
            case "allgirlbooru":
            case "allgirl.booru.org":
                site = BooruSite.AllGirlBooru;
                return true;
            case "thecollectionbooru":
            case "the-collection.booru.org":
                site = BooruSite.TheCollectionBooru;
                return true;
            default:
                return false;
        }
    }

    private async Task HydrateMissingFavoritesAsync(IReadOnlyList<string> missingFavoriteKeys)
    {
        var hydratedPosts = new List<ImagePost>();
        foreach (var key in missingFavoriteKeys)
        {
            if (!TryParseFavoriteKey(key, out var site, out var postId))
            {
                continue;
            }

            _settings.CredentialsBySite.TryGetValue(site, out var creds);
            var hydrated = await _api.GetPostByIdAsync(site, postId, creds);
            if (hydrated is null)
            {
                continue;
            }

            hydrated.IsFavorite = true;
            AddOrUpdateFavoriteSnapshot(hydrated);
            hydratedPosts.Add(hydrated);
        }

        if (hydratedPosts.Count == 0)
        {
            return;
        }

        _ = LoadPreviewsAsync(hydratedPosts, CancellationToken.None);
        await SaveSettingsAsync();
    }

    private static bool TryParseFavoriteKey(string key, out BooruSite site, out string postId)
    {
        site = BooruSite.Safebooru;
        postId = string.Empty;

        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var parts = key.Split("::", 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
        {
            return false;
        }

        var normalizedSite = parts[0].Trim().ToLowerInvariant();
        site = normalizedSite switch
        {
            "safebooru" => BooruSite.Safebooru,
            "e621" => BooruSite.E621,
            "danbooru" => BooruSite.Danbooru,
            "gelbooru" => BooruSite.Gelbooru,
            "xbooru" => BooruSite.XBooru,
            "tabbooru" or "tab.booru.org" => BooruSite.TabBooru,
            "allgirlbooru" or "allgirl.booru.org" => BooruSite.AllGirlBooru,
            "thecollectionbooru" or "the-collection.booru.org" => BooruSite.TheCollectionBooru,
            _ => BooruSite.Safebooru
        };

        if (normalizedSite is not ("safebooru" or "e621" or "danbooru" or "gelbooru" or "xbooru" or "tabbooru" or "tab.booru.org" or "allgirlbooru" or "allgirl.booru.org" or "thecollectionbooru" or "the-collection.booru.org"))
        {
            return false;
        }

        postId = parts[1];
        return true;
    }

    private void NotifyFavoritesChanged()
    {
        OnPropertyChanged(nameof(FavoritesCount));
        OnPropertyChanged(nameof(FavoritesSummary));
        OnPropertyChanged(nameof(FavoritesTabTitle));
    }

    private void AddOrUpdateFavoriteSnapshot(ImagePost post)
    {
        var key = BuildFavoriteKey(post);
        if (_favoritePostsByKey.TryGetValue(key, out var existing))
        {
            existing.SourceSite = post.SourceSite;
            existing.Id = post.Id;
            existing.PreviewUrl = post.PreviewUrl;
            existing.FullImageUrl = post.FullImageUrl;
            existing.PostUrl = post.PostUrl;
            existing.Rating = post.Rating;
            existing.Tags = post.Tags;
            existing.Score = post.Score;
            existing.CreatedAtUnix = post.CreatedAtUnix;
            existing.TagGroups = CloneTagGroups(post.TagGroups);
            existing.Width = post.Width;
            existing.Height = post.Height;
            existing.IsFavorite = true;
            if (existing.PreviewImage is null && post.PreviewImage is not null)
            {
                existing.PreviewImage = post.PreviewImage;
            }

            return;
        }

        post.IsFavorite = true;
        _favoritePostsByKey[key] = post;
        FavoriteImages.Add(post);
        NotifyFavoritesChanged();
    }

    private void RemoveFavoriteSnapshot(string key)
    {
        if (!_favoritePostsByKey.TryGetValue(key, out var snapshot))
        {
            return;
        }

        _favoritePostsByKey.Remove(key);
        FavoriteImages.Remove(snapshot);
    }

    private void UpdateLoadedFavoriteState(string key, bool isFavorite)
    {
        foreach (var loadedPost in _allImages)
        {
            if (string.Equals(BuildFavoriteKey(loadedPost), key, StringComparison.OrdinalIgnoreCase))
            {
                loadedPost.IsFavorite = isFavorite;
            }
        }
    }

    private async Task SaveSettingsAsync(CancellationToken cancellationToken = default)
    {
        _settings.RecentSearches = RecentSearches.ToList();
        _settings.FavoritePostKeys = _favoriteKeys.ToList();
        _settings.FavoritePosts = FavoriteImages.Select(CreateFavoriteSnapshot).ToList();
        _settings.ResultsPerPage = SelectedPageSize;
        _settings.SearchSortKey = SelectedSortOption.Key;
        _settings.ShowFavoritesOnly = ShowFavoritesOnly;
        _settings.MinimumScore = MinimumScore;
        _settings.MinimumWidth = MinimumWidth;
        _settings.MinimumHeight = MinimumHeight;
        _settings.RequiredTags = RequiredTags;
        _settings.ExcludedTags = ExcludedTags;

        await _settingsSaveGate.WaitAsync(cancellationToken);
        try
        {
            await _credentialsStore.SaveAsync(_settings, cancellationToken);
        }
        finally
        {
            _settingsSaveGate.Release();
        }
    }

    private static ImagePost CreateFavoriteSnapshot(ImagePost post)
    {
        return new ImagePost
        {
            Id = post.Id,
            SourceSite = post.SourceSite,
            PreviewUrl = post.PreviewUrl,
            FullImageUrl = post.FullImageUrl,
            PostUrl = post.PostUrl,
            Rating = post.Rating,
            Tags = post.Tags,
            Score = post.Score,
            CreatedAtUnix = post.CreatedAtUnix,
            TagGroups = CloneTagGroups(post.TagGroups),
            Width = post.Width,
            Height = post.Height,
            IsFavorite = true
        };
    }

    private static Dictionary<string, List<string>> CloneTagGroups(Dictionary<string, List<string>> source)
    {
        var clone = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, values) in source)
        {
            if (string.IsNullOrWhiteSpace(key) || values is null || values.Count == 0)
            {
                continue;
            }

            clone[key] = values
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return clone;
    }

    private static string ResolveExportDirectory()
    {
        var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrWhiteSpace(desktopPath))
        {
            return desktopPath;
        }

        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appDataPath, "BooruManager", "Exports");
    }

    private static string EscapeCsv(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var escaped = value.Replace("\"", "\"\"");
        if (escaped.Contains(',') || escaped.Contains('"') || escaped.Contains('\n') || escaped.Contains('\r'))
        {
            return $"\"{escaped}\"";
        }

        return escaped;
    }

    private void SaveSettingsInBackground()
    {
        _ = SaveSettingsAsync();
    }

    private void CancelCurrentSearch()
    {
        if (_searchCts is null)
        {
            return;
        }

        if (!_searchCts.IsCancellationRequested)
        {
            _searchCts.Cancel();
        }

        _searchCts.Dispose();
        _searchCts = null;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private async Task LoadPreviewsAsync(IReadOnlyList<ImagePost> posts, CancellationToken cancellationToken)
    {
        foreach (var post in posts)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            QueuePreviewLoad(post, PreviewPriorityNormal);
        }

        await Task.CompletedTask;
    }

    private void QueuePreviewLoad(ImagePost post, int priority)
    {
        if (post.PreviewImage is not null || string.IsNullOrWhiteSpace(post.PreviewUrl))
        {
            return;
        }

        lock (_previewQueueGate)
        {
            if (_loadingPreviewPosts.Contains(post))
            {
                return;
            }

            if (_queuedPreviewPriority.TryGetValue(post, out var currentPriority))
            {
                if (priority >= currentPriority)
                {
                    return;
                }
            }

            _queuedPreviewPriority[post] = priority;
            _previewQueue.Enqueue(
                new PreviewWorkItem(post, priority),
                ComposePreviewPriority(priority));
        }

        _previewWorkSignal.Release();
    }

    private void BoostVisiblePreviewLoad(ImagePost post)
    {
        if (post.PreviewImage is not null || string.IsNullOrWhiteSpace(post.PreviewUrl))
        {
            return;
        }

        lock (_previewQueueGate)
        {
            if (_loadingPreviewPosts.Contains(post))
            {
                return;
            }

            _loadingPreviewPosts.Add(post);
            _queuedPreviewPriority.Remove(post);
        }

        _ = Task.Run(async () =>
        {
            await _visiblePreviewGate.WaitAsync();
            try
            {
                var bitmap = await _imageLoader.LoadBitmapAsync(
                    post.PreviewUrl,
                    post.SourceSite,
                    CancellationToken.None);

                if (bitmap is not null)
                {
                    await AssignPreviewImageAsync(post, bitmap);
                }
            }
            catch
            {
            }
            finally
            {
                lock (_previewQueueGate)
                {
                    _loadingPreviewPosts.Remove(post);
                }

                _visiblePreviewGate.Release();
            }
        });
    }

    private async Task PreviewWorkerLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _previewWorkSignal.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            ImagePost? target = null;
            lock (_previewQueueGate)
            {
                while (_previewQueue.Count > 0)
                {
                    var workItem = _previewQueue.Dequeue();
                    var post = workItem.Post;

                    if (post.PreviewImage is not null || string.IsNullOrWhiteSpace(post.PreviewUrl))
                    {
                        _queuedPreviewPriority.Remove(post);
                        continue;
                    }

                    if (_loadingPreviewPosts.Contains(post))
                    {
                        continue;
                    }

                    if (!_queuedPreviewPriority.TryGetValue(post, out var expectedPriority))
                    {
                        continue;
                    }

                    if (workItem.Priority > expectedPriority)
                    {
                        continue;
                    }

                    _queuedPreviewPriority.Remove(post);
                    _loadingPreviewPosts.Add(post);
                    target = post;
                    break;
                }
            }

            if (target is null)
            {
                continue;
            }

            try
            {
                var bitmap = await _imageLoader.LoadBitmapAsync(
                    target.PreviewUrl,
                    target.SourceSite,
                    cancellationToken);

                if (bitmap is not null)
                {
                    await AssignPreviewImageAsync(target, bitmap);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
            }
            finally
            {
                lock (_previewQueueGate)
                {
                    _loadingPreviewPosts.Remove(target);
                }
            }
        }
    }

    private void StartPreviewWorkers()
    {
        for (var i = 0; i < PreviewWorkerCount; i++)
        {
            _ = Task.Run(() => PreviewWorkerLoopAsync(_previewWorkersCts.Token));
        }
    }

    private long ComposePreviewPriority(int priority)
    {
        var sequence = Interlocked.Increment(ref _previewQueueSequence);
        return (priority * 1_000_000_000_000L) + sequence;
    }

    private static async Task AssignPreviewImageAsync(ImagePost post, IImage bitmap)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            post.PreviewImage = bitmap;
            post.IsLoaded = true;
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            post.PreviewImage = bitmap;
            post.IsLoaded = true;
        });
    }

    private readonly record struct PreviewWorkItem(ImagePost Post, int Priority);
}

public class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;

    public AsyncRelayCommand(Func<Task> execute)
    {
        _execute = execute;
    }

    public bool CanExecute(object? parameter) => true;
#pragma warning disable CS0067
    public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067

    public async void Execute(object? parameter)
    {
        await _execute();
    }
}
