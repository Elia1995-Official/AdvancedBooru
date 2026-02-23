using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace BooruManager.Services;

public class DownloadService
{
    private readonly HttpClient _httpClient = new();

    public event Action<DownloadProgress>? ProgressChanged;

    public async Task<DownloadResult> DownloadPostAsync(
        string url,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? string.Empty);

            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            var canReportProgress = totalBytes > 0;

            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[8192];
            var totalBytesRead = 0L;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                totalBytesRead += bytesRead;

                if (canReportProgress)
                {
                    var progress = (double)totalBytesRead / totalBytes;
                    ProgressChanged?.Invoke(new DownloadProgress(url, progress, totalBytesRead, totalBytes));
                }
            }

            return new DownloadResult(true, destinationPath, null);
        }
        catch (OperationCanceledException)
        {
            return new DownloadResult(false, destinationPath, "Download cancelled");
        }
        catch (Exception ex)
        {
            return new DownloadResult(false, destinationPath, ex.Message);
        }
    }

    public string GenerateFileName(string sourceSite, string postId, string url)
    {
        var extension = GetExtensionFromUrl(url);
        var safeSite = SanitizeFileName(sourceSite);
        var safeId = SanitizeFileName(postId);
        return $"{safeSite}_{safeId}{extension}";
    }

    private static string GetExtensionFromUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return ".jpg";
        }

        var path = url;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            path = uri.AbsolutePath;
        }

        var queryIndex = path.IndexOf('?');
        if (queryIndex >= 0)
        {
            path = path[..queryIndex];
        }

        var lastDot = path.LastIndexOf('.');
        if (lastDot >= 0 && lastDot < path.Length - 1)
        {
            var ext = path[lastDot..].ToLowerInvariant();
            if (ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".webm" or ".mp4" or ".apng")
            {
                return ext;
            }
        }

        return ".jpg";
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "unknown";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var result = name;
        foreach (var c in invalid)
        {
            result = result.Replace(c, '_');
        }

        return result.Trim().ToLowerInvariant();
    }
}

public record DownloadProgress(string Url, double Progress, long BytesDownloaded, long TotalBytes);
public record DownloadResult(bool Success, string? FilePath, string? Error);
