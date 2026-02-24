using System;
using System.Collections.Generic;

namespace BooruManager.Models;

public class AppSettings
{
    public Dictionary<BooruSite, BooruCredentials> CredentialsBySite { get; set; } = new();
    public List<string> RecentSearches { get; set; } = new();
    public List<string> FavoritePostKeys { get; set; } = new();
    public List<ImagePost> FavoritePosts { get; set; } = new();
    public List<SyncedFavoriteProfile> SyncedFavoriteProfiles { get; set; } = new();
    public List<string> SelectedFavoriteOwnerKeys { get; set; } = new();
    public List<string> FavoriteSourceFilterKeys { get; set; } = new();
    public string FavoriteTagFilterText { get; set; } = string.Empty;
    public DateTime? LastFavoritesSyncUtc { get; set; }
    public int ResultsPerPage { get; set; } = 40;
    public string SearchSortKey { get; set; } = "date_desc";
    public bool ShowFavoritesOnly { get; set; }
    public int MinimumScore { get; set; }
    public int MinimumWidth { get; set; }
    public int MinimumHeight { get; set; }
    public string RequiredTags { get; set; } = string.Empty;
    public string ExcludedTags { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public List<string> BlacklistedPostKeys { get; set; } = new();
    public int SlideshowIntervalSeconds { get; set; } = 3;

    public string DownloadFolder { get; set; } = string.Empty;
    public bool DownloadCreateSubfolders { get; set; } = true;
    public bool DownloadPreserveOriginalFilenames { get; set; }
    public int MaxConcurrentDownloads { get; set; } = 3;
    public bool AutoStartSlideshow { get; set; }
    public bool ShowGridLines { get; set; } = true;
    public int CardWidth { get; set; } = 340;
    public int CardHeight { get; set; } = 400;
    public bool ConfirmBeforeDownload { get; set; } = true;
    public bool ShowDownloadNotifications { get; set; } = true;
    public string CustomUserAgent { get; set; } = string.Empty;
    public int RequestTimeoutSeconds { get; set; } = 30;

    public List<PostCollection> Collections { get; set; } = new();
    public List<PostNote> PostNotes { get; set; } = new();
    public List<TagBlacklistEntry> TagBlacklist { get; set; } = new();
    public List<ViewedPost> ViewedPosts { get; set; } = new();
    public int ThumbnailSize { get; set; } = 100;
    public bool ShowTagStatistics { get; set; } = true;
    public bool DetectDuplicates { get; set; } = true;
    public bool TrackViewedPosts { get; set; } = true;
    public int MaxViewedPostsHistory { get; set; } = 1000;
}
