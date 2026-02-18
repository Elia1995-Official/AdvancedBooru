# Advanced Booru Manager

Desktop Booru browser for Windows, Linux, and macOS built with Avalonia.

## Features

### Search & Browsing
- Support for multiple Booru sites
- Real API integrations with automatic fallback parsing
- Infinite scroll: search keeps loading pages until all results are fetched
- Search history with quick recall

### Filtering & Sorting
- **Sort by**: Date, Size, Votes
- **Media type**: All, Static images, Animated, Videos, WebM, MP4
- **Size range**: Large, Medium, Small
- **Rating filters**: Safe, Questionable, Adult
- **Local filters**: Minimum score, dimensions, required/excluded tags
- Shuffle results, export to CSV

### Post Grid
- Card-based grid with lazy thumbnail loading
- Favorites tab with persistent storage
- Single-click selection, CTRL+click for multi-select
- Context menu: View, Favorite, Tags, Copy URLs

### Tag Selector
- Tags grouped by category
- Multi-select support
- Custom window with scrolling

### Viewers
- **Image viewer**: Zoom, pan, fit-to-window
- **Video player**: WebM/MP4 playback via ffmpeg, seek slider, play/pause

### Performance
- Background preview loading with visibility-based priority
- Duplicate post suppression
- Lazy metadata hydration

### Updates & Storage
- Built-in update checker
- Settings persisted: credentials, favorites, filters, search history

## Requirements

- .NET 10 SDK
- ffmpeg + ffprobe (for video playback)

## Build & Run

```bash
cd src/BooruManager
dotnet restore
dotnet run
```

## Build Release

```bash
dotnet publish src/BooruManager/BooruManager.csproj -c Release -r linux-x64 --self-contained
```

## Notes

- Video playback is video-only (no audio)
