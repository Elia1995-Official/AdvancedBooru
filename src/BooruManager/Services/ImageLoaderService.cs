using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace BooruManager.Services;

public class ImageLoaderService
{
    private static readonly HttpClient HttpClient = CreateClient();

    public async Task<Bitmap?> LoadBitmapAsync(
        string url,
        string? sourceSite = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var normalizedUrl = NormalizeUrl(url);
        var candidates = BuildCandidateUrls(normalizedUrl, sourceSite);

        foreach (var candidate in candidates)
        {
            var bitmap = await TryLoadFromUrlAsync(candidate, sourceSite, includeReferer: true, cancellationToken);
            if (bitmap is not null)
            {
                return bitmap;
            }

            bitmap = await TryLoadFromUrlAsync(candidate, sourceSite, includeReferer: false, cancellationToken);
            if (bitmap is not null)
            {
                return bitmap;
            }
        }

        return null;
    }

    private static async Task<Bitmap?> TryLoadFromUrlAsync(
        string url,
        string? sourceSite,
        bool includeReferer,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Accept", "image/avif,image/webp,image/apng,image/*,*/*;q=0.8");

            if (includeReferer)
            {
                var referer = ResolveReferer(url, sourceSite);
                if (referer is not null)
                {
                    request.Headers.Referrer = referer;
                    request.Headers.TryAddWithoutValidation("Origin", $"{referer.Scheme}://{referer.Host}");
                }
            }

            using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length == 0)
            {
                return null;
            }

            return new Bitmap(new MemoryStream(bytes));
        }
        catch
        {
            return null;
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BooruManager/1.0 (by chatgpt-codex)");
        return client;
    }

    private static IEnumerable<string> BuildCandidateUrls(string normalizedUrl, string? sourceSite)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string candidate)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                seen.Add(candidate);
            }
        }

        Add(normalizedUrl);

        if (!Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var absoluteUri))
        {
            var baseUrl = GetSiteBaseUrl(sourceSite);
            if (baseUrl is not null && Uri.TryCreate(new Uri(baseUrl), normalizedUrl, out var combined))
            {
                Add(combined.ToString());
            }

            return seen;
        }

        if (absoluteUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
            && absoluteUri.Host.Contains("xbooru.com", StringComparison.OrdinalIgnoreCase))
        {
            Add($"http://{absoluteUri.Host}{absoluteUri.PathAndQuery}");
        }

        return seen;
    }

    private static Uri? ResolveReferer(string imageUrl, string? sourceSite)
    {
        var siteBase = GetSiteBaseUrl(sourceSite);
        if (!string.IsNullOrWhiteSpace(siteBase) && Uri.TryCreate(siteBase, UriKind.Absolute, out var bySource))
        {
            return bySource;
        }

        if (Uri.TryCreate(imageUrl, UriKind.Absolute, out var imageUri))
        {
            if (imageUri.Host.Contains("xbooru.com", StringComparison.OrdinalIgnoreCase))
            {
                return new Uri("https://xbooru.com/");
            }

            if (imageUri.Host.Contains("gelbooru.com", StringComparison.OrdinalIgnoreCase))
            {
                return new Uri("https://gelbooru.com/");
            }

            if (imageUri.Host.Contains("tab.booru.org", StringComparison.OrdinalIgnoreCase))
            {
                return new Uri("https://tab.booru.org/");
            }

            if (imageUri.Host.Contains("allgirl.booru.org", StringComparison.OrdinalIgnoreCase))
            {
                return new Uri("https://allgirl.booru.org/");
            }

            if (imageUri.Host.Contains("the-collection.booru.org", StringComparison.OrdinalIgnoreCase))
            {
                return new Uri("https://the-collection.booru.org/");
            }

            if (imageUri.Host.Contains("e621.net", StringComparison.OrdinalIgnoreCase))
            {
                return new Uri("https://e621.net/");
            }

            if (imageUri.Host.Contains("donmai.us", StringComparison.OrdinalIgnoreCase))
            {
                return new Uri("https://danbooru.donmai.us/");
            }

            return new Uri($"{imageUri.Scheme}://{imageUri.Host}/");
        }

        return null;
    }

    private static string? GetSiteBaseUrl(string? sourceSite)
    {
        var normalized = (sourceSite ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "xbooru" => "https://xbooru.com/",
            "gelbooru" => "https://gelbooru.com/",
            "tabbooru" or "tab.booru.org" => "https://tab.booru.org/",
            "allgirlbooru" or "allgirl.booru.org" => "https://allgirl.booru.org/",
            "thecollectionbooru" or "the-collection.booru.org" => "https://the-collection.booru.org/",
            "e621" => "https://e621.net/",
            "danbooru" => "https://danbooru.donmai.us/",
            _ => null
        };
    }

    private static string NormalizeUrl(string url)
    {
        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            return $"https:{url}";
        }

        return url;
    }
}
