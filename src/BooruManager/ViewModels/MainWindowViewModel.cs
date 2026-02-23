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
using System.Collections.Concurrent;

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

    private const string MediaTypeImagesKey = "media_images";
    private const string MediaTypeAnimatedKey = "media_animated";
    private const string MediaTypeVideoKey = "media_video";

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
        new ResultSortOption(MediaTypeImagesKey, "Images"),
        new ResultSortOption(MediaTypeAnimatedKey, "Animated images"),
        new ResultSortOption(MediaTypeVideoKey, "Videos")
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
    private readonly HashSet<string> _blacklistedPostKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly DownloadService _downloadService = new();
    private readonly ConcurrentBag<string> _collectedTags = new();
    private CancellationTokenSource? _tagCollectionCts;
    private readonly Dictionary<string, PostCollection> _collectionsByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PostNote> _postNotesByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _tagBlacklist = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _viewedPostKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _md5ToPostKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DownloadQueueItem> _downloadQueue = new();
    private readonly List<TagStatistic> _tagStatistics = new();
    private bool _downloadQueuePaused;

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
    private string _selectedLanguage = "en";

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
    public IReadOnlyList<LanguageInfo> AvailableLanguages => Services.LocalizationService.Instance.AvailableLanguages;

    public string LocSearch => Services.LocalizationService.Instance["Search"];
    public string LocBooru => Services.LocalizationService.Instance["Booru"];
    public string LocRecent => Services.LocalizationService.Instance["Recent"];
    public string LocClearHistory => Services.LocalizationService.Instance["ClearHistory"];
    public string LocResults => Services.LocalizationService.Instance["Results"];
    public string LocPerPage => Services.LocalizationService.Instance["PerPage"];
    public string LocSort => Services.LocalizationService.Instance["Sort"];
    public string LocMediaType => Services.LocalizationService.Instance["MediaType"];
    public string LocSize => Services.LocalizationService.Instance["Size"];
    public string LocFilters => Services.LocalizationService.Instance["Filters"];
    public string LocMinScore => Services.LocalizationService.Instance["MinScore"];
    public string LocMinWidth => Services.LocalizationService.Instance["MinWidth"];
    public string LocMinHeight => Services.LocalizationService.Instance["MinHeight"];
    public string LocMustInclude => Services.LocalizationService.Instance["MustInclude"];
    public string LocExclude => Services.LocalizationService.Instance["Exclude"];
    public string LocResetFilters => Services.LocalizationService.Instance["ResetFilters"];
    public string LocShuffle => Services.LocalizationService.Instance["Shuffle"];
    public string LocExportCsv => Services.LocalizationService.Instance["ExportCsv"];
    public string LocAccount => Services.LocalizationService.Instance["Account"];
    public string LocLanguage => Services.LocalizationService.Instance["Language"];
    public string LocCheckUpdates => Services.LocalizationService.Instance["CheckUpdates"];
    public string LocBrowse => Services.LocalizationService.Instance["Browse"];
    public string LocTags => Services.LocalizationService.Instance["Tags"];
    public string LocSafe => Services.LocalizationService.Instance["Safe"];
    public string LocQuestionable => Services.LocalizationService.Instance["Questionable"];
    public string LocAdult => Services.LocalizationService.Instance["Adult"];

    public string SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (_selectedLanguage == value)
            {
                return;
            }

            _selectedLanguage = value;
            OnPropertyChanged();
            LocalizationService.Instance.CurrentLanguage = value;
            RefreshLocalizedProperties();
            _ = SaveSettingsAsync();
        }
    }

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

    private int _selectedPostsCount;
    public int SelectedPostsCount
    {
        get => _selectedPostsCount;
        private set
        {
            if (_selectedPostsCount == value) return;
            _selectedPostsCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedPosts));
            OnPropertyChanged(nameof(SelectedPostsSummary));
        }
    }

    public bool HasSelectedPosts => SelectedPostsCount > 0;
    public string SelectedPostsSummary => SelectedPostsCount > 0 ? $"{SelectedPostsCount} selected" : string.Empty;

    private int _downloadProgress;
    public int DownloadProgress
    {
        get => _downloadProgress;
        private set
        {
            if (_downloadProgress == value) return;
            _downloadProgress = value;
            OnPropertyChanged();
        }
    }

    private bool _isDownloading;
    public bool IsDownloading
    {
        get => _isDownloading;
        private set
        {
            if (_isDownloading == value) return;
            _isDownloading = value;
            OnPropertyChanged();
        }
    }

    private int _blacklistedCount;
    public int BlacklistedCount
    {
        get => _blacklistedCount;
        private set
        {
            if (_blacklistedCount == value) return;
            _blacklistedCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BlacklistedSummary));
        }
    }

    public string BlacklistedSummary => BlacklistedCount > 0 ? $"Hidden: {BlacklistedCount}" : string.Empty;

    private bool _slideshowMode;
    public bool SlideshowMode
    {
        get => _slideshowMode;
        set
        {
            if (_slideshowMode == value) return;
            _slideshowMode = value;
            OnPropertyChanged();
        }
    }

    private int _slideshowIntervalSeconds = 3;
    public int SlideshowIntervalSeconds
    {
        get => _slideshowIntervalSeconds;
        set
        {
            var normalized = Math.Clamp(value, 1, 60);
            if (_slideshowIntervalSeconds == normalized) return;
            _slideshowIntervalSeconds = normalized;
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<int> SlideshowIntervalOptions { get; } = new[] { 1, 2, 3, 5, 10, 15, 30 };

    private string _tagAutocompleteQuery = string.Empty;
    private IReadOnlyList<string> _tagSuggestions = Array.Empty<string>();
    public IReadOnlyList<string> TagSuggestions
    {
        get => _tagSuggestions;
        private set
        {
            _tagSuggestions = value;
            OnPropertyChanged();
        }
    }

    public int BlacklistedTotal => _blacklistedPostKeys.Count;

    private string _downloadFolder = string.Empty;
    public string DownloadFolder
    {
        get => _downloadFolder;
        set
        {
            if (_downloadFolder == value) return;
            _downloadFolder = value;
            OnPropertyChanged();
        }
    }

    private bool _downloadCreateSubfolders = true;
    public bool DownloadCreateSubfolders
    {
        get => _downloadCreateSubfolders;
        set
        {
            if (_downloadCreateSubfolders == value) return;
            _downloadCreateSubfolders = value;
            OnPropertyChanged();
        }
    }

    private bool _downloadPreserveFilenames;
    public bool DownloadPreserveFilenames
    {
        get => _downloadPreserveFilenames;
        set
        {
            if (_downloadPreserveFilenames == value) return;
            _downloadPreserveFilenames = value;
            OnPropertyChanged();
        }
    }

    private int _maxConcurrentDownloads = 3;
    public int MaxConcurrentDownloads
    {
        get => _maxConcurrentDownloads;
        set
        {
            var normalized = Math.Clamp(value, 1, 10);
            if (_maxConcurrentDownloads == normalized) return;
            _maxConcurrentDownloads = normalized;
            OnPropertyChanged();
        }
    }

    private int _requestTimeoutSeconds = 30;
    public int RequestTimeoutSeconds
    {
        get => _requestTimeoutSeconds;
        set
        {
            var normalized = Math.Clamp(value, 5, 120);
            if (_requestTimeoutSeconds == normalized) return;
            _requestTimeoutSeconds = normalized;
            OnPropertyChanged();
        }
    }

    private int _cardWidth = 340;
    public int CardWidth
    {
        get => _cardWidth;
        set
        {
            var normalized = Math.Clamp(value, 200, 600);
            if (_cardWidth == normalized) return;
            _cardWidth = normalized;
            OnPropertyChanged();
        }
    }

    private int _cardHeight = 400;
    public int CardHeight
    {
        get => _cardHeight;
        set
        {
            var normalized = Math.Clamp(value, 250, 600);
            if (_cardHeight == normalized) return;
            _cardHeight = normalized;
            OnPropertyChanged();
        }
    }

    private bool _confirmBeforeDownload = true;
    public bool ConfirmBeforeDownload
    {
        get => _confirmBeforeDownload;
        set
        {
            if (_confirmBeforeDownload == value) return;
            _confirmBeforeDownload = value;
            OnPropertyChanged();
        }
    }

    private bool _showDownloadNotifications = true;
    public bool ShowDownloadNotifications
    {
        get => _showDownloadNotifications;
        set
        {
            if (_showDownloadNotifications == value) return;
            _showDownloadNotifications = value;
            OnPropertyChanged();
        }
    }

    private bool _autoStartSlideshow;
    public bool AutoStartSlideshow
    {
        get => _autoStartSlideshow;
        set
        {
            if (_autoStartSlideshow == value) return;
            _autoStartSlideshow = value;
            OnPropertyChanged();
        }
    }

    private string _customUserAgent = string.Empty;
    public string CustomUserAgent
    {
        get => _customUserAgent;
        set
        {
            if (_customUserAgent == value) return;
            _customUserAgent = value;
            OnPropertyChanged();
        }
    }

    public ObservableCollection<PostCollection> Collections { get; } = new();
    public ObservableCollection<DownloadQueueItem> DownloadQueueItems { get; } = new();
    public ObservableCollection<TagStatistic> TagStatistics { get; } = new();

    private PostCollection? _selectedCollection;
    public PostCollection? SelectedCollection
    {
        get => _selectedCollection;
        set
        {
            if (_selectedCollection == value) return;
            _selectedCollection = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCollectionSelected));
            if (value != null)
            {
                FilterByCollection(value);
            }
        }
    }

    public bool IsCollectionSelected => SelectedCollection != null;

    private int _thumbnailSize = 100;
    public int ThumbnailSize
    {
        get => _thumbnailSize;
        set
        {
            var normalized = Math.Clamp(value, 50, 200);
            if (_thumbnailSize == normalized) return;
            _thumbnailSize = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ThumbnailCellWidth));
            OnPropertyChanged(nameof(ThumbnailCellHeight));
        }
    }

    public double ThumbnailCellWidth => 220 + (ThumbnailSize - 100) * 1.5;
    public double ThumbnailCellHeight => 280 + (ThumbnailSize - 100) * 1.5;

    private bool _showTagStatistics = true;
    public bool ShowTagStatistics
    {
        get => _showTagStatistics;
        set
        {
            if (_showTagStatistics == value) return;
            _showTagStatistics = value;
            OnPropertyChanged();
        }
    }

    private bool _detectDuplicates = true;
    public bool DetectDuplicates
    {
        get => _detectDuplicates;
        set
        {
            if (_detectDuplicates == value) return;
            _detectDuplicates = value;
            OnPropertyChanged();
        }
    }

    private bool _trackViewedPosts = true;
    public bool TrackViewedPosts
    {
        get => _trackViewedPosts;
        set
        {
            if (_trackViewedPosts == value) return;
            _trackViewedPosts = value;
            OnPropertyChanged();
        }
    }

    private string _tagBlacklistText = string.Empty;
    public string TagBlacklistText
    {
        get => _tagBlacklistText;
        set
        {
            if (_tagBlacklistText == value) return;
            _tagBlacklistText = value;
            OnPropertyChanged();
        }
    }

    private int _viewedCount;
    public int ViewedCount
    {
        get => _viewedCount;
        private set
        {
            if (_viewedCount == value) return;
            _viewedCount = value;
            OnPropertyChanged();
        }
    }

    private int _duplicateCount;
    public int DuplicateCount
    {
        get => _duplicateCount;
        private set
        {
            if (_duplicateCount == value) return;
            _duplicateCount = value;
            OnPropertyChanged();
        }
    }

    private int _queueCount;
    public int QueueCount
    {
        get => _queueCount;
        private set
        {
            if (_queueCount == value) return;
            _queueCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(QueueSummary));
        }
    }

    public string QueueSummary => QueueCount > 0 ? $"Queue: {QueueCount}" : string.Empty;

    public ICommand OpenSettingsCommand { get; }

    public ICommand SearchCommand { get; }
    public ICommand LoadMoreCommand { get; }
    public ICommand LoginToggleCommand { get; }
    public ICommand ClearHistoryCommand { get; }
    public ICommand ResetLocalFiltersCommand { get; }
    public ICommand ShuffleVisibleCommand { get; }
    public ICommand ExportVisibleCommand { get; }
    public ICommand DownloadSelectedCommand { get; }
    public ICommand ClearSelectionCommand { get; }
    public ICommand ToggleBlacklistCommand { get; }
    public ICommand ClearBlacklistCommand { get; }
    public ICommand ToggleSlideshowCommand { get; }
    public ICommand CreateCollectionCommand { get; }
    public ICommand AddToCollectionCommand { get; }
    public ICommand RemoveFromCollectionCommand { get; }
    public ICommand DeleteCollectionCommand { get; }
    public ICommand QuickTagSearchCommand { get; }
    public ICommand AddTagToBlacklistCommand { get; }
    public ICommand RemoveTagFromBlacklistCommand { get; }
    public ICommand AddNoteToPostCommand { get; }
    public ICommand AddToDownloadQueueCommand { get; }
    public ICommand PauseDownloadQueueCommand { get; }
    public ICommand ClearDownloadQueueCommand { get; }
    public ICommand CopyTagsFromSelectedCommand { get; }
    public ICommand ClearViewedHistoryCommand { get; }

    public MainWindowViewModel()
    {
        SearchCommand = new AsyncRelayCommand(StartSearchAsync);
        LoadMoreCommand = new AsyncRelayCommand(LoadNextPageAsync);
        LoginToggleCommand = new AsyncRelayCommand(ToggleLoginAsync);
        ClearHistoryCommand = new AsyncRelayCommand(ClearHistoryAsync);
        ResetLocalFiltersCommand = new AsyncRelayCommand(ResetLocalFiltersAsync);
        ShuffleVisibleCommand = new AsyncRelayCommand(ShuffleVisibleAsync);
        ExportVisibleCommand = new AsyncRelayCommand(ExportVisibleAsync);
        DownloadSelectedCommand = new AsyncRelayCommand(DownloadSelectedAsync);
        ClearSelectionCommand = new AsyncRelayCommand(ClearSelectionAsync);
        ToggleBlacklistCommand = new AsyncRelayCommand(ToggleBlacklistOnSelectedAsync);
        ClearBlacklistCommand = new AsyncRelayCommand(ClearBlacklistAsync);
        ToggleSlideshowCommand = new RelayCommand(_ => ToggleSlideshow());
        OpenSettingsCommand = new AsyncRelayCommand(OpenSettingsAsync);
        CreateCollectionCommand = new AsyncRelayCommand(CreateCollectionAsync);
        AddToCollectionCommand = new AsyncRelayCommand(AddSelectedToCollectionAsync);
        RemoveFromCollectionCommand = new AsyncRelayCommand(RemoveSelectedFromCollectionAsync);
        DeleteCollectionCommand = new AsyncRelayCommand(DeleteCollectionAsync);
        QuickTagSearchCommand = new RelayCommand(tag => QuickTagSearch(tag?.ToString()));
        AddTagToBlacklistCommand = new AsyncRelayCommand(AddTagToBlacklistAsync);
        RemoveTagFromBlacklistCommand = new AsyncRelayCommand(RemoveTagFromBlacklistAsync);
        AddNoteToPostCommand = new AsyncRelayCommand(AddNoteToPostAsync);
        AddToDownloadQueueCommand = new AsyncRelayCommand(AddSelectedToDownloadQueueAsync);
        PauseDownloadQueueCommand = new RelayCommand(_ => ToggleDownloadQueuePause());
        ClearDownloadQueueCommand = new AsyncRelayCommand(ClearDownloadQueueAsync);
        CopyTagsFromSelectedCommand = new AsyncRelayCommand(CopyTagsFromSelectedAsync);
        ClearViewedHistoryCommand = new AsyncRelayCommand(ClearViewedHistoryAsync);

        StartPreviewWorkers();
        _ = InitializeAsync();
    }

    public event Action? RequestSettingsWindow;

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

        _slideshowIntervalSeconds = SlideshowIntervalOptions.Contains(_settings.SlideshowIntervalSeconds)
            ? _settings.SlideshowIntervalSeconds
            : 3;

        _downloadFolder = _settings.DownloadFolder ?? string.Empty;
        _downloadCreateSubfolders = _settings.DownloadCreateSubfolders;
        _downloadPreserveFilenames = _settings.DownloadPreserveOriginalFilenames;
        _maxConcurrentDownloads = Math.Clamp(_settings.MaxConcurrentDownloads, 1, 10);
        _requestTimeoutSeconds = Math.Clamp(_settings.RequestTimeoutSeconds, 5, 120);
        _cardWidth = Math.Clamp(_settings.CardWidth, 200, 600);
        _cardHeight = Math.Clamp(_settings.CardHeight, 250, 600);
        _confirmBeforeDownload = _settings.ConfirmBeforeDownload;
        _showDownloadNotifications = _settings.ShowDownloadNotifications;
        _autoStartSlideshow = _settings.AutoStartSlideshow;
        _customUserAgent = _settings.CustomUserAgent ?? string.Empty;
        _thumbnailSize = Math.Clamp(_settings.ThumbnailSize, 50, 200);
        _showTagStatistics = _settings.ShowTagStatistics;
        _detectDuplicates = _settings.DetectDuplicates;
        _trackViewedPosts = _settings.TrackViewedPosts;

        Collections.Clear();
        _collectionsByKey.Clear();
        foreach (var collection in _settings.Collections)
        {
            if (string.IsNullOrWhiteSpace(collection.Id)) continue;
            _collectionsByKey[collection.Id] = collection;
            Collections.Add(collection);
        }

        _postNotesByKey.Clear();
        foreach (var note in _settings.PostNotes)
        {
            if (!string.IsNullOrWhiteSpace(note.PostKey))
            {
                _postNotesByKey[note.PostKey] = note;
            }
        }

        _tagBlacklist.Clear();
        foreach (var entry in _settings.TagBlacklist)
        {
            if (!string.IsNullOrWhiteSpace(entry.Tag))
            {
                _tagBlacklist.Add(entry.Tag.Trim().ToLowerInvariant());
            }
        }

        _viewedPostKeys.Clear();
        foreach (var viewed in _settings.ViewedPosts.Take(_settings.MaxViewedPostsHistory))
        {
            if (!string.IsNullOrWhiteSpace(viewed.PostKey))
            {
                _viewedPostKeys.Add(viewed.PostKey.Trim());
            }
        }
        ViewedCount = _viewedPostKeys.Count;

        if (!string.IsNullOrEmpty(_settings.Language))
        {
            _selectedLanguage = _settings.Language;
            LocalizationService.Instance.CurrentLanguage = _settings.Language;
        }
        RefreshLocalizedProperties();

        _blacklistedPostKeys.Clear();
        foreach (var key in _settings.BlacklistedPostKeys)
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                _blacklistedPostKeys.Add(key.Trim());
            }
        }
        BlacklistedCount = _allImages.Count(p => _blacklistedPostKeys.Contains(BuildFavoriteKey(p)));
        OnPropertyChanged(nameof(BlacklistedTotal));

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

        var key = BuildFavoriteKey(post);
        if (_blacklistedPostKeys.Contains(key))
        {
            return false;
        }

        if (MatchesTagBlacklist(post))
        {
            return false;
        }

        if (SelectedCollection != null && !SelectedCollection.PostKeys.Contains(key))
        {
            return false;
        }

        return MatchesSelectedTypeFilter(post) && MatchesSizeFilter(post) && MatchesLocalFilters(post);
    }

    private bool MatchesTagBlacklist(ImagePost post)
    {
        if (_tagBlacklist.Count == 0 || string.IsNullOrWhiteSpace(post.Tags))
        {
            return false;
        }

        var postTags = post.Tags.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim().ToLowerInvariant())
            .ToHashSet();

        return postTags.Any(t => _tagBlacklist.Contains(t));
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

    private void RefreshLocalizedProperties()
    {
        OnPropertyChanged(nameof(LocSearch));
        OnPropertyChanged(nameof(LocBooru));
        OnPropertyChanged(nameof(LocRecent));
        OnPropertyChanged(nameof(LocClearHistory));
        OnPropertyChanged(nameof(LocResults));
        OnPropertyChanged(nameof(LocPerPage));
        OnPropertyChanged(nameof(LocSort));
        OnPropertyChanged(nameof(LocMediaType));
        OnPropertyChanged(nameof(LocSize));
        OnPropertyChanged(nameof(LocFilters));
        OnPropertyChanged(nameof(LocMinScore));
        OnPropertyChanged(nameof(LocMinWidth));
        OnPropertyChanged(nameof(LocMinHeight));
        OnPropertyChanged(nameof(LocMustInclude));
        OnPropertyChanged(nameof(LocExclude));
        OnPropertyChanged(nameof(LocResetFilters));
        OnPropertyChanged(nameof(LocShuffle));
        OnPropertyChanged(nameof(LocExportCsv));
        OnPropertyChanged(nameof(LocAccount));
        OnPropertyChanged(nameof(LocLanguage));
        OnPropertyChanged(nameof(LocCheckUpdates));
        OnPropertyChanged(nameof(LocBrowse));
        OnPropertyChanged(nameof(LocTags));
        OnPropertyChanged(nameof(LocSafe));
        OnPropertyChanged(nameof(LocQuestionable));
        OnPropertyChanged(nameof(LocAdult));
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
        var isVideo = path.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);
        var isAnimatedImage = path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".apng", StringComparison.OrdinalIgnoreCase);

        return SelectedMediaTypeFilter.Key switch
        {
            MediaTypeImagesKey => !isVideo && !isAnimatedImage,
            MediaTypeAnimatedKey => isAnimatedImage,
            MediaTypeVideoKey => isVideo,
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
            if (uri.Host.Equals("thumbs.booru.org", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

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
        _settings.Language = SelectedLanguage;
        _settings.BlacklistedPostKeys = _blacklistedPostKeys.ToList();
        _settings.SlideshowIntervalSeconds = SlideshowIntervalSeconds;
        _settings.DownloadFolder = DownloadFolder;
        _settings.DownloadCreateSubfolders = DownloadCreateSubfolders;
        _settings.DownloadPreserveOriginalFilenames = DownloadPreserveFilenames;
        _settings.MaxConcurrentDownloads = MaxConcurrentDownloads;
        _settings.RequestTimeoutSeconds = RequestTimeoutSeconds;
        _settings.CardWidth = CardWidth;
        _settings.CardHeight = CardHeight;
        _settings.ConfirmBeforeDownload = ConfirmBeforeDownload;
        _settings.ShowDownloadNotifications = ShowDownloadNotifications;
        _settings.AutoStartSlideshow = AutoStartSlideshow;
        _settings.CustomUserAgent = CustomUserAgent;
        _settings.Collections = Collections.ToList();
        _settings.PostNotes = _postNotesByKey.Values.ToList();
        _settings.TagBlacklist = _tagBlacklist.Select(t => new TagBlacklistEntry { Tag = t }).ToList();
        _settings.ViewedPosts = _viewedPostKeys.Select(k => new ViewedPost { PostKey = k }).ToList();
        _settings.ThumbnailSize = ThumbnailSize;
        _settings.ShowTagStatistics = ShowTagStatistics;
        _settings.DetectDuplicates = DetectDuplicates;
        _settings.TrackViewedPosts = TrackViewedPosts;

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

    public void UpdateSelectedCount()
    {
        var count = Images.Count(p => p.IsSelected) + FavoriteImages.Count(p => p.IsSelected);
        SelectedPostsCount = count;
    }

    public IReadOnlyList<ImagePost> GetSelectedPosts()
    {
        var selected = new List<ImagePost>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var post in Images.Where(p => p.IsSelected).Concat(FavoriteImages.Where(p => p.IsSelected)))
        {
            var key = BuildFavoriteKey(post);
            if (seenKeys.Add(key))
            {
                selected.Add(post);
            }
        }

        return selected;
    }

    private async Task DownloadSelectedAsync()
    {
        var selected = GetSelectedPosts();
        if (selected.Count == 0)
        {
            StatusText = "No posts selected";
            return;
        }

        IsDownloading = true;
        DownloadProgress = 0;
        StatusText = $"Downloading {selected.Count} posts...";

        var baseDownloadDir = !string.IsNullOrWhiteSpace(DownloadFolder)
            ? DownloadFolder
            : Path.Combine(ResolveExportDirectory(), "BooruDownloads");

        string downloadDir;
        if (DownloadCreateSubfolders)
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            downloadDir = Path.Combine(baseDownloadDir, $"download_{timestamp}");
        }
        else
        {
            downloadDir = baseDownloadDir;
        }

        Directory.CreateDirectory(downloadDir);

        var successCount = 0;
        var failCount = 0;

        for (var i = 0; i < selected.Count; i++)
        {
            var post = selected[i];
            var url = !string.IsNullOrWhiteSpace(post.FullImageUrl) ? post.FullImageUrl : post.PreviewUrl;

            if (string.IsNullOrWhiteSpace(url))
            {
                failCount++;
                continue;
            }

            string fileName;
            if (DownloadPreserveFilenames)
            {
                var uri = new Uri(url);
                fileName = Path.GetFileName(uri.LocalPath);
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    fileName = _downloadService.GenerateFileName(post.SourceSite, post.Id, url);
                }
            }
            else
            {
                fileName = _downloadService.GenerateFileName(post.SourceSite, post.Id, url);
            }

            var filePath = Path.Combine(downloadDir, fileName);

            var result = await _downloadService.DownloadPostAsync(url, filePath);
            if (result.Success)
            {
                successCount++;
            }
            else
            {
                failCount++;
            }

            DownloadProgress = (int)((i + 1) / (double)selected.Count * 100);
            StatusText = $"Downloading {i + 1}/{selected.Count}...";
        }

        IsDownloading = false;
        DownloadProgress = 100;
        StatusText = $"Download complete: {successCount} saved, {failCount} failed. Folder: {downloadDir}";
    }

    public async Task ClearSelectionAsync()
    {
        foreach (var post in Images)
        {
            post.IsSelected = false;
        }

        foreach (var post in FavoriteImages)
        {
            post.IsSelected = false;
        }

        SelectedPostsCount = 0;
        StatusText = "Selection cleared";
        await Task.CompletedTask;
    }

    private async Task ToggleBlacklistOnSelectedAsync()
    {
        var selected = GetSelectedPosts();
        if (selected.Count == 0)
        {
            StatusText = "No posts selected";
            return;
        }

        var addedCount = 0;
        var removedCount = 0;

        foreach (var post in selected)
        {
            var key = BuildFavoriteKey(post);
            if (_blacklistedPostKeys.Add(key))
            {
                addedCount++;
            }
            else
            {
                _blacklistedPostKeys.Remove(key);
                removedCount++;
            }
        }

        BlacklistedCount = _allImages.Count(p => _blacklistedPostKeys.Contains(BuildFavoriteKey(p)));
        OnPropertyChanged(nameof(BlacklistedTotal));
        ApplyVisibleFilter();
        await ClearSelectionAsync();

        if (addedCount > 0)
        {
            StatusText = $"Hidden {addedCount} posts";
        }
        else if (removedCount > 0)
        {
            StatusText = $"Unhidden {removedCount} posts";
        }

        await SaveSettingsAsync();
    }

    private async Task ClearBlacklistAsync()
    {
        var count = _blacklistedPostKeys.Count;
        _blacklistedPostKeys.Clear();
        BlacklistedCount = 0;
        OnPropertyChanged(nameof(BlacklistedTotal));
        ApplyVisibleFilter();
        StatusText = $"Cleared {count} hidden posts";
        await SaveSettingsAsync();
    }

    public async Task ToggleBlacklistForPostAsync(ImagePost post)
    {
        var key = BuildFavoriteKey(post);
        if (_blacklistedPostKeys.Add(key))
        {
            StatusText = "Post hidden";
        }
        else
        {
            _blacklistedPostKeys.Remove(key);
            StatusText = "Post unhidden";
        }

        BlacklistedCount = _allImages.Count(p => _blacklistedPostKeys.Contains(BuildFavoriteKey(p)));
        OnPropertyChanged(nameof(BlacklistedTotal));
        ApplyVisibleFilter();
        await SaveSettingsAsync();
    }

    private void ToggleSlideshow()
    {
        SlideshowMode = !SlideshowMode;
        StatusText = SlideshowMode ? "Slideshow mode enabled" : "Slideshow mode disabled";
    }

    private async Task OpenSettingsAsync()
    {
        RequestSettingsWindow?.Invoke();
        await Task.CompletedTask;
    }

    public void ApplySettings(
        string? downloadFolder,
        string? language,
        int slideshowInterval,
        int maxDownloads,
        int timeout,
        int cardWidth,
        int cardHeight,
        bool createSubfolders,
        bool preserveFilenames,
        bool confirmDownload,
        bool showNotifications,
        bool autoStartSlideshow,
        string customUserAgent)
    {
        if (!string.IsNullOrWhiteSpace(downloadFolder))
        {
            DownloadFolder = downloadFolder;
        }

        if (!string.IsNullOrWhiteSpace(language) && language != SelectedLanguage)
        {
            SelectedLanguage = language;
        }

        SlideshowIntervalSeconds = slideshowInterval;
        MaxConcurrentDownloads = maxDownloads;
        RequestTimeoutSeconds = timeout;
        CardWidth = cardWidth;
        CardHeight = cardHeight;
        DownloadCreateSubfolders = createSubfolders;
        DownloadPreserveFilenames = preserveFilenames;
        ConfirmBeforeDownload = confirmDownload;
        ShowDownloadNotifications = showNotifications;
        AutoStartSlideshow = autoStartSlideshow;
        CustomUserAgent = customUserAgent;

        _ = SaveSettingsAsync();
        StatusText = "Settings saved";
    }

    public async Task CollectTagsFromVisiblePostsAsync()
    {
        _tagCollectionCts?.Cancel();
        _tagCollectionCts = new CancellationTokenSource();
        var token = _tagCollectionCts.Token;

        await Task.Run(() =>
        {
            foreach (var post in Images)
            {
                if (token.IsCancellationRequested) break;

                if (string.IsNullOrWhiteSpace(post.Tags)) continue;

                var tags = post.Tags.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var tag in tags)
                {
                    if (!string.IsNullOrWhiteSpace(tag))
                    {
                        _collectedTags.Add(tag.Trim().ToLowerInvariant());
                    }
                }
            }
        }, token);

        StatusText = $"Collected {_collectedTags.Count} unique tags";
    }

    public IReadOnlyList<string> GetTagSuggestions(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || _collectedTags.IsEmpty)
        {
            return Array.Empty<string>();
        }

        var normalized = query.Trim().ToLowerInvariant();
        return _collectedTags
            .Where(t => t.Contains(normalized, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t)
            .Take(20)
            .ToList();
    }

    public async Task NavigateNextAsync()
    {
        if (Images.Count == 0) return;

        var currentSelected = Images.FirstOrDefault(p => p.IsSelected);
        var currentIndex = currentSelected != null ? Images.IndexOf(currentSelected) : -1;
        var nextIndex = currentIndex < Images.Count - 1 ? currentIndex + 1 : 0;

        if (currentSelected != null)
        {
            currentSelected.IsSelected = false;
        }

        Images[nextIndex].IsSelected = true;
        SelectedPostsCount = 1;
        StatusText = $"Viewing {nextIndex + 1}/{Images.Count}";

        await Task.CompletedTask;
    }

    public async Task NavigatePreviousAsync()
    {
        if (Images.Count == 0) return;

        var currentSelected = Images.FirstOrDefault(p => p.IsSelected);
        var currentIndex = currentSelected != null ? Images.IndexOf(currentSelected) : 0;
        var prevIndex = currentIndex > 0 ? currentIndex - 1 : Images.Count - 1;

        if (currentSelected != null)
        {
            currentSelected.IsSelected = false;
        }

        Images[prevIndex].IsSelected = true;
        SelectedPostsCount = 1;
        StatusText = $"Viewing {prevIndex + 1}/{Images.Count}";

        await Task.CompletedTask;
    }

    public void MarkPostAsViewed(ImagePost post)
    {
        if (!TrackViewedPosts) return;
        
        var key = BuildFavoriteKey(post);
        if (_viewedPostKeys.Add(key))
        {
            post.IsViewed = true;
            ViewedCount = _viewedPostKeys.Count;
        }
    }

    private void FilterByCollection(PostCollection collection)
    {
        if (collection == null)
        {
            ApplyVisibleFilter();
            return;
        }

        Images.Clear();
        foreach (var post in _allImages)
        {
            var key = BuildFavoriteKey(post);
            if (collection.PostKeys.Contains(key) && ShouldShowPost(post))
            {
                Images.Add(post);
            }
        }
        
        StatusText = $"Showing {Images.Count} posts from collection '{collection.Name}'";
    }

    private async Task CreateCollectionAsync()
    {
        var name = $"Collection {Collections.Count + 1}";
        var collection = new PostCollection
        {
            Name = name,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _collectionsByKey[collection.Id] = collection;
        Collections.Add(collection);
        _settings.Collections = Collections.ToList();
        await SaveSettingsAsync();
        StatusText = $"Created collection '{name}'";
    }

    private async Task AddSelectedToCollectionAsync()
    {
        if (SelectedCollection == null)
        {
            StatusText = "No collection selected";
            return;
        }

        var selected = GetSelectedPosts();
        if (selected.Count == 0)
        {
            StatusText = "No posts selected";
            return;
        }

        var added = 0;
        foreach (var post in selected)
        {
            var key = BuildFavoriteKey(post);
            if (SelectedCollection.PostKeys.Add(key))
            {
                added++;
            }
        }

        SelectedCollection.UpdatedAt = DateTime.UtcNow;
        await SaveSettingsAsync();
        await ClearSelectionAsync();
        StatusText = $"Added {added} posts to '{SelectedCollection.Name}'";
    }

    private async Task RemoveSelectedFromCollectionAsync()
    {
        if (SelectedCollection == null)
        {
            StatusText = "No collection selected";
            return;
        }

        var selected = GetSelectedPosts();
        if (selected.Count == 0)
        {
            StatusText = "No posts selected";
            return;
        }

        var removed = 0;
        foreach (var post in selected)
        {
            var key = BuildFavoriteKey(post);
            if (SelectedCollection.PostKeys.Remove(key))
            {
                removed++;
            }
        }

        SelectedCollection.UpdatedAt = DateTime.UtcNow;
        FilterByCollection(SelectedCollection);
        await SaveSettingsAsync();
        await ClearSelectionAsync();
        StatusText = $"Removed {removed} posts from '{SelectedCollection.Name}'";
    }

    private async Task DeleteCollectionAsync()
    {
        if (SelectedCollection == null)
        {
            StatusText = "No collection selected";
            return;
        }

        var name = SelectedCollection.Name;
        _collectionsByKey.Remove(SelectedCollection.Id);
        Collections.Remove(SelectedCollection);
        SelectedCollection = null;
        
        _settings.Collections = Collections.ToList();
        await SaveSettingsAsync();
        ApplyVisibleFilter();
        StatusText = $"Deleted collection '{name}'";
    }

    public void QuickTagSearch(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return;
        
        var normalizedTag = tag.Trim().Replace(' ', '_');
        SearchText = normalizedTag;
        SearchCommand.Execute(null);
    }

    public void QuickTagSearchAppend(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return;
        
        var normalizedTag = tag.Trim().Replace(' ', '_');
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            SearchText = normalizedTag;
        }
        else
        {
            SearchText = $"{SearchText} {normalizedTag}";
        }
    }

    private async Task AddTagToBlacklistAsync()
    {
        if (string.IsNullOrWhiteSpace(TagBlacklistText))
        {
            StatusText = "Enter a tag to blacklist";
            return;
        }

        var tags = TagBlacklistText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var added = 0;
        foreach (var tag in tags)
        {
            var normalized = tag.Trim().ToLowerInvariant();
            if (_tagBlacklist.Add(normalized))
            {
                added++;
            }
        }

        TagBlacklistText = string.Empty;
        _settings.TagBlacklist = _tagBlacklist.Select(t => new TagBlacklistEntry { Tag = t }).ToList();
        ApplyVisibleFilter();
        await SaveSettingsAsync();
        StatusText = $"Blacklisted {added} tags";
    }

    private async Task RemoveTagFromBlacklistAsync()
    {
        if (string.IsNullOrWhiteSpace(TagBlacklistText))
        {
            StatusText = "Enter a tag to remove from blacklist";
            return;
        }

        var tags = TagBlacklistText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var removed = 0;
        foreach (var tag in tags)
        {
            var normalized = tag.Trim().ToLowerInvariant();
            if (_tagBlacklist.Remove(normalized))
            {
                removed++;
            }
        }

        TagBlacklistText = string.Empty;
        _settings.TagBlacklist = _tagBlacklist.Select(t => new TagBlacklistEntry { Tag = t }).ToList();
        ApplyVisibleFilter();
        await SaveSettingsAsync();
        StatusText = $"Removed {removed} tags from blacklist";
    }

    public async Task AddNoteToPostAsync()
    {
        var selected = GetSelectedPosts();
        if (selected.Count != 1)
        {
            StatusText = "Select exactly one post to add a note";
            return;
        }

        var post = selected[0];
        var key = BuildFavoriteKey(post);
        
        if (_postNotesByKey.TryGetValue(key, out var existingNote))
        {
            existingNote.Note = $"Updated at {DateTime.Now:HH:mm}";
            existingNote.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            var note = new PostNote
            {
                PostKey = key,
                Note = $"Note added {DateTime.Now:HH:mm}",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _postNotesByKey[key] = note;
        }

        post.HasNote = true;
        _settings.PostNotes = _postNotesByKey.Values.ToList();
        await SaveSettingsAsync();
        StatusText = "Note added to post";
    }

    public string? GetPostNote(ImagePost post)
    {
        var key = BuildFavoriteKey(post);
        return _postNotesByKey.TryGetValue(key, out var note) ? note.Note : null;
    }

    public async Task UpdatePostNoteAsync(ImagePost post, string noteText)
    {
        var key = BuildFavoriteKey(post);
        
        if (string.IsNullOrWhiteSpace(noteText))
        {
            _postNotesByKey.Remove(key);
            post.HasNote = false;
        }
        else
        {
            if (_postNotesByKey.TryGetValue(key, out var existing))
            {
                existing.Note = noteText;
                existing.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                _postNotesByKey[key] = new PostNote
                {
                    PostKey = key,
                    Note = noteText,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
            }
            post.HasNote = true;
        }

        _settings.PostNotes = _postNotesByKey.Values.ToList();
        await SaveSettingsAsync();
    }

    private async Task AddSelectedToDownloadQueueAsync()
    {
        var selected = GetSelectedPosts();
        if (selected.Count == 0)
        {
            StatusText = "No posts selected";
            return;
        }

        var baseDir = !string.IsNullOrWhiteSpace(DownloadFolder)
            ? DownloadFolder
            : Path.Combine(ResolveExportDirectory(), "BooruDownloads");

        foreach (var post in selected)
        {
            var url = !string.IsNullOrWhiteSpace(post.FullImageUrl) ? post.FullImageUrl : post.PreviewUrl;
            if (string.IsNullOrWhiteSpace(url)) continue;

            var fileName = _downloadService.GenerateFileName(post.SourceSite, post.Id, url);
            var item = new DownloadQueueItem
            {
                PostKey = BuildFavoriteKey(post),
                Url = url,
                DestinationPath = Path.Combine(baseDir, fileName),
                AddedAt = DateTime.UtcNow
            };

            _downloadQueue.Add(item);
            DownloadQueueItems.Add(item);
        }

        QueueCount = _downloadQueue.Count;
        await ClearSelectionAsync();
        StatusText = $"Added {selected.Count} posts to download queue";
        
        if (!_downloadQueuePaused)
        {
            _ = ProcessDownloadQueueAsync();
        }
    }

    private async Task ProcessDownloadQueueAsync()
    {
        while (_downloadQueue.Count > 0 && !_downloadQueuePaused)
        {
            var item = _downloadQueue.FirstOrDefault(i => i.Status == "pending");
            if (item == null) break;

            item.Status = "downloading";
            
            var result = await _downloadService.DownloadPostAsync(item.Url, item.DestinationPath);
            
            item.Status = result.Success ? "completed" : "failed";
            item.Error = result.Error;
            item.Progress = result.Success ? 100 : 0;
            item.CompletedAt = DateTime.UtcNow;

            if (result.Success)
            {
                _downloadQueue.Remove(item);
                DownloadQueueItems.Remove(item);
                QueueCount = _downloadQueue.Count;
            }

            await Task.Delay(100);
        }
    }

    private void ToggleDownloadQueuePause()
    {
        _downloadQueuePaused = !_downloadQueuePaused;
        StatusText = _downloadQueuePaused ? "Download queue paused" : "Download queue resumed";
        
        if (!_downloadQueuePaused)
        {
            _ = ProcessDownloadQueueAsync();
        }
    }

    private async Task ClearDownloadQueueAsync()
    {
        _downloadQueue.Clear();
        DownloadQueueItems.Clear();
        QueueCount = 0;
        await Task.CompletedTask;
        StatusText = "Download queue cleared";
    }

    private async Task CopyTagsFromSelectedAsync()
    {
        var selected = GetSelectedPosts();
        if (selected.Count == 0)
        {
            StatusText = "No posts selected";
            return;
        }

        var allTags = selected
            .SelectMany(p => p.Tags?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t)
            .ToList();

        var tagString = string.Join(' ', allTags);
        SearchText = tagString;
        StatusText = $"Copied {allTags.Count} unique tags to search";
        await Task.CompletedTask;
    }

    private async Task ClearViewedHistoryAsync()
    {
        var count = _viewedPostKeys.Count;
        _viewedPostKeys.Clear();
        
        foreach (var post in _allImages)
        {
            post.IsViewed = false;
        }

        ViewedCount = 0;
        _settings.ViewedPosts.Clear();
        await SaveSettingsAsync();
        StatusText = $"Cleared {count} viewed posts from history";
    }

    public void UpdateTagStatistics()
    {
        if (!ShowTagStatistics) return;

        var tagCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var totalPosts = Images.Count;

        foreach (var post in Images)
        {
            if (string.IsNullOrWhiteSpace(post.Tags)) continue;

            var tags = post.Tags.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var tag in tags)
            {
                var normalized = tag.Trim().ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    tagCounts[normalized] = tagCounts.GetValueOrDefault(normalized) + 1;
                }
            }
        }

        TagStatistics.Clear();
        _tagStatistics.Clear();

        foreach (var (tag, count) in tagCounts.OrderByDescending(t => t.Value).Take(50))
        {
            var stat = new TagStatistic
            {
                Tag = tag,
                Count = count,
                Percentage = totalPosts > 0 ? Math.Round((double)count / totalPosts * 100, 1) : 0
            };
            _tagStatistics.Add(stat);
            TagStatistics.Add(stat);
        }
    }

    public void DetectDuplicatePosts()
    {
        if (!DetectDuplicates) return;

        _md5ToPostKeys.Clear();
        var duplicatesFound = 0;

        foreach (var post in _allImages)
        {
            if (string.IsNullOrWhiteSpace(post.Md5Hash)) continue;

            if (!_md5ToPostKeys.TryGetValue(post.Md5Hash, out var keys))
            {
                keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _md5ToPostKeys[post.Md5Hash] = keys;
            }

            if (keys.Count > 0)
            {
                post.IsDuplicate = true;
                duplicatesFound++;
            }

            keys.Add(BuildFavoriteKey(post));
        }

        DuplicateCount = duplicatesFound;
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

public class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;

    public RelayCommand(Action<object?> execute)
    {
        _execute = execute;
    }

    public bool CanExecute(object? parameter) => true;
#pragma warning disable CS0067
    public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067

    public void Execute(object? parameter)
    {
        _execute(parameter);
    }
}
