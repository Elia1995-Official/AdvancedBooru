using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using BooruManager.Models;

namespace BooruManager.Services;

public class BooruApiService
{
    private const int GelbooruHtmlPostsPerPageEstimate = 28;
    private const string BrowserLikeUserAgent =
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36 BooruManager/1.0";

    private readonly HttpClient _httpClient = new();

    public BooruApiService()
    {
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("BooruManager/1.0 (by chatgpt-codex)");
    }

    public async Task<IReadOnlyList<ImagePost>> SearchAsync(
        BooruSite site,
        string tags,
        int page,
        int pageSize,
        bool allowSafe,
        bool allowQuestionable,
        bool allowAdult,
        BooruCredentials? credentials,
        CancellationToken cancellationToken = default)
    {
        var results = site switch
        {
            BooruSite.Safebooru => await SearchGelbooruLikeAsync("https://safebooru.org", "Safebooru", tags, page, pageSize, credentials, false, cancellationToken),
            BooruSite.E621 => await SearchE621Async(tags, page, pageSize, credentials, cancellationToken),
            BooruSite.Danbooru => await SearchDanbooruAsync(tags, page, pageSize, credentials, cancellationToken),
            BooruSite.Gelbooru => await SearchGelbooruLikeAsync("https://gelbooru.com", "Gelbooru", tags, page, pageSize, credentials, true, cancellationToken),
            BooruSite.XBooru => await SearchGelbooruLikeAsync("https://xbooru.com", "XBooru", tags, page, pageSize, credentials, false, cancellationToken),
            BooruSite.TabBooru => await SearchGelbooruLikeAsync("https://tab.booru.org", "tab.booru.org", tags, page, pageSize, credentials, false, cancellationToken),
            BooruSite.AllGirlBooru => await SearchGelbooruLikeAsync("https://allgirl.booru.org", "allgirl.booru.org", tags, page, pageSize, credentials, false, cancellationToken),
            BooruSite.TheCollectionBooru => await SearchGelbooruLikeAsync("https://the-collection.booru.org", "the-collection.booru.org", tags, page, pageSize, credentials, false, cancellationToken),
            _ => Array.Empty<ImagePost>()
        };

        return results
            .Where(p => site == BooruSite.Safebooru || MatchesRating(p.Rating, allowSafe, allowQuestionable, allowAdult))
            .ToList();
    }

    public async Task<ImagePost?> GetPostByIdAsync(
        BooruSite site,
        string postId,
        BooruCredentials? credentials,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(postId))
        {
            return null;
        }

        try
        {
            return site switch
            {
                BooruSite.Safebooru => await GetGelbooruLikePostByIdAsync("https://safebooru.org", "Safebooru", postId, credentials, false, cancellationToken),
                BooruSite.E621 => await GetE621PostByIdAsync(postId, credentials, cancellationToken),
                BooruSite.Danbooru => await GetDanbooruPostByIdAsync(postId, credentials, cancellationToken),
                BooruSite.Gelbooru => await GetGelbooruLikePostByIdAsync("https://gelbooru.com", "Gelbooru", postId, credentials, true, cancellationToken),
                BooruSite.XBooru => await GetGelbooruLikePostByIdAsync("https://xbooru.com", "XBooru", postId, credentials, false, cancellationToken),
                BooruSite.TabBooru => await GetGelbooruLikePostByIdAsync("https://tab.booru.org", "tab.booru.org", postId, credentials, false, cancellationToken),
                BooruSite.AllGirlBooru => await GetGelbooruLikePostByIdAsync("https://allgirl.booru.org", "allgirl.booru.org", postId, credentials, false, cancellationToken),
                BooruSite.TheCollectionBooru => await GetGelbooruLikePostByIdAsync("https://the-collection.booru.org", "the-collection.booru.org", postId, credentials, false, cancellationToken),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task<ImagePost?> GetE621PostByIdAsync(
        string postId,
        BooruCredentials? credentials,
        CancellationToken cancellationToken)
    {
        var url = $"https://e621.net/posts/{Uri.EscapeDataString(postId.Trim())}.json";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddBasicAuthIfAvailable(request, credentials);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!json.RootElement.TryGetProperty("post", out var post) || post.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = post.GetProperty("id").ToString();
        var preview = GetNestedString(post, "sample", "url") ?? GetNestedString(post, "preview", "url") ?? string.Empty;
        var full = GetNestedString(post, "file", "url") ?? preview;
        var rating = GetString(post, "rating") ?? string.Empty;
        var score = GetNestedInt(post, "score", "total");
        var createdAtUnix = GetUnixTime(post, "created_at");
        var tagGroups = ExtractE621TagGroups(post);
        var tagsText = GetPreferredTagsForDisplay(tagGroups, ExtractE621Tags(post));
        var width = GetNestedInt(post, "file", "width");
        if (width <= 0)
        {
            width = GetNestedInt(post, "sample", "width");
        }

        if (width <= 0)
        {
            width = GetNestedInt(post, "preview", "width");
        }

        var height = GetNestedInt(post, "file", "height");
        if (height <= 0)
        {
            height = GetNestedInt(post, "sample", "height");
        }

        if (height <= 0)
        {
            height = GetNestedInt(post, "preview", "height");
        }

        if (string.IsNullOrWhiteSpace(preview) || string.IsNullOrWhiteSpace(full))
        {
            return null;
        }

        return new ImagePost
        {
            Id = id,
            SourceSite = "e621",
            PreviewUrl = preview,
            FullImageUrl = full,
            PostUrl = $"https://e621.net/posts/{id}",
            Rating = rating,
            Tags = tagsText,
            Score = score,
            CreatedAtUnix = createdAtUnix,
            TagGroups = CloneTagGroups(tagGroups),
            Width = width,
            Height = height
        };
    }

    private async Task<ImagePost?> GetDanbooruPostByIdAsync(
        string postId,
        BooruCredentials? credentials,
        CancellationToken cancellationToken)
    {
        var url = $"https://danbooru.donmai.us/posts/{Uri.EscapeDataString(postId.Trim())}.json";

        if (credentials is { } cred && !string.IsNullOrWhiteSpace(cred.Username) && !string.IsNullOrWhiteSpace(cred.Secret))
        {
            url += $"?login={Uri.EscapeDataString(cred.Username)}&api_key={Uri.EscapeDataString(cred.Secret)}";
        }

        using var response = await _httpClient.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (json.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var post = json.RootElement;
        var id = post.GetProperty("id").ToString();
        var preview = GetString(post, "preview_file_url") ?? string.Empty;
        var full = GetString(post, "file_url") ?? GetString(post, "large_file_url") ?? preview;
        var rating = GetString(post, "rating") ?? string.Empty;
        var score = GetInt(post, "score");
        var createdAtUnix = GetUnixTime(post, "created_at");
        var tagGroups = ExtractDanbooruTagGroups(post);
        var tagsText = GetPreferredTagsForDisplay(tagGroups, GetString(post, "tag_string") ?? string.Empty);
        var width = GetInt(post, "image_width");
        if (width <= 0)
        {
            width = GetInt(post, "width");
        }

        var height = GetInt(post, "image_height");
        if (height <= 0)
        {
            height = GetInt(post, "height");
        }

        preview = MakeAbsoluteDanbooruUrl(preview);
        full = MakeAbsoluteDanbooruUrl(full);

        if (string.IsNullOrWhiteSpace(preview) || string.IsNullOrWhiteSpace(full))
        {
            return null;
        }

        return new ImagePost
        {
            Id = id,
            SourceSite = "Danbooru",
            PreviewUrl = preview,
            FullImageUrl = full,
            PostUrl = $"https://danbooru.donmai.us/posts/{id}",
            Rating = rating,
            Tags = tagsText,
            Score = score,
            CreatedAtUnix = createdAtUnix,
            TagGroups = CloneTagGroups(tagGroups),
            Width = width,
            Height = height
        };
    }

    private async Task<ImagePost?> GetGelbooruLikePostByIdAsync(
        string baseUrl,
        string sourceName,
        string postId,
        BooruCredentials? credentials,
        bool supportsApiKeyAuth,
        CancellationToken cancellationToken)
    {
        var urlBuilder = new StringBuilder($"{baseUrl}/index.php?page=dapi&s=post&q=index&id={Uri.EscapeDataString(postId.Trim())}");

        if (supportsApiKeyAuth
            && credentials is { } cred
            && !string.IsNullOrWhiteSpace(cred.Username)
            && !string.IsNullOrWhiteSpace(cred.Secret))
        {
            urlBuilder.Append("&user_id=").Append(Uri.EscapeDataString(cred.Username));
            urlBuilder.Append("&api_key=").Append(Uri.EscapeDataString(cred.Secret));
        }

        string xmlString;
        try
        {
            xmlString = await GetStringWithSiteHeadersAsync(
                urlBuilder.ToString(),
                RequiresBrowserLikeUserAgent(baseUrl),
                cancellationToken);
        }
        catch (HttpRequestException ex) when (ShouldUseGelbooruHtmlFallback(baseUrl, ex.StatusCode))
        {
            return await GetGelbooruPostByIdFromHtmlAsync(baseUrl, sourceName, postId, cancellationToken);
        }
        catch (HttpRequestException) when (IsGelbooruBaseUrl(baseUrl))
        {
            return await GetGelbooruPostByIdFromHtmlAsync(baseUrl, sourceName, postId, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(xmlString))
        {
            return IsGelbooruBaseUrl(baseUrl)
                ? await GetGelbooruPostByIdFromHtmlAsync(baseUrl, sourceName, postId, cancellationToken)
                : null;
        }

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xmlString);
        }
        catch (Exception) when (IsGelbooruBaseUrl(baseUrl))
        {
            return await GetGelbooruPostByIdFromHtmlAsync(baseUrl, sourceName, postId, cancellationToken);
        }

        var post = doc.Descendants("post").FirstOrDefault();
        if (post is null)
        {
            return IsGelbooruBaseUrl(baseUrl)
                ? await GetGelbooruPostByIdFromHtmlAsync(baseUrl, sourceName, postId, cancellationToken)
                : null;
        }

        var id = post.Attribute("id")?.Value ?? string.Empty;
        var previewRaw = post.Attribute("preview_url")?.Value
            ?? post.Attribute("sample_url")?.Value
            ?? post.Attribute("file_url")?.Value
            ?? string.Empty;
        var fullRaw = post.Attribute("file_url")?.Value
            ?? post.Attribute("sample_url")?.Value
            ?? previewRaw;
        var rating = post.Attribute("rating")?.Value ?? string.Empty;
        var tagsText = post.Attribute("tags")?.Value ?? string.Empty;
        var score = ParseInt(post.Attribute("score")?.Value);
        var createdAtUnix = ParseUnixTime(post.Attribute("created_at")?.Value);
        var tagGroups = BuildSingleTagGroup(tagsText);
        var width = ParsePositiveInt(post.Attribute("width")?.Value);
        if (width <= 0)
        {
            width = ParsePositiveInt(post.Attribute("sample_width")?.Value);
        }

        var height = ParsePositiveInt(post.Attribute("height")?.Value);
        if (height <= 0)
        {
            height = ParsePositiveInt(post.Attribute("sample_height")?.Value);
        }

        var preview = MakeAbsoluteGelbooruLikeUrl(baseUrl, previewRaw);
        var full = MakeAbsoluteGelbooruLikeUrl(baseUrl, fullRaw);

        if (IsThumbsSubdomainSite(baseUrl))
        {
            full = FixThumbsSubdomainUrl(full);
            preview = FixThumbsSubdomainUrl(preview);
        }

        if (string.IsNullOrWhiteSpace(preview) || string.IsNullOrWhiteSpace(full))
        {
            return IsGelbooruBaseUrl(baseUrl)
                ? await GetGelbooruPostByIdFromHtmlAsync(baseUrl, sourceName, postId, cancellationToken)
                : null;
        }

        if (string.Equals(preview, full, StringComparison.OrdinalIgnoreCase) || IsLikelySampleOrPreviewMediaUrl(full))
        {
            var htmlResolved = await GetGelbooruPostByIdFromHtmlAsync(baseUrl, sourceName, postId, cancellationToken);
            if (htmlResolved is not null
                && !string.IsNullOrWhiteSpace(htmlResolved.FullImageUrl)
                && !string.Equals(htmlResolved.FullImageUrl, htmlResolved.PreviewUrl, StringComparison.OrdinalIgnoreCase)
                && !IsLikelySampleOrPreviewMediaUrl(htmlResolved.FullImageUrl))
            {
                return htmlResolved;
            }
        }

        return new ImagePost
        {
            Id = id,
            SourceSite = sourceName,
            PreviewUrl = preview,
            FullImageUrl = full,
            PostUrl = $"{baseUrl}/index.php?page=post&s=view&id={id}",
            Rating = rating,
            Tags = tagsText,
            Score = score,
            CreatedAtUnix = createdAtUnix,
            TagGroups = CloneTagGroups(tagGroups),
            Width = width,
            Height = height
        };
    }

    private async Task<IReadOnlyList<ImagePost>> SearchE621Async(
        string tags,
        int page,
        int pageSize,
        BooruCredentials? credentials,
        CancellationToken cancellationToken)
    {
        var encodedTags = Uri.EscapeDataString(tags.Trim());
        var url = $"https://e621.net/posts.json?limit={pageSize}&page={page}&tags={encodedTags}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddBasicAuthIfAvailable(request, credentials);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var list = new List<ImagePost>();
        if (!json.RootElement.TryGetProperty("posts", out var posts) || posts.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var post in posts.EnumerateArray())
        {
            var id = post.GetProperty("id").ToString();
            var preview = GetNestedString(post, "sample", "url") ?? GetNestedString(post, "preview", "url") ?? string.Empty;
            var full = GetNestedString(post, "file", "url") ?? preview;
            var rating = GetString(post, "rating") ?? string.Empty;
            var score = GetNestedInt(post, "score", "total");
            var createdAtUnix = GetUnixTime(post, "created_at");
            var tagGroups = ExtractE621TagGroups(post);
            var tagsText = GetPreferredTagsForDisplay(tagGroups, ExtractE621Tags(post));
            var width = GetNestedInt(post, "file", "width");
            if (width <= 0)
            {
                width = GetNestedInt(post, "sample", "width");
            }

            if (width <= 0)
            {
                width = GetNestedInt(post, "preview", "width");
            }

            var height = GetNestedInt(post, "file", "height");
            if (height <= 0)
            {
                height = GetNestedInt(post, "sample", "height");
            }

            if (height <= 0)
            {
                height = GetNestedInt(post, "preview", "height");
            }

            if (string.IsNullOrWhiteSpace(preview) || string.IsNullOrWhiteSpace(full))
            {
                continue;
            }

            list.Add(new ImagePost
            {
                Id = id,
                SourceSite = "e621",
                PreviewUrl = preview,
                FullImageUrl = full,
                PostUrl = $"https://e621.net/posts/{id}",
                Rating = rating,
                Tags = tagsText,
                Score = score,
                CreatedAtUnix = createdAtUnix,
                TagGroups = CloneTagGroups(tagGroups),
                Width = width,
                Height = height
            });
        }

        return list;
    }

    private async Task<IReadOnlyList<ImagePost>> SearchDanbooruAsync(
        string tags,
        int page,
        int pageSize,
        BooruCredentials? credentials,
        CancellationToken cancellationToken)
    {
        var encodedTags = Uri.EscapeDataString(tags.Trim());
        var url = $"https://danbooru.donmai.us/posts.json?limit={pageSize}&page={page}&tags={encodedTags}";

        if (credentials is { } cred && !string.IsNullOrWhiteSpace(cred.Username) && !string.IsNullOrWhiteSpace(cred.Secret))
        {
            url += $"&login={Uri.EscapeDataString(cred.Username)}&api_key={Uri.EscapeDataString(cred.Secret)}";
        }

        await using var stream = await _httpClient.GetStreamAsync(url, cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var list = new List<ImagePost>();
        if (json.RootElement.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var post in json.RootElement.EnumerateArray())
        {
            var id = post.GetProperty("id").ToString();
            var preview = GetString(post, "preview_file_url") ?? string.Empty;
            var full = GetString(post, "file_url") ?? GetString(post, "large_file_url") ?? preview;
            var rating = GetString(post, "rating") ?? string.Empty;
            var score = GetInt(post, "score");
            var createdAtUnix = GetUnixTime(post, "created_at");
            var tagGroups = ExtractDanbooruTagGroups(post);
            var tagsText = GetPreferredTagsForDisplay(tagGroups, GetString(post, "tag_string") ?? string.Empty);
            var width = GetInt(post, "image_width");
            if (width <= 0)
            {
                width = GetInt(post, "width");
            }

            var height = GetInt(post, "image_height");
            if (height <= 0)
            {
                height = GetInt(post, "height");
            }

            preview = MakeAbsoluteDanbooruUrl(preview);
            full = MakeAbsoluteDanbooruUrl(full);

            if (string.IsNullOrWhiteSpace(preview) || string.IsNullOrWhiteSpace(full))
            {
                continue;
            }

            list.Add(new ImagePost
            {
                Id = id,
                SourceSite = "Danbooru",
                PreviewUrl = preview,
                FullImageUrl = full,
                PostUrl = $"https://danbooru.donmai.us/posts/{id}",
                Rating = rating,
                Tags = tagsText,
                Score = score,
                CreatedAtUnix = createdAtUnix,
                TagGroups = CloneTagGroups(tagGroups),
                Width = width,
                Height = height
            });
        }

        return list;
    }

    private async Task<IReadOnlyList<ImagePost>> SearchGelbooruLikeAsync(
        string baseUrl,
        string sourceName,
        string tags,
        int page,
        int pageSize,
        BooruCredentials? credentials,
        bool supportsApiKeyAuth,
        CancellationToken cancellationToken)
    {
        var encodedTags = Uri.EscapeDataString(tags.Trim());
        var pid = Math.Max(0, page - 1);

        var urlBuilder = new StringBuilder($"{baseUrl}/index.php?page=dapi&s=post&q=index&limit={pageSize}&pid={pid}&tags={encodedTags}");

        if (supportsApiKeyAuth
            && credentials is { } cred
            && !string.IsNullOrWhiteSpace(cred.Username)
            && !string.IsNullOrWhiteSpace(cred.Secret))
        {
            urlBuilder.Append("&user_id=").Append(Uri.EscapeDataString(cred.Username));
            urlBuilder.Append("&api_key=").Append(Uri.EscapeDataString(cred.Secret));
        }

        string xmlString;
        try
        {
            xmlString = await GetStringWithSiteHeadersAsync(
                urlBuilder.ToString(),
                RequiresBrowserLikeUserAgent(baseUrl),
                cancellationToken);
        }
        catch (HttpRequestException ex) when (ShouldUseGelbooruHtmlFallback(baseUrl, ex.StatusCode))
        {
            return await SearchGelbooruHtmlFallbackAsync(baseUrl, sourceName, tags, page, pageSize, cancellationToken);
        }
        catch (HttpRequestException) when (IsGelbooruBaseUrl(baseUrl))
        {
            return await SearchGelbooruHtmlFallbackAsync(baseUrl, sourceName, tags, page, pageSize, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(xmlString))
        {
            return IsGelbooruBaseUrl(baseUrl)
                ? await SearchGelbooruHtmlFallbackAsync(baseUrl, sourceName, tags, page, pageSize, cancellationToken)
                : Array.Empty<ImagePost>();
        }

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xmlString);
        }
        catch (Exception) when (IsGelbooruBaseUrl(baseUrl))
        {
            return await SearchGelbooruHtmlFallbackAsync(baseUrl, sourceName, tags, page, pageSize, cancellationToken);
        }

        var posts = doc.Descendants("post").ToList();
        if (posts.Count == 0 && IsGelbooruBaseUrl(baseUrl))
        {
            return await SearchGelbooruHtmlFallbackAsync(baseUrl, sourceName, tags, page, pageSize, cancellationToken);
        }

        var list = new List<ImagePost>();
        foreach (var post in posts)
        {
            var id = post.Attribute("id")?.Value ?? string.Empty;
            var previewRaw = post.Attribute("preview_url")?.Value
                ?? post.Attribute("sample_url")?.Value
                ?? post.Attribute("file_url")?.Value
                ?? string.Empty;
            var fullRaw = post.Attribute("file_url")?.Value
                ?? post.Attribute("sample_url")?.Value
                ?? previewRaw;
            var rating = post.Attribute("rating")?.Value ?? string.Empty;
            var tagsText = post.Attribute("tags")?.Value ?? string.Empty;
            var score = ParseInt(post.Attribute("score")?.Value);
            var createdAtUnix = ParseUnixTime(post.Attribute("created_at")?.Value);
            var tagGroups = BuildSingleTagGroup(tagsText);
            var width = ParsePositiveInt(post.Attribute("width")?.Value);
            if (width <= 0)
            {
                width = ParsePositiveInt(post.Attribute("sample_width")?.Value);
            }

            var height = ParsePositiveInt(post.Attribute("height")?.Value);
            if (height <= 0)
            {
                height = ParsePositiveInt(post.Attribute("sample_height")?.Value);
            }

            var preview = MakeAbsoluteGelbooruLikeUrl(baseUrl, previewRaw);
            var full = MakeAbsoluteGelbooruLikeUrl(baseUrl, fullRaw);

            if (IsThumbsSubdomainSite(baseUrl))
            {
                full = FixThumbsSubdomainUrl(full);
                preview = FixThumbsSubdomainUrl(preview);
            }

            if (string.IsNullOrWhiteSpace(preview) || string.IsNullOrWhiteSpace(full))
            {
                continue;
            }

            list.Add(new ImagePost
            {
                Id = id,
                SourceSite = sourceName,
                PreviewUrl = preview,
                FullImageUrl = full,
                PostUrl = $"{baseUrl}/index.php?page=post&s=view&id={id}",
                Rating = rating,
                Tags = tagsText,
                Score = score,
                CreatedAtUnix = createdAtUnix,
                TagGroups = CloneTagGroups(tagGroups),
                Width = width,
                Height = height
            });
        }

        return list;
    }

    private static bool IsGelbooruBaseUrl(string baseUrl)
    {
        return baseUrl.Contains("gelbooru.com", StringComparison.OrdinalIgnoreCase)
            || baseUrl.Contains(".booru.org", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsThumbsSubdomainSite(string baseUrl)
    {
        return baseUrl.Contains("allgirl.booru.org", StringComparison.OrdinalIgnoreCase)
            || baseUrl.Contains("the-collection.booru.org", StringComparison.OrdinalIgnoreCase)
            || baseUrl.Contains("tab.booru.org", StringComparison.OrdinalIgnoreCase);
    }

    private static string FixThumbsSubdomainUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return url;
        }

        if (url.Contains("://thumbs.", StringComparison.OrdinalIgnoreCase))
        {
            return url.Replace("://thumbs.", "://img.", StringComparison.OrdinalIgnoreCase);
        }

        return url;
    }

    private static bool ShouldUseGelbooruHtmlFallback(string baseUrl, HttpStatusCode? statusCode)
    {
        if (!IsGelbooruBaseUrl(baseUrl))
        {
            return false;
        }

        if (!statusCode.HasValue)
        {
            return true;
        }

        var code = (int)statusCode.Value;
        return code is 401 or 403 or 429 or 500 or 502 or 503 or 504 or 520 or 521 or 522 or 523 or 524 or 525 or 526;
    }

    private async Task<IReadOnlyList<ImagePost>> SearchGelbooruHtmlFallbackAsync(
        string baseUrl,
        string sourceName,
        string tags,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var posts = new List<ImagePost>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var useBrowserLikeUserAgent = RequiresBrowserLikeUserAgent(baseUrl);

        var pagesPerRequest = Math.Max(1, (int)Math.Ceiling(pageSize / (double)GelbooruHtmlPostsPerPageEstimate));
        var startPid = Math.Max(0, (page - 1) * pagesPerRequest);

        for (var i = 0; i < pagesPerRequest; i++)
        {
            var pid = startPid + i;
            var listUrlBuilder = new StringBuilder($"{baseUrl}/index.php?page=post&s=list&pid={pid}");
            if (!string.IsNullOrWhiteSpace(tags))
            {
                listUrlBuilder.Append("&tags=").Append(Uri.EscapeDataString(tags.Trim()));
            }

            string html;
            try
            {
                html = await GetStringWithSiteHeadersAsync(listUrlBuilder.ToString(), useBrowserLikeUserAgent, cancellationToken);
            }
            catch (HttpRequestException ex) when (ShouldUseGelbooruHtmlFallback(baseUrl, ex.StatusCode))
            {
                break;
            }
            catch (HttpRequestException) when (IsGelbooruBaseUrl(baseUrl))
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(html))
            {
                break;
            }

            var parsedPage = ParseGelbooruHtmlListPage(html, baseUrl, sourceName);
            if (parsedPage.Count == 0)
            {
                break;
            }

            foreach (var post in parsedPage)
            {
                if (!seenIds.Add(post.Id))
                {
                    continue;
                }

                posts.Add(post);
                if (posts.Count >= pageSize)
                {
                    return posts;
                }
            }
        }

        return posts;
    }

    private static List<ImagePost> ParseGelbooruHtmlListPage(string html, string baseUrl, string sourceName)
    {
        var list = new List<ImagePost>();
        if (string.IsNullOrWhiteSpace(html))
        {
            return list;
        }

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var modernRegex = new Regex(
            "<article\\s+class=\"thumbnail-preview\".*?<a\\s+id=\"p(?<id>\\d+)\"\\s+href=\"(?<href>[^\"]+)\".*?<img[^>]*src=\"(?<src>[^\"]+)\"[^>]*title=\"(?<title>[^\"]*)\"",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        foreach (Match match in modernRegex.Matches(html))
        {
            var id = match.Groups["id"].Value.Trim();
            var hrefRaw = match.Groups["href"].Value;
            var previewRaw = match.Groups["src"].Value;
            var titleRaw = match.Groups["title"].Value;

            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(hrefRaw) || string.IsNullOrWhiteSpace(previewRaw))
            {
                continue;
            }

            if (!seenIds.Add(id))
            {
                continue;
            }

            var postUrl = MakeAbsoluteGelbooruLikeUrl(baseUrl, WebUtility.HtmlDecode(hrefRaw));
            var preview = MakeAbsoluteGelbooruLikeUrl(baseUrl, WebUtility.HtmlDecode(previewRaw));
            if (string.IsNullOrWhiteSpace(postUrl) || string.IsNullOrWhiteSpace(preview))
            {
                continue;
            }

            var (tags, rating, score) = ParseGelbooruListTitle(WebUtility.HtmlDecode(titleRaw));
            var tagGroups = BuildSingleTagGroup(tags);
            list.Add(new ImagePost
            {
                Id = id,
                SourceSite = sourceName,
                PreviewUrl = preview,
                FullImageUrl = preview,
                PostUrl = postUrl,
                Rating = rating,
                Tags = tags,
                Score = score,
                TagGroups = CloneTagGroups(tagGroups)
            });
        }

        var legacyRegex = new Regex(
            "<span\\s+class=\"thumb\">\\s*<a\\s+id=\"p(?<id>\\d+)\"\\s+href=\"(?<href>[^\"]+)\"[^>]*>\\s*<img[^>]*src=\"(?<src>[^\"]+)\"[^>]*title=\"(?<title>[^\"]*)\"",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        foreach (Match match in legacyRegex.Matches(html))
        {
            var id = match.Groups["id"].Value.Trim();
            var hrefRaw = match.Groups["href"].Value;
            var previewRaw = match.Groups["src"].Value;
            var titleRaw = match.Groups["title"].Value;

            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(hrefRaw) || string.IsNullOrWhiteSpace(previewRaw))
            {
                continue;
            }

            if (!seenIds.Add(id))
            {
                continue;
            }

            var postUrl = MakeAbsoluteGelbooruLikeUrl(baseUrl, WebUtility.HtmlDecode(hrefRaw));
            var preview = MakeAbsoluteGelbooruLikeUrl(baseUrl, WebUtility.HtmlDecode(previewRaw));
            if (string.IsNullOrWhiteSpace(postUrl) || string.IsNullOrWhiteSpace(preview))
            {
                continue;
            }

            var (tags, rating, score) = ParseGelbooruListTitle(WebUtility.HtmlDecode(titleRaw));
            var tagGroups = BuildSingleTagGroup(tags);
            list.Add(new ImagePost
            {
                Id = id,
                SourceSite = sourceName,
                PreviewUrl = preview,
                FullImageUrl = preview,
                PostUrl = postUrl,
                Rating = rating,
                Tags = tags,
                Score = score,
                TagGroups = CloneTagGroups(tagGroups)
            });
        }

        return list;
    }

    private static (string Tags, string Rating, int Score) ParseGelbooruListTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return (string.Empty, string.Empty, 0);
        }

        var tags = new List<string>();
        var rating = string.Empty;
        var score = 0;

        foreach (var token in title.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith("score:", StringComparison.OrdinalIgnoreCase))
            {
                score = ParseInt(token["score:".Length..]);
                continue;
            }

            if (token.StartsWith("rating:", StringComparison.OrdinalIgnoreCase))
            {
                rating = token["rating:".Length..];
                continue;
            }

            tags.Add(token);
        }

        return (string.Join(' ', tags), rating, score);
    }

    private async Task<ImagePost?> GetGelbooruPostByIdFromHtmlAsync(
        string baseUrl,
        string sourceName,
        string postId,
        CancellationToken cancellationToken)
    {
        var id = postId.Trim();
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var postUrl = $"{baseUrl}/index.php?page=post&s=view&id={Uri.EscapeDataString(id)}";
        string html;
        try
        {
            html = await GetStringWithSiteHeadersAsync(postUrl, RequiresBrowserLikeUserAgent(baseUrl), cancellationToken);
        }
        catch (HttpRequestException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var originalMatch = Regex.Match(
            html,
            "<a\\b[^>]*href=[\"'](?<url>[^\"']+)[\"'][^>]*>\\s*Original(?:\\s+image)?\\s*</a>",
            RegexOptions.IgnoreCase);

        var imageMatch = Regex.Match(
            html,
            "<img\\b(?=[^>]*\\bid=[\"']image[\"'])(?=[^>]*\\bsrc=[\"'](?<url>[^\"']+)[\"'])[^>]*>",
            RegexOptions.IgnoreCase);

        var previewMetaMatch = Regex.Match(
            html,
            "<meta\\s+property=\"og:image\"\\s+content=\"(?<url>[^\"]+)\"",
            RegexOptions.IgnoreCase);

        var fullRaw = string.Empty;

        if (IsThumbsSubdomainSite(baseUrl))
        {
            var imgBooruMatch = Regex.Match(
                html,
                "https://img\\.booru\\.org/[^/]+//images/[^/]+/[^.\"']+\\.(jpg|jpeg|png|gif|webp)",
                RegexOptions.IgnoreCase);
            if (imgBooruMatch.Success)
            {
                fullRaw = imgBooruMatch.Value;
            }
        }

        if (string.IsNullOrEmpty(fullRaw))
        {
            fullRaw = originalMatch.Success
                ? originalMatch.Groups["url"].Value
                : imageMatch.Groups["url"].Value;
        }

        var previewRaw = previewMetaMatch.Success
            ? previewMetaMatch.Groups["url"].Value
            : fullRaw;

        var full = MakeAbsoluteGelbooruLikeUrl(baseUrl, WebUtility.HtmlDecode(fullRaw));
        var preview = MakeAbsoluteGelbooruLikeUrl(baseUrl, WebUtility.HtmlDecode(previewRaw));

        if (IsThumbsSubdomainSite(baseUrl))
        {
            full = FixThumbsSubdomainUrl(full);
            preview = FixThumbsSubdomainUrl(preview);
        }

        if (string.IsNullOrWhiteSpace(full))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(preview))
        {
            preview = full;
        }

        var tagsMatch = Regex.Match(html, "data-tags=\"(?<tags>[^\"]*)\"", RegexOptions.IgnoreCase);
        if (!tagsMatch.Success)
        {
            tagsMatch = Regex.Match(
                html,
                "<textarea[^>]*id=\"tags\"[^>]*>(?<tags>.*?)</textarea>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
        }

        var ratingMatch = Regex.Match(html, "data-rating=\"(?<rating>[^\"]*)\"", RegexOptions.IgnoreCase);
        if (!ratingMatch.Success)
        {
            ratingMatch = Regex.Match(html, "Rating:\\s*(?<rating>[A-Za-z]+)", RegexOptions.IgnoreCase);
        }

        var widthMatch = Regex.Match(html, "data-width=\"(?<width>\\d+)\"", RegexOptions.IgnoreCase);
        var heightMatch = Regex.Match(html, "data-height=\"(?<height>\\d+)\"", RegexOptions.IgnoreCase);
        if (!widthMatch.Success || !heightMatch.Success)
        {
            var sizeMatch = Regex.Match(html, "Size:\\s*(?<width>\\d+)x(?<height>\\d+)", RegexOptions.IgnoreCase);
            if (sizeMatch.Success)
            {
                widthMatch = sizeMatch;
                heightMatch = sizeMatch;
            }
        }

        var scoreMatch = Regex.Match(html, "Score:\\s*(?<score>-?\\d+)", RegexOptions.IgnoreCase);
        var postedMatch = Regex.Match(html, "Posted:\\s*(?<posted>[0-9:\\-\\s]+)", RegexOptions.IgnoreCase);

        var tags = tagsMatch.Success ? WebUtility.HtmlDecode(tagsMatch.Groups["tags"].Value).Trim() : string.Empty;
        var tagGroups = BuildSingleTagGroup(tags);
        var rating = ratingMatch.Success ? WebUtility.HtmlDecode(ratingMatch.Groups["rating"].Value).Trim() : string.Empty;
        var width = ParsePositiveInt(widthMatch.Success ? widthMatch.Groups["width"].Value : null);
        var height = ParsePositiveInt(heightMatch.Success ? heightMatch.Groups["height"].Value : null);
        var score = ParseInt(scoreMatch.Success ? scoreMatch.Groups["score"].Value : null);
        var createdAtUnix = ParseUnixTime(postedMatch.Success ? postedMatch.Groups["posted"].Value : null);

        return new ImagePost
        {
            Id = id,
            SourceSite = sourceName,
            PreviewUrl = preview,
            FullImageUrl = full,
            PostUrl = postUrl,
            Rating = rating,
            Tags = tags,
            Score = score,
            CreatedAtUnix = createdAtUnix,
            TagGroups = CloneTagGroups(tagGroups),
            Width = width,
            Height = height
        };
    }

    public async Task<bool> ValidateCredentialsAsync(BooruSite site, BooruCredentials credentials, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(credentials.Username) || string.IsNullOrWhiteSpace(credentials.Secret))
        {
            return false;
        }

        try
        {
            return site switch
            {
                BooruSite.Safebooru => true,
                BooruSite.E621 => await ValidateE621Async(credentials, cancellationToken),
                BooruSite.Danbooru => await ValidateDanbooruAsync(credentials, cancellationToken),
                BooruSite.Gelbooru => true,
                BooruSite.XBooru => true,
                BooruSite.TabBooru => true,
                BooruSite.AllGirlBooru => true,
                BooruSite.TheCollectionBooru => true,
                _ => false
            };
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> ValidateE621Async(BooruCredentials credentials, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://e621.net/users.json?limit=1");
        AddBasicAuthIfAvailable(request, credentials);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    private async Task<bool> ValidateDanbooruAsync(BooruCredentials credentials, CancellationToken cancellationToken)
    {
        var url = $"https://danbooru.donmai.us/profile.json?login={Uri.EscapeDataString(credentials.Username)}&api_key={Uri.EscapeDataString(credentials.Secret)}";
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    private static void AddBasicAuthIfAvailable(HttpRequestMessage request, BooruCredentials? credentials)
    {
        if (credentials is null || string.IsNullOrWhiteSpace(credentials.Username) || string.IsNullOrWhiteSpace(credentials.Secret))
        {
            return;
        }

        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credentials.Username}:{credentials.Secret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
    }

    private static int GetInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var child))
        {
            return 0;
        }

        if (child.ValueKind == JsonValueKind.Number)
        {
            if (child.TryGetInt32(out var value))
            {
                return value;
            }

            if (child.TryGetInt64(out var longValue) && longValue > 0 && longValue <= int.MaxValue)
            {
                return (int)longValue;
            }

            return 0;
        }

        if (child.ValueKind == JsonValueKind.String && int.TryParse(child.GetString(), out var parsed))
        {
            return parsed;
        }

        return 0;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var child))
        {
            return null;
        }

        return child.ValueKind switch
        {
            JsonValueKind.String => child.GetString(),
            JsonValueKind.Number => child.ToString(),
            _ => null
        };
    }

    private static string? GetNestedString(JsonElement element, string first, string second)
    {
        if (!element.TryGetProperty(first, out var child) || child.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return GetString(child, second);
    }

    private static int GetNestedInt(JsonElement element, string first, string second)
    {
        if (!element.TryGetProperty(first, out var child) || child.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        return GetInt(child, second);
    }

    private static string ExtractE621Tags(JsonElement post)
    {
        if (!post.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        if (!tags.TryGetProperty("general", out var general) || general.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        return string.Join(' ', general.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x))!);
    }

    private static bool MatchesRating(string ratingRaw, bool allowSafe, bool allowQuestionable, bool allowAdult)
    {
        if (!allowSafe && !allowQuestionable && !allowAdult)
        {
            return true;
        }

        var rating = (ratingRaw ?? string.Empty).Trim().ToLowerInvariant();

        return rating switch
        {
            "s" or "safe" or "g" or "general" => allowSafe,
            "q" or "questionable" => allowQuestionable,
            "e" or "explicit" or "adult" => allowAdult,
            _ => true
        };
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

        var normalized = (path ?? string.Empty).Trim().ToLowerInvariant();
        return normalized.Contains("/samples/", StringComparison.Ordinal)
            || normalized.Contains("/sample/", StringComparison.Ordinal)
            || normalized.Contains("/thumbnails/", StringComparison.Ordinal)
            || normalized.Contains("/thumbnail/", StringComparison.Ordinal)
            || normalized.Contains("sample_", StringComparison.Ordinal)
            || normalized.Contains("thumbnail_", StringComparison.Ordinal);
    }

    private static bool RequiresBrowserLikeUserAgent(string baseUrl)
    {
        return baseUrl.Contains(".booru.org", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> GetStringWithSiteHeadersAsync(string url, bool useBrowserLikeUserAgent, CancellationToken cancellationToken)
    {
        if (!useBrowserLikeUserAgent)
        {
            return await _httpClient.GetStringAsync(url, cancellationToken);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(BrowserLikeUserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static string MakeAbsoluteDanbooruUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        return url.StartsWith("/", StringComparison.Ordinal)
            ? $"https://danbooru.donmai.us{url}"
            : url;
    }

    private static string MakeAbsoluteGelbooruLikeUrl(string baseUrl, string mediaUrl)
    {
        if (string.IsNullOrWhiteSpace(mediaUrl))
        {
            return string.Empty;
        }

        var normalized = mediaUrl.Trim();

        if (normalized.StartsWith("//", StringComparison.Ordinal))
        {
            return $"https:{normalized}";
        }

        if (Uri.TryCreate(normalized, UriKind.Absolute, out _))
        {
            return normalized;
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            return normalized;
        }

        if (Uri.TryCreate(baseUri, normalized, out var combined))
        {
            return combined.ToString();
        }

        return normalized;
    }

    private static int ParseInt(string? value)
    {
        return int.TryParse(value, out var parsed)
            ? parsed
            : 0;
    }

    private static long ParseUnixTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        if (long.TryParse(value, out var direct))
        {
            return direct;
        }

        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                out var parsedDate))
        {
            return parsedDate.ToUnixTimeSeconds();
        }

        return 0;
    }

    private static long GetUnixTime(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var child))
        {
            return 0;
        }

        if (child.ValueKind == JsonValueKind.Number)
        {
            if (child.TryGetInt64(out var asLong))
            {
                return asLong;
            }

            if (child.TryGetDouble(out var asDouble))
            {
                return (long)asDouble;
            }
        }

        if (child.ValueKind == JsonValueKind.String)
        {
            return ParseUnixTime(child.GetString());
        }

        if (child.ValueKind == JsonValueKind.Object)
        {
            if (child.TryGetProperty("s", out var secValue))
            {
                if (secValue.ValueKind == JsonValueKind.Number && secValue.TryGetInt64(out var secLong))
                {
                    return secLong;
                }

                if (secValue.ValueKind == JsonValueKind.String)
                {
                    return ParseUnixTime(secValue.GetString());
                }
            }
        }

        return 0;
    }

    private static int ParsePositiveInt(string? value)
    {
        return int.TryParse(value, out var parsed) && parsed > 0
            ? parsed
            : 0;
    }

    private static Dictionary<string, List<string>> ExtractE621TagGroups(JsonElement post)
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (!post.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object)
        {
            return groups;
        }

        foreach (var property in tags.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var values = property.Value
                .EnumerateArray()
                .Select(x => x.GetString())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (values.Count == 0)
            {
                continue;
            }

            groups[MapTagGroupName(property.Name)] = values;
        }

        return groups;
    }

    private static Dictionary<string, List<string>> ExtractDanbooruTagGroups(JsonElement post)
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        AddDanbooruTagGroup(post, groups, "tag_string_artist", "Artist");
        AddDanbooruTagGroup(post, groups, "tag_string_character", "Character");
        AddDanbooruTagGroup(post, groups, "tag_string_copyright", "Copyright");
        AddDanbooruTagGroup(post, groups, "tag_string_meta", "Meta");
        AddDanbooruTagGroup(post, groups, "tag_string_general", "General");
        return groups;
    }

    private static void AddDanbooruTagGroup(JsonElement post, Dictionary<string, List<string>> groups, string propertyName, string groupName)
    {
        var raw = GetString(post, propertyName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        var values = raw
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (values.Count > 0)
        {
            groups[groupName] = values;
        }
    }

    private static Dictionary<string, List<string>> BuildSingleTagGroup(string tags)
    {
        var values = (tags ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (values.Count > 0)
        {
            groups["General"] = values;
        }

        return groups;
    }

    private static string GetPreferredTagsForDisplay(Dictionary<string, List<string>> groups, string fallback)
    {
        if (groups.TryGetValue("General", out var general) && general.Count > 0)
        {
            return string.Join(' ', general);
        }

        return fallback ?? string.Empty;
    }

    private static string MapTagGroupName(string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return "General";
        }

        return rawName.Trim().ToLowerInvariant() switch
        {
            "artist" => "Artist",
            "character" => "Character",
            "copyright" => "Copyright",
            "species" => "Species",
            "general" => "General",
            "meta" => "Meta",
            "lore" => "Lore",
            "invalid" => "Invalid",
            _ => char.ToUpperInvariant(rawName.Trim()[0]) + rawName.Trim()[1..]
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
}
