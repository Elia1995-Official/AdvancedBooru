# Advanced Booru Manager (Avalonia)

Desktop Booru browser for Windows, Linux, and macOS built with Avalonia.

## Current Features

### Search and Sources
- Multi-site support
- Real API integrations (JSON/XML), with HTML fallback parsing for Gelbooru-like sources when needed.
- Automatic full-load search flow: after a search starts, the app keeps loading pages until no more results are available.
- Rating filter toggles: `safe`, `questionable`, `adult`.
- Per-site credentials with login/logout UI.
- Credential validation for e621 and Danbooru.
- Search history with quick recall and clear-history action.

### Sorting and Filtering
- Sort modes:
  - Date (newest)
  - Size (largest)
  - Votes (highest)
- Media-type filter:
  - All media
  - Static images
  - Animated images (`gif`/`apng`)
  - Videos
  - WebM only
  - MP4 only
- Size-range filter:
  - All sizes
  - Large (>2000px)
  - Medium (1000-2000px)
  - Small (<1000px)
- Local advanced filters:
  - Minimum score
  - Minimum width
  - Minimum height
  - Required tags
  - Excluded tags
- Utility actions:
  - Reset local filters
  - Shuffle visible results
  - Export visible results to CSV

### Post Grid and Actions
- Browse tab with card grid and preview thumbnails.
- Favorites tab with persistent favorites.
- Favorite toggle from both button and context menu.
- Context menu actions:
  - View post/media
  - Toggle favorite
  - Open tag selector
  - Copy post URL
  - Copy media URL
  - Copy tags
- Enter-to-search from the search textbox.
- Card fade-in when preview image finishes loading.

### Tag Selector
- Grouped tags by category (artist, character, copyright, species, general, meta, etc.).
- Multi-select supported, including multiple tags from the same category.
- Custom window chrome:
  - Native title bar removed
  - Custom title bar with title `Tag Selector`
  - Single `X` close button
- Window sized to content with min/max constraints and scrolling for long tag lists.

### Viewers
- Image viewer:
  - Dedicated window for full image
  - Zoom in/out, mouse-wheel zoom, fit-to-window
  - Click-drag panning
  - Auto-fit behavior on resize
- Video viewer:
  - Playback for WebM/MP4 through `ffmpeg`/`ffprobe`
  - Timeline slider with seek
  - Play/pause
  - Frame rendering inside Avalonia UI

### Performance and UX
- Background preview loading queue with worker pool.
- Priority boosting for currently visible cards.
- Duplicate suppression across pages.
- Lazy hydration of missing favorite metadata on startup.

### Updates and Persistence
- Built-in update checks with NetSparkle (`Check for updates` button + startup loop).
- Settings persisted in app data (`settings.json`), including:
  - Credentials by site
  - Recent searches
  - Favorites list and snapshots
  - Page size
  - Sort mode
  - Favorite-only toggle
  - Advanced local filters

## Requirements

- .NET 10 SDK
- `ffmpeg` and `ffprobe` in PATH (required for video playback)

## Quick Start

```bash
cd src/BooruManager
dotnet restore
dotnet run
```

## Build

```bash
dotnet build src/BooruManager/BooruManager.csproj
```

## Notes / Limitations

- Video playback currently decodes video-only stream (`-an`), so no audio output.
- Credentials are validated only for e621 and Danbooru; other sources accept stored credentials without remote validation.
