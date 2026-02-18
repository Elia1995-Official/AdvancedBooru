using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace BooruManager;

public class VideoPlayerWindow : Window
{
    private const string MediaUserAgent =
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36 BooruManager/1.0";

    private readonly string _videoUrl;
    private readonly string _sourceSite;

    private readonly Image _videoImage;
    private readonly TextBlock _statusText;
    private readonly Button _playPauseButton;
    private readonly Slider _timelineSlider;
    private readonly TextBlock _currentTimeText;
    private readonly TextBlock _durationText;
    private readonly Slider _volumeSlider;
    private readonly Button _muteButton;
    private readonly object _audioGate = new();

    private readonly CancellationTokenSource _windowCts = new();
    private CancellationTokenSource? _playbackCts;
    private Task? _playbackTask;
    private Process? _audioProcess;
    private readonly SemaphoreSlim _playbackSwitchGate = new(1, 1);

    private bool _isPaused;
    private bool _isLoaded;
    private bool _isScrubbing;
    private bool _resumeAfterScrub;
    private bool _isInternalSliderUpdate;
    private bool _isMuted;
    private double _volume = 1.0;
    private WindowState _windowStateBeforeFullscreen = WindowState.Normal;

    private VideoMetadata? _metadata;
    private List<byte[]>? _frames;
    private WriteableBitmap? _playbackBitmap;
    private int _frameBytes;
    private double _frameRate = 30;
    private int _currentFrameIndex;

    public VideoPlayerWindow(string sourceSite, string postId, string videoUrl)
    {
        _videoUrl = videoUrl;
        _sourceSite = sourceSite ?? string.Empty;

        Title = $"{sourceSite} - {postId} (Video)";
        Width = 960;
        Height = 540;
        Background = new SolidColorBrush(Color.Parse("#10151C"));

        _videoImage = new Image
        {
            Stretch = Stretch.Uniform
        };

        _statusText = new TextBlock
        {
            Text = "Loading video...",
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };

        _playPauseButton = new Button
        {
            Content = "Pause",
            IsEnabled = false,
            Width = 90
        };
        _playPauseButton.Click += PlayPauseButtonOnClick;

        _muteButton = new Button
        {
            Content = "🔊",
            Width = 40,
            IsEnabled = false
        };
        _muteButton.Click += MuteButtonOnClick;

        _volumeSlider = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            Value = 1,
            Width = 80,
            IsEnabled = false
        };
        _volumeSlider.ValueChanged += VolumeSliderOnValueChanged;

        _currentTimeText = new TextBlock
        {
            Text = "00:00",
            Width = 56,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };

        _durationText = new TextBlock
        {
            Text = "00:00",
            Width = 56,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };

        _timelineSlider = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            Value = 0,
            IsEnabled = false,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
        };
        _timelineSlider.ValueChanged += TimelineSliderOnValueChanged;
        _timelineSlider.AddHandler(
            InputElement.PointerPressedEvent,
            TimelineSliderOnPointerPressed,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        _timelineSlider.AddHandler(
            InputElement.PointerReleasedEvent,
            TimelineSliderOnPointerReleased,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        _timelineSlider.AddHandler(
            InputElement.PointerCaptureLostEvent,
            TimelineSliderOnPointerCaptureLost,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        _timelineSlider.AddHandler(
            InputElement.KeyDownEvent,
            TimelineSliderOnKeyDown,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);

        var controls = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto,*"),
            Margin = new Thickness(10)
        };
        controls.Children.Add(_playPauseButton);

        var muteHost = new Border { Child = _muteButton, Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(muteHost, 1);
        controls.Children.Add(muteHost);

        var volumeHost = new Border { Child = _volumeSlider, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        Grid.SetColumn(volumeHost, 2);
        controls.Children.Add(volumeHost);

        var statusHost = new Border
        {
            Child = _statusText,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        Grid.SetColumn(statusHost, 3);
        controls.Children.Add(statusHost);

        var timelineGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(10, 0, 10, 10)
        };
        timelineGrid.Children.Add(_currentTimeText);

        Grid.SetColumn(_timelineSlider, 1);
        timelineGrid.Children.Add(_timelineSlider);

        Grid.SetColumn(_durationText, 2);
        timelineGrid.Children.Add(_durationText);

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto,Auto")
        };

        var videoHost = new Border
        {
            Margin = new Thickness(10, 10, 10, 0),
            Background = new SolidColorBrush(Color.Parse("#0E141C")),
            CornerRadius = new CornerRadius(8),
            Child = _videoImage
        };
        videoHost.DoubleTapped += VideoHostOnDoubleTapped;
        root.Children.Add(videoHost);

        Grid.SetRow(controls, 1);
        root.Children.Add(controls);

        Grid.SetRow(timelineGrid, 2);
        root.Children.Add(timelineGrid);

        Content = root;

        Opened += OnOpened;
        Closed += OnClosed;
        KeyDown += OnKeyDown;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (_isLoaded)
        {
            return;
        }

        _isLoaded = true;
        await StartPlaybackAsync();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _windowCts.Cancel();
        _windowCts.Dispose();
        StopAudioPlayback();
        CancelPlayback();
    }

    private void PlayPauseButtonOnClick(object? sender, EventArgs e)
    {
        _isPaused = !_isPaused;
        _playPauseButton.Content = _isPaused ? "Play" : "Pause";

        if (_isPaused)
        {
            StopAudioPlayback();
        }
        else
        {
            StartAudioPlaybackAtCurrentPosition();
        }
    }

    private void MuteButtonOnClick(object? sender, EventArgs e)
    {
        _isMuted = !_isMuted;
        _muteButton.Content = _isMuted ? "🔇" : "🔊";

        if (_isMuted)
        {
            _volumeSlider.Value = 0;
            StopAudioPlayback();
            return;
        }

        if (_volume <= 0)
        {
            _volume = 1.0;
            _volumeSlider.Value = 1.0;
        }
        else
        {
            _volumeSlider.Value = _volume;
        }

        if (!_isPaused)
        {
            StartAudioPlaybackAtCurrentPosition();
        }
    }

    private void VolumeSliderOnValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        _volume = Math.Clamp(e.NewValue, 0, 1);
        if (_volume > 0)
        {
            _isMuted = false;
            _muteButton.Content = "🔊";
        }
        else
        {
            _isMuted = true;
            _muteButton.Content = "🔇";
        }

        if (_isPaused)
        {
            return;
        }

        if (_isMuted || _volume <= 0)
        {
            StopAudioPlayback();
        }
        else
        {
            StartAudioPlaybackAtCurrentPosition();
        }
    }

    private async Task StartPlaybackAsync()
    {
        _metadata = await FfmpegVideoBackend.ProbeAsync(_videoUrl);
        if (_metadata is null)
        {
            _statusText.Text = "Unable to read video metadata (ffprobe failed).";
            return;
        }

        Width = _metadata.Width;
        Height = _metadata.Height;
        _frameRate = Math.Max(1.0, _metadata.FrameRate);
        _frameBytes = _metadata.Width * _metadata.Height * 4;

        _statusText.Text = $"Preloading video frames... ({_metadata.Width}x{_metadata.Height} @ {_frameRate:F2} fps)";

        List<byte[]> frames;
        try
        {
            frames = await FfmpegVideoBackend.PreloadRawFramesAsync(
                _videoUrl,
                _metadata.Width,
                _metadata.Height,
                _windowCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Unable to preload frames: {ex.Message}";
            return;
        }

        if (frames.Count == 0)
        {
            _statusText.Text = "No frames decoded from video.";
            return;
        }

        _frames = frames;
        _playbackBitmap = new WriteableBitmap(
            new PixelSize(_metadata.Width, _metadata.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);
        _videoImage.Source = _playbackBitmap;

        var durationFromFrames = FrameIndexToSeconds(frames.Count - 1);
        _statusText.Text = $"{_metadata.Width}x{_metadata.Height} @ {_frameRate:F2} fps • preloaded {frames.Count} frames";
        _playPauseButton.IsEnabled = true;
        _volumeSlider.IsEnabled = true;
        _muteButton.IsEnabled = true;
        _timelineSlider.IsEnabled = frames.Count > 1;
        _timelineSlider.Minimum = 0;
        _timelineSlider.Maximum = Math.Max(0, durationFromFrames);
        _durationText.Text = FormatTime(durationFromFrames);

        RenderFrame(0, updateTimeline: true);
        _isPaused = false;
        _playPauseButton.Content = "Pause";

        await StartPlaybackFromAsync(0);
    }

    private async Task StartPlaybackFromAsync(double positionSeconds)
    {
        try
        {
            await _playbackSwitchGate.WaitAsync(_windowCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            var previousTask = _playbackTask;
            CancelPlayback();
            if (previousTask is not null)
            {
                try
                {
                    await previousTask;
                }
                catch (OperationCanceledException)
                {
                }
                catch
                {
                }
            }

            if (_frames is not { Count: > 0 })
            {
                return;
            }

            var targetFrameIndex = TimeToFrameIndex(positionSeconds);
            RenderFrame(targetFrameIndex, updateTimeline: true);
            var targetSeconds = FrameIndexToSeconds(targetFrameIndex);

            StopAudioPlayback();
            if (!_isPaused)
            {
                StartAudioPlayback(targetSeconds);
            }

            _playbackCts = CancellationTokenSource.CreateLinkedTokenSource(_windowCts.Token);
            var ct = _playbackCts.Token;
            _playbackTask = Task.Run(() => PlaybackLoopAsync(targetFrameIndex, ct), ct);
        }
        finally
        {
            _playbackSwitchGate.Release();
        }
    }

    private void CancelPlayback()
    {
        if (_playbackCts is null)
        {
            return;
        }

        if (!_playbackCts.IsCancellationRequested)
        {
            _playbackCts.Cancel();
        }

        _playbackCts.Dispose();
        _playbackCts = null;
    }

    private async Task PlaybackLoopAsync(int startFrameIndex, CancellationToken cancellationToken)
    {
        var frames = _frames;
        if (frames is not { Count: > 0 })
        {
            return;
        }

        var frameInterval = TimeSpan.FromSeconds(1.0 / Math.Max(1.0, _frameRate));
        var frameIndex = Math.Clamp(startFrameIndex, 0, frames.Count - 1);
        var sw = Stopwatch.StartNew();
        var nextFrameAt = sw.Elapsed;

        while (!cancellationToken.IsCancellationRequested)
        {
            while (_isPaused && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(20, cancellationToken);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (frameIndex >= frames.Count)
            {
                StopAudioPlayback();
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _statusText.Text = "Playback completed";
                    _isPaused = true;
                    _playPauseButton.Content = "Play";
                });
                break;
            }

            var frameToRender = frameIndex;
            await Dispatcher.UIThread.InvokeAsync(
                () => RenderFrame(frameToRender, updateTimeline: true),
                DispatcherPriority.Render);

            frameIndex++;
            nextFrameAt += frameInterval;
            var delay = nextFrameAt - sw.Elapsed;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }
            else
            {
                nextFrameAt = sw.Elapsed;
            }
        }
    }

    private void UpdateTimelineUi(double positionSeconds)
    {
        if (_metadata is null)
        {
            return;
        }

        var maxTimeline = Math.Max(0, _timelineSlider.Maximum);
        var clamped = Math.Clamp(positionSeconds, 0, maxTimeline);
        _currentTimeText.Text = FormatTime(clamped);

        if (_isScrubbing)
        {
            return;
        }

        _isInternalSliderUpdate = true;
        _timelineSlider.Value = clamped;
        _isInternalSliderUpdate = false;
    }

    private void TimelineSliderOnValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isInternalSliderUpdate)
        {
            return;
        }

        if (!_isScrubbing)
        {
            return;
        }

        _currentTimeText.Text = FormatTime(e.NewValue);
        RenderFrame(TimeToFrameIndex(e.NewValue), updateTimeline: false);
    }

    private void TimelineSliderOnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_timelineSlider.IsEnabled)
        {
            return;
        }

        if (e.GetCurrentPoint(_timelineSlider).Properties.IsLeftButtonPressed)
        {
            _isScrubbing = true;
            _resumeAfterScrub = !_isPaused;
            _isPaused = true;
            _playPauseButton.Content = "Play";
            StopAudioPlayback();
        }
    }

    private async void TimelineSliderOnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_timelineSlider.IsEnabled)
        {
            return;
        }

        await CommitSeekAsync();
    }

    private async void TimelineSliderOnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!_timelineSlider.IsEnabled)
        {
            return;
        }

        await CommitSeekAsync();
    }

    private async Task CommitSeekAsync()
    {
        if (!_isScrubbing)
        {
            return;
        }

        _isScrubbing = false;
        var target = Math.Clamp(_timelineSlider.Value, 0, Math.Max(0, _timelineSlider.Maximum));
        UpdateTimelineUi(target);
        await StartPlaybackFromAsync(target);

        if (_resumeAfterScrub)
        {
            _isPaused = false;
            _playPauseButton.Content = "Pause";
            StartAudioPlaybackAtCurrentPosition();
        }

        _resumeAfterScrub = false;
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_frames is not { Count: > 0 })
        {
            return;
        }

        if (e.Key == Key.Right)
        {
            await StepFrameAsync(1);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Left)
        {
            await StepFrameAsync(-1);
            e.Handled = true;
        }
    }

    private async void TimelineSliderOnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Right)
        {
            if (_frames is { Count: > 0 })
            {
                await StepFrameAsync(1);
            }

            e.Handled = true;
            return;
        }

        if (e.Key == Key.Left)
        {
            if (_frames is { Count: > 0 })
            {
                await StepFrameAsync(-1);
            }

            e.Handled = true;
        }
    }

    private async Task StepFrameAsync(int delta)
    {
        if (_frames is not { Count: > 0 })
        {
            return;
        }

        _isPaused = true;
        _playPauseButton.Content = "Play";
        StopAudioPlayback();

        var targetIndex = Math.Clamp(_currentFrameIndex + delta, 0, _frames.Count - 1);
        RenderFrame(targetIndex, updateTimeline: true);
        await StartPlaybackFromAsync(FrameIndexToSeconds(targetIndex));
        PlayAudioFramePreviewAtCurrentPosition();
    }

    private int TimeToFrameIndex(double positionSeconds)
    {
        if (_frames is not { Count: > 0 })
        {
            return 0;
        }

        var rawIndex = (int)Math.Round(positionSeconds * Math.Max(1.0, _frameRate));
        return Math.Clamp(rawIndex, 0, _frames.Count - 1);
    }

    private double FrameIndexToSeconds(int frameIndex)
    {
        if (frameIndex <= 0)
        {
            return 0;
        }

        return frameIndex / Math.Max(1.0, _frameRate);
    }

    private void RenderFrame(int frameIndex, bool updateTimeline)
    {
        if (_frames is not { Count: > 0 } || _playbackBitmap is null)
        {
            return;
        }

        var clampedIndex = Math.Clamp(frameIndex, 0, _frames.Count - 1);
        var frame = _frames[clampedIndex];
        _currentFrameIndex = clampedIndex;

        using (var fb = _playbackBitmap.Lock())
        {
            Marshal.Copy(frame, 0, fb.Address, _frameBytes);
        }

        _videoImage.InvalidateVisual();

        if (updateTimeline)
        {
            UpdateTimelineUi(FrameIndexToSeconds(clampedIndex));
        }
    }

    private void StartAudioPlaybackAtCurrentPosition()
    {
        StartAudioPlayback(FrameIndexToSeconds(_currentFrameIndex));
    }

    private void PlayAudioFramePreviewAtCurrentPosition()
    {
        var durationSeconds = Math.Max(0.08, 2.0 / Math.Max(1.0, _frameRate));
        StartAudioPlayback(FrameIndexToSeconds(_currentFrameIndex), durationSeconds);
    }

    private void StartAudioPlayback(double positionSeconds)
    {
        StartAudioPlayback(positionSeconds, null);
    }

    private void StartAudioPlayback(double positionSeconds, double? clipDurationSeconds)
    {
        if (_isMuted || _volume <= 0)
        {
            return;
        }

        lock (_audioGate)
        {
            StopAudioPlaybackCore();

            var psi = new ProcessStartInfo("ffplay")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add("-nodisp");
            psi.ArgumentList.Add("-autoexit");
            psi.ArgumentList.Add("-loglevel");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-vn");
            psi.ArgumentList.Add("-user_agent");
            psi.ArgumentList.Add(MediaUserAgent);

            var referer = ResolveMediaReferer();
            if (!string.IsNullOrWhiteSpace(referer))
            {
                psi.ArgumentList.Add("-referer");
                psi.ArgumentList.Add(referer);
            }

            psi.ArgumentList.Add("-volume");
            psi.ArgumentList.Add(((int)Math.Round(Math.Clamp(_volume, 0, 1) * 100)).ToString(CultureInfo.InvariantCulture));

            var hasAudioFilter = false;
            if (positionSeconds > 0 || clipDurationSeconds is > 0)
            {
                var startText = Math.Max(0, positionSeconds).ToString("0.###", CultureInfo.InvariantCulture);
                string filter;
                if (clipDurationSeconds is > 0)
                {
                    var durationText = clipDurationSeconds.Value.ToString("0.###", CultureInfo.InvariantCulture);
                    filter = $"atrim=start={startText}:duration={durationText},asetpts=PTS-STARTPTS";
                }
                else
                {
                    filter = $"atrim=start={startText},asetpts=PTS-STARTPTS";
                }

                psi.ArgumentList.Add("-af");
                psi.ArgumentList.Add(filter);
                hasAudioFilter = true;
            }

            if (!hasAudioFilter && clipDurationSeconds is > 0)
            {
                psi.ArgumentList.Add("-t");
                psi.ArgumentList.Add(clipDurationSeconds.Value.ToString("0.###", CultureInfo.InvariantCulture));
            }

            psi.ArgumentList.Add(_videoUrl);

            try
            {
                var process = Process.Start(psi);
                _audioProcess = process;
                if (process is not null)
                {
                    MonitorAudioProcess(process);
                }
            }
            catch
            {
                _audioProcess = null;
                _statusText.Text = "Unable to start audio process (ffplay).";
            }
        }
    }

    private void MonitorAudioProcess(Process process)
    {
        _ = Task.Run(async () =>
        {
            string errorText;
            try
            {
                errorText = await process.StandardError.ReadToEndAsync();
            }
            catch
            {
                errorText = string.Empty;
            }

            try
            {
                await process.WaitForExitAsync(_windowCts.Token);
            }
            catch
            {
                return;
            }

            if (_windowCts.IsCancellationRequested)
            {
                return;
            }

            if (process.ExitCode == 0)
            {
                return;
            }

            var reason = FirstLineOrDefault(errorText);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _statusText.Text = string.IsNullOrWhiteSpace(reason)
                    ? $"Audio process failed (code {process.ExitCode})."
                    : $"Audio error: {reason}";
            });
        });
    }

    private string? ResolveMediaReferer()
    {
        var normalized = _sourceSite.Trim().ToLowerInvariant();
        if (normalized is "xbooru")
        {
            return "https://xbooru.com/";
        }

        if (normalized is "gelbooru")
        {
            return "https://gelbooru.com/";
        }

        if (normalized is "tabbooru" or "tab.booru.org")
        {
            return "https://tab.booru.org/";
        }

        if (normalized is "allgirlbooru" or "allgirl.booru.org")
        {
            return "https://allgirl.booru.org/";
        }

        if (normalized is "thecollectionbooru" or "the-collection.booru.org")
        {
            return "https://the-collection.booru.org/";
        }

        if (normalized is "safebooru")
        {
            return "https://safebooru.org/";
        }

        if (normalized is "e621")
        {
            return "https://e621.net/";
        }

        if (normalized is "danbooru")
        {
            return "https://danbooru.donmai.us/";
        }

        if (!Uri.TryCreate(_videoUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return $"{uri.Scheme}://{uri.Host}/";
    }

    private static string FirstLineOrDefault(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                return trimmed;
            }
        }

        return string.Empty;
    }

    private void VideoHostOnDoubleTapped(object? sender, TappedEventArgs e)
    {
        ToggleFullscreen();
    }

    private void ToggleFullscreen()
    {
        if (WindowState == WindowState.FullScreen)
        {
            WindowState = _windowStateBeforeFullscreen == WindowState.FullScreen
                ? WindowState.Normal
                : _windowStateBeforeFullscreen;
            return;
        }

        _windowStateBeforeFullscreen = WindowState;
        WindowState = WindowState.FullScreen;
    }

    private void StopAudioPlayback()
    {
        lock (_audioGate)
        {
            StopAudioPlaybackCore();
        }
    }

    private void StopAudioPlaybackCore()
    {
        if (_audioProcess is null)
        {
            return;
        }

        try
        {
            if (!_audioProcess.HasExited)
            {
                _audioProcess.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
        finally
        {
            _audioProcess.Dispose();
            _audioProcess = null;
        }
    }

    private static string FormatTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}"
            : $"{ts.Minutes:00}:{ts.Seconds:00}";
    }
}

internal sealed class VideoMetadata
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required double FrameRate { get; init; }
    public required double DurationSeconds { get; init; }
}

internal static class FfmpegVideoBackend
{
    public static async Task<VideoMetadata?> ProbeAsync(string source)
    {
        var psi = new ProcessStartInfo("ffprobe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-select_streams");
        psi.ArgumentList.Add("v:0");
        psi.ArgumentList.Add("-show_entries");
        psi.ArgumentList.Add("stream=width,height,r_frame_rate:format=duration");
        psi.ArgumentList.Add("-of");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add(source);

        using var process = Process.Start(psi);
        if (process is null)
        {
            return null;
        }

        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(stdout);
        if (!doc.RootElement.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0)
        {
            return null;
        }

        var stream = streams[0];

        var width = stream.TryGetProperty("width", out var widthEl) ? widthEl.GetInt32() : 0;
        var height = stream.TryGetProperty("height", out var heightEl) ? heightEl.GetInt32() : 0;
        var fpsText = stream.TryGetProperty("r_frame_rate", out var fpsEl) ? fpsEl.GetString() : "30/1";

        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var duration = 0.0;
        if (doc.RootElement.TryGetProperty("format", out var format)
            && format.TryGetProperty("duration", out var durationEl)
            && double.TryParse(durationEl.GetString(), out var parsedDuration)
            && parsedDuration > 0)
        {
            duration = parsedDuration;
        }

        return new VideoMetadata
        {
            Width = width,
            Height = height,
            FrameRate = ParseFrameRate(fpsText),
            DurationSeconds = duration
        };
    }

    public static async Task<List<byte[]>> PreloadRawFramesAsync(
        string source,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        var frameBytes = checked(width * height * 4);

        var psi = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(source);
        psi.ArgumentList.Add("-an");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("rawvideo");
        psi.ArgumentList.Add("-pix_fmt");
        psi.ArgumentList.Add("bgra");
        psi.ArgumentList.Add("-");

        var process = Process.Start(psi);
        if (process is null)
        {
            throw new InvalidOperationException("Unable to start ffmpeg.");
        }

        using var processScope = process;
        var stderrDrain = process.StandardError.ReadToEndAsync();
        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
        });

        var frames = new List<byte[]>();
        var stream = process.StandardOutput.BaseStream;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var frame = new byte[frameBytes];
            var read = await ReadFrameAsync(stream, frame, frameBytes, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (read < frameBytes)
            {
                break;
            }

            frames.Add(frame);
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        finally
        {
            _ = stderrDrain;
        }

        if (process.ExitCode != 0 && frames.Count == 0)
        {
            throw new InvalidOperationException("ffmpeg failed to decode video.");
        }

        return frames;
    }

    private static async Task<int> ReadFrameAsync(Stream stream, byte[] buffer, int count, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, count - read), cancellationToken);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        return read;
    }

    private static double ParseFrameRate(string? fpsText)
    {
        if (string.IsNullOrWhiteSpace(fpsText))
        {
            return 30;
        }

        var parts = fpsText.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], out var numerator)
            && double.TryParse(parts[1], out var denominator)
            && denominator > 0)
        {
            return numerator / denominator;
        }

        if (double.TryParse(fpsText, out var fps) && fps > 0)
        {
            return fps;
        }

        return 30;
    }
}
