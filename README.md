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
- **Media type**: Images, Animated images, Videos
- **Size range**: Large, Medium, Small
- **Rating filters**: Safe, Questionable, Adult
- **Local filters**: Minimum score, dimensions, required/excluded tags
- Shuffle results, export to CSV

### Post Grid
- Card-based grid with lazy thumbnail loading
- Favorites tab with persistent storage
- Single-click selection, CTRL+click for multi-select
- Context menu: View, Favorite, Tags, Copy URLs, Hide

### Batch Operations
- Multi-select posts with CTRL+click
- Download all selected posts to local folder
- Hide/unhide posts (blacklist feature)
- Clear all selections

### Slideshow Mode
- Automatic image slideshow with configurable interval
- Keyboard navigation: Left/Right arrows, Space to pause, ESC to exit
- Fullscreen viewing experience

### Keyboard Shortcuts
- `Right/Left arrows`: Navigate between posts (with CTRL or in slideshow mode)
- `Ctrl+A`: Select all visible posts
- `Ctrl+D`: Clear selection
- `Space`: Pause/resume slideshow
- `ESC`: Exit slideshow mode
- `F5`: Refresh search

### Tag Collection
- Automatically collects unique tags from visible posts
- Tag suggestions while searching

### Viewers
- **Image viewer**: Zoom, pan, fit-to-window
- **Video player**: WebM/MP4 playback via ffmpeg, seek slider, play/pause

### Performance
- Background preview loading with visibility-based priority
- Duplicate post suppression
- Lazy metadata hydration

### Updates & Storage
- Built-in update checker
- Settings persisted: credentials, favorites, filters, search history, blacklist

### Settings
- **Download folder**: Choose where to save downloaded images
- **Subfolders**: Option to create timestamped subfolders for each download
- **Filenames**: Preserve original filenames or generate custom ones
- **Slideshow**: Configurable interval, auto-start option
- **UI**: Customize card width and height
- **Advanced**: Request timeout, custom user agent
- **Language**: 8 languages supported (EN, IT, ES, FR, DE, PT, JA, ZH)

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
