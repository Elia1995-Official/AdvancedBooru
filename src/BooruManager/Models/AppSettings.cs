using System.Collections.Generic;

namespace BooruManager.Models;

public class AppSettings
{
    public Dictionary<BooruSite, BooruCredentials> CredentialsBySite { get; set; } = new();
    public List<string> RecentSearches { get; set; } = new();
    public List<string> FavoritePostKeys { get; set; } = new();
    public List<ImagePost> FavoritePosts { get; set; } = new();
    public int ResultsPerPage { get; set; } = 40;
    public string SearchSortKey { get; set; } = "date_desc";
    public bool ShowFavoritesOnly { get; set; }
    public int MinimumScore { get; set; }
    public int MinimumWidth { get; set; }
    public int MinimumHeight { get; set; }
    public string RequiredTags { get; set; } = string.Empty;
    public string ExcludedTags { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
}
