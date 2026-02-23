using System;
using System.Collections.Generic;

namespace BooruManager.Models;

public class PostCollection
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public HashSet<string> PostKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string Description { get; set; } = string.Empty;
    public string Color { get; set; } = "#2A8FD7";

    public int Count => PostKeys.Count;
}

public class PostNote
{
    public string PostKey { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class TagBlacklistEntry
{
    public string Tag { get; set; } = string.Empty;
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}

public class ViewedPost
{
    public string PostKey { get; set; } = string.Empty;
    public DateTime ViewedAt { get; set; } = DateTime.UtcNow;
}

public class DownloadQueueItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string PostKey { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string DestinationPath { get; set; } = string.Empty;
    public string Status { get; set; } = "pending";
    public double Progress { get; set; }
    public string? Error { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}

public class TagStatistic
{
    public string Tag { get; set; } = string.Empty;
    public int Count { get; set; }
    public double Percentage { get; set; }
}
