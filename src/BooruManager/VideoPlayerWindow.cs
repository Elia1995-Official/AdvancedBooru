using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace BooruManager;

public class VideoPlayerWindow : Window
{
    private readonly string _videoUrl;

    private readonly Image _videoImage;
    private readonly TextBlock _statusText;
    private readonly Button _playPauseButton;
    private readonly Slider _timelineSlider;
    private readonly TextBlock _currentTimeText;
    private readonly TextBlock _durationText;
    private readonly Slider _volumeSlider;
    private readonly Button _muteButton;

    private CancellationTokenSource? _playbackCts;
    private Task? _playbackTask;
    private readonly SemaphoreSlim _playbackSwitchGate = new(1, 1);

    private bool _isPaused;
    private bool _isLoaded;
    private bool _isScrubbing;
    private bool _isInternalSliderUpdate;
    private bool _isMuted;
    private double _volume = 1.0;

    private VideoMetadata? _metadata;

    public VideoPlayerWindow(string sourceSite, string postId, string videoUrl)
    {
        _videoUrl = videoUrl;

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
        _timelineSlider.PointerPressed += (_, _) => _isScrubbing = true;
        _timelineSlider.PointerMoved += async (_, _) =>
        {
            if (_isScrubbing && _metadata != null)
            {
                await StartPlaybackFromAsync(_timelineSlider.Value);
            }
        };
        _timelineSlider.PointerReleased += async (_, _) =>
        {
            _isScrubbing = false;
            await StartPlaybackFromAsync(_timelineSlider.Value);
        };

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

        root.Children.Add(new Border
        {
            Margin = new Thickness(10, 10, 10, 0),
            Background = new SolidColorBrush(Color.Parse("#0E141C")),
            CornerRadius = new CornerRadius(8),
            Child = _videoImage
        });

        Grid.SetRow(controls, 1);
        root.Children.Add(controls);

        Grid.SetRow(timelineGrid, 2);
        root.Children.Add(timelineGrid);

        Content = root;

        Opened += OnOpened;
        Closed += OnClosed;
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
        CancelPlayback();
    }

    private void PlayPauseButtonOnClick(object? sender, EventArgs e)
    {
        _isPaused = !_isPaused;
        _playPauseButton.Content = _isPaused ? "Play" : "Pause";
    }

    private void MuteButtonOnClick(object? sender, EventArgs e)
    {
        _isMuted = !_isMuted;
        _muteButton.Content = _isMuted ? "🔇" : "🔊";
        _volumeSlider.Value = _isMuted ? 0 : _volume;
    }

    private void VolumeSliderOnValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        _volume = e.NewValue;
        if (_volume > 0)
        {
            _isMuted = false;
            _muteButton.Content = "🔊";
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

        _statusText.Text = $"{_metadata.Width}x{_metadata.Height} @ {_metadata.FrameRate:F2} fps";
        _playPauseButton.IsEnabled = true;
        _volumeSlider.IsEnabled = true;
        _muteButton.IsEnabled = true;
        _timelineSlider.IsEnabled = _metadata.DurationSeconds > 0;
        _timelineSlider.Maximum = Math.Max(1, _metadata.DurationSeconds);
        _durationText.Text = FormatTime(_metadata.DurationSeconds);

        await StartPlaybackFromAsync(0);
    }

    private async Task StartPlaybackFromAsync(double positionSeconds)
    {
        await _playbackSwitchGate.WaitAsync();
        try
        {
            CancelPlayback();

            _playbackCts = new CancellationTokenSource();
            var ct = _playbackCts.Token;
            var metadata = _metadata;
            if (metadata is null)
            {
                return;
            }

            _playbackTask = Task.Run(() => PlaybackLoopAsync(metadata, Math.Max(0, positionSeconds), ct), ct);
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

    private async Task PlaybackLoopAsync(VideoMetadata metadata, double startSeconds, CancellationToken cancellationToken)
    {
        await using var session = FfmpegVideoBackend.OpenRawVideoStream(_videoUrl, startSeconds);
        if (session is null)
        {
            await Dispatcher.UIThread.InvokeAsync(() => _statusText.Text = "Unable to start ffmpeg stream.");
            return;
        }

        using var cancellationRegistration = cancellationToken.Register(session.Stop);

        var stream = session.Stream;
        var frameBytes = metadata.Width * metadata.Height * 4;
        var buffer = new byte[frameBytes];
        var frameInterval = TimeSpan.FromSeconds(1.0 / Math.Max(1.0, metadata.FrameRate));

        var bitmap = new WriteableBitmap(
            new PixelSize(metadata.Width, metadata.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);

        await Dispatcher.UIThread.InvokeAsync(() => _videoImage.Source = bitmap);

        var sw = Stopwatch.StartNew();
        var nextFrameAt = sw.Elapsed;
        var position = startSeconds;
        var lastUiUpdate = TimeSpan.Zero;

        while (!cancellationToken.IsCancellationRequested)
        {
            var ok = await ReadExactAsync(stream, buffer, frameBytes, cancellationToken);
            if (!ok)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => _statusText.Text = "Playback completed");
                }

                break;
            }

            using (var fb = bitmap.Lock())
            {
                Marshal.Copy(buffer, 0, fb.Address, frameBytes);
            }

            await Dispatcher.UIThread.InvokeAsync(() => _videoImage.InvalidateVisual());

            position += frameInterval.TotalSeconds;

            if (sw.Elapsed - lastUiUpdate >= TimeSpan.FromMilliseconds(120))
            {
                lastUiUpdate = sw.Elapsed;
                await Dispatcher.UIThread.InvokeAsync(() => UpdateTimelineUi(position));
            }

            while (_isPaused && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(40, cancellationToken);
            }

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

        var clamped = Math.Clamp(positionSeconds, 0, Math.Max(0, _metadata.DurationSeconds));
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
    }

    private async Task CommitSeekAsync()
    {
        if (!_isScrubbing)
        {
            return;
        }

        _isScrubbing = false;
        await StartPlaybackFromAsync(_timelineSlider.Value);
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, count - read), cancellationToken);
            if (n == 0)
            {
                return false;
            }

            read += n;
        }

        return true;
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

    public static FfmpegRawVideoSession? OpenRawVideoStream(string source, double startSeconds)
    {
        var psi = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");

        if (startSeconds > 0)
        {
            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add(startSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        }

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
            return null;
        }

        return new FfmpegRawVideoSession(process);
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

internal sealed class FfmpegRawVideoSession : IAsyncDisposable
{
    private readonly Process _process;

    public FfmpegRawVideoSession(Process process)
    {
        _process = process;
        Stream = process.StandardOutput.BaseStream;
    }

    public Stream Stream { get; }

    public void Stop()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        try
        {
            await _process.WaitForExitAsync();
        }
        catch
        {
        }

        _process.Dispose();
    }
}
