using System.ComponentModel;
using System.Runtime.CompilerServices;
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Avalonia.Media;
using Material.Icons;

namespace BooruManager.Models;

public class ImagePost : INotifyPropertyChanged
{
    private IImage? _previewImage;
    private bool _isFavorite;
    private bool _isLoaded;
    private bool _isSelected;
    private int _width;
    private int _height;

    public string Id { get; set; } = string.Empty;
    public string SourceSite { get; set; } = string.Empty;
    public string PreviewUrl { get; set; } = string.Empty;
    public string FullImageUrl { get; set; } = string.Empty;
    public string PostUrl { get; set; } = string.Empty;
    public string Rating { get; set; } = string.Empty;
    public string Tags { get; set; } = string.Empty;
    public int Score { get; set; }
    public long CreatedAtUnix { get; set; }
    [JsonIgnore]
    public Dictionary<string, List<string>> TagGroups { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int Width
    {
        get => _width;
        set
        {
            if (_width == value)
            {
                return;
            }

            _width = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SummaryLine));
        }
    }

    public int Height
    {
        get => _height;
        set
        {
            if (_height == value)
            {
                return;
            }

            _height = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SummaryLine));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    [JsonIgnore]
    public IImage? PreviewImage
    {
        get => _previewImage;
        set
        {
            if (ReferenceEquals(_previewImage, value))
            {
                return;
            }

            _previewImage = value;
            OnPropertyChanged();
        }
    }

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value)
            {
                return;
            }

            _isFavorite = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FavoriteIconKind));
        }
    }

    public bool IsLoaded
    {
        get => _isLoaded;
        set
        {
            if (_isLoaded == value)
            {
                return;
            }

            _isLoaded = value;
            OnPropertyChanged();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    [JsonIgnore]
    public MaterialIconKind FavoriteIconKind => IsFavorite ? MaterialIconKind.Heart : MaterialIconKind.HeartOutline;
    [JsonIgnore]
    public string SummaryLine => $"{(SourceSite ?? string.Empty).ToUpperInvariant()} • {MediaTypeDisplay} • {RatingDisplay}{PixelSizeSegment} • #{Id}";
    [JsonIgnore]
    public string TagsDisplay => string.IsNullOrWhiteSpace(Tags) ? "(no tags)" : Tags.Replace('_', ' ');

    [JsonIgnore]
    private string MediaTypeDisplay
    {
        get
        {
            var path = FullImageUrl ?? string.Empty;
            if (Uri.TryCreate(path, UriKind.Absolute, out var uri))
            {
                path = uri.AbsolutePath;
            }

            return path.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                ? "VIDEO"
                : "IMAGE";
        }
    }

    [JsonIgnore]
    private string PixelSizeSegment => Width > 0 && Height > 0 ? $" • {Width}x{Height}px" : string.Empty;

    [JsonIgnore]
    private string RatingDisplay
    {
        get
        {
            var normalized = (Rating ?? string.Empty).Trim().ToLowerInvariant();
            return normalized switch
            {
                "s" or "safe" or "g" or "general" => "SAFE",
                "q" or "questionable" => "QUESTIONABLE",
                "e" or "explicit" or "adult" => "ADULT",
                _ => normalized.ToUpperInvariant()
            };
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
