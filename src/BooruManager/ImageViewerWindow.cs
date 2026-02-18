using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using BooruManager.Models;
using BooruManager.Services;

namespace BooruManager;

public class ImageViewerWindow : Window
{
    private const double ImagePadding = 8.0;

    private readonly ImagePost _post;
    private readonly ImageLoaderService _imageLoader;

    private readonly Image _image;
    private readonly Canvas _contentCanvas;
    private readonly TextBlock _statusText;
    private readonly ScrollViewer _scrollViewer;

    private Bitmap? _bitmap;
    private bool _autoFit = true;
    private double _zoom = 1.0;

    private bool _isPanning;
    private Point _lastPanPoint;

    private Point _imageTopLeft;
    private double _contentWidth;
    private double _contentHeight;

    public ImageViewerWindow(ImagePost post, ImageLoaderService imageLoader)
    {
        _post = post;
        _imageLoader = imageLoader;

        Title = $"{post.SourceSite} - {post.Id}";
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Width = 1000;
        Height = 720;
        Background = new SolidColorBrush(Color.Parse("#10151C"));

        _image = new Image
        {
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };

        _image.PointerPressed += ImageOnPointerPressed;
        _image.PointerMoved += ImageOnPointerMoved;
        _image.PointerReleased += ImageOnPointerReleased;
        _image.PointerCaptureLost += (_, _) => StopPanning();

        _contentCanvas = new Canvas();
        _contentCanvas.Children.Add(_image);

        _scrollViewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Content = _contentCanvas
        };
        _scrollViewer.AddHandler(InputElement.PointerWheelChangedEvent, ScrollViewerOnPointerWheelChanged, RoutingStrategies.Tunnel);

        _statusText = new TextBlock
        {
            Text = "Loading full image...",
            VerticalAlignment = VerticalAlignment.Center
        };

        var zoomOutButton = new Button { Content = "-", Width = 34 };
        zoomOutButton.Click += (_, _) => SetZoom(_zoom * 0.9, autoFit: false, anchorInViewport: null);

        var zoomInButton = new Button { Content = "+", Width = 34 };
        zoomInButton.Click += (_, _) => SetZoom(_zoom * 1.1, autoFit: false, anchorInViewport: null);

        var fitButton = new Button { Content = "Fit", Width = 56 };
        fitButton.Click += (_, _) => FitToWindow();

        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(10, 8)
        };
        controls.Children.Add(zoomOutButton);
        controls.Children.Add(zoomInButton);
        controls.Children.Add(fitButton);
        controls.Children.Add(_statusText);

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto")
        };
        root.Children.Add(_scrollViewer);

        Grid.SetRow(controls, 1);
        root.Children.Add(controls);

        Content = root;

        Opened += async (_, _) => await LoadImageAsync();
        SizeChanged += (_, _) =>
        {
            if (_bitmap is null)
            {
                return;
            }

            if (_autoFit)
            {
                FitToWindow();
                return;
            }

            UpdateImageLayout();
            _scrollViewer.Offset = ClampOffset(_scrollViewer.Offset);
        };
    }

    private async Task LoadImageAsync()
    {
        _bitmap = await _imageLoader.LoadBitmapAsync(_post.FullImageUrl, _post.SourceSite);
        if (_bitmap is null)
        {
            _statusText.Text = "Unable to load full image";
            return;
        }

        _image.Source = _bitmap;
        _statusText.Text = _post.FullImageUrl;

        var targetWidth = _bitmap.PixelSize.Width * 0.6;
        var targetHeight = _bitmap.PixelSize.Height * 0.6;

        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is not null)
        {
            var maxWidth = screen.WorkingArea.Width * 0.9;
            var maxHeight = screen.WorkingArea.Height * 0.9;
            targetWidth = Math.Min(targetWidth, maxWidth);
            targetHeight = Math.Min(targetHeight, maxHeight);
        }

        targetWidth = Math.Min(targetWidth, _bitmap.PixelSize.Width + 40);
        targetHeight = Math.Min(targetHeight, _bitmap.PixelSize.Height + 120);

        Width = Math.Max(320, targetWidth);
        Height = Math.Max(240, targetHeight);

        CenterOnCurrentScreen();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await Dispatcher.UIThread.InvokeAsync(FitToWindow, DispatcherPriority.Background);
    }

    private void FitToWindow()
    {
        if (_bitmap is null)
        {
            return;
        }

        var viewportWidth = Math.Max(1, _scrollViewer.Viewport.Width - (ImagePadding * 2));
        var viewportHeight = Math.Max(1, _scrollViewer.Viewport.Height - (ImagePadding * 2));

        var fitX = viewportWidth / _bitmap.PixelSize.Width;
        var fitY = viewportHeight / _bitmap.PixelSize.Height;
        var fit = Math.Min(fitX, fitY);

        if (double.IsNaN(fit) || double.IsInfinity(fit) || fit <= 0)
        {
            fit = 1;
        }

        SetZoom(fit, autoFit: true, anchorInViewport: null);
    }

    private void SetZoom(double value, bool autoFit, Point? anchorInViewport)
    {
        if (_bitmap is null)
        {
            return;
        }

        var oldZoom = _zoom;
        _autoFit = autoFit;
        _zoom = Math.Clamp(value, 0.05, 8.0);
        if (Math.Abs(_zoom - oldZoom) < 0.0001)
        {
            return;
        }

        var anchor = anchorInViewport ?? new Point(_scrollViewer.Viewport.Width / 2.0, _scrollViewer.Viewport.Height / 2.0);
        var anchorPixel = GetImagePixelAtViewportPoint(anchor, oldZoom);

        UpdateImageLayout();

        if (autoFit)
        {
            _scrollViewer.Offset = ClampOffset(new Vector(0, 0));
            return;
        }

        var newContentX = _imageTopLeft.X + (anchorPixel.X * _zoom);
        var newContentY = _imageTopLeft.Y + (anchorPixel.Y * _zoom);
        var targetOffset = new Vector(newContentX - anchor.X, newContentY - anchor.Y);
        _scrollViewer.Offset = ClampOffset(targetOffset);
    }

    private void UpdateImageLayout()
    {
        if (_bitmap is null)
        {
            return;
        }

        var viewportWidth = Math.Max(1, _scrollViewer.Viewport.Width);
        var viewportHeight = Math.Max(1, _scrollViewer.Viewport.Height);

        var scaledWidth = Math.Max(1, _bitmap.PixelSize.Width * _zoom);
        var scaledHeight = Math.Max(1, _bitmap.PixelSize.Height * _zoom);

        _image.Width = scaledWidth;
        _image.Height = scaledHeight;

        var contentWidth = Math.Max(viewportWidth, scaledWidth + (ImagePadding * 2));
        var contentHeight = Math.Max(viewportHeight, scaledHeight + (ImagePadding * 2));

        _contentWidth = contentWidth;
        _contentHeight = contentHeight;
        _contentCanvas.Width = contentWidth;
        _contentCanvas.Height = contentHeight;

        var left = (contentWidth - scaledWidth) / 2.0;
        var top = (contentHeight - scaledHeight) / 2.0;

        _imageTopLeft = new Point(left, top);
        Canvas.SetLeft(_image, left);
        Canvas.SetTop(_image, top);
    }

    private void ScrollViewerOnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_bitmap is null)
        {
            return;
        }

        var direction = Math.Exp(e.Delta.Y * 0.12);
        var anchor = e.GetPosition(_scrollViewer);
        SetZoom(_zoom * direction, autoFit: false, anchorInViewport: anchor);
        e.Handled = true;
    }

    private void ImageOnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_image).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isPanning = true;
        _lastPanPoint = e.GetPosition(_scrollViewer);
        _image.Cursor = new Cursor(StandardCursorType.SizeAll);
        e.Pointer.Capture(_image);
        e.Handled = true;
    }

    private void ImageOnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPanning)
        {
            return;
        }

        var current = e.GetPosition(_scrollViewer);
        var dx = current.X - _lastPanPoint.X;
        var dy = current.Y - _lastPanPoint.Y;

        _scrollViewer.Offset = ClampOffset(new Vector(_scrollViewer.Offset.X - dx, _scrollViewer.Offset.Y - dy));
        _lastPanPoint = current;
        e.Handled = true;
    }

    private void ImageOnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isPanning)
        {
            return;
        }

        e.Pointer.Capture(null);
        StopPanning();
        e.Handled = true;
    }

    private void StopPanning()
    {
        if (!_isPanning)
        {
            return;
        }

        _isPanning = false;
        _image.Cursor = new Cursor(StandardCursorType.Arrow);
    }

    private Vector ClampOffset(Vector offset)
    {
        var maxX = Math.Max(0, _contentWidth - _scrollViewer.Viewport.Width);
        var maxY = Math.Max(0, _contentHeight - _scrollViewer.Viewport.Height);
        var x = Math.Clamp(offset.X, 0, maxX);
        var y = Math.Clamp(offset.Y, 0, maxY);
        return new Vector(x, y);
    }

    private Point GetImagePixelAtViewportPoint(Point viewportPoint, double zoom)
    {
        if (_bitmap is null)
        {
            return new Point(0, 0);
        }

        var contentX = _scrollViewer.Offset.X + viewportPoint.X;
        var contentY = _scrollViewer.Offset.Y + viewportPoint.Y;

        var pixelX = (contentX - _imageTopLeft.X) / zoom;
        var pixelY = (contentY - _imageTopLeft.Y) / zoom;

        pixelX = Math.Clamp(pixelX, 0, _bitmap.PixelSize.Width);
        pixelY = Math.Clamp(pixelY, 0, _bitmap.PixelSize.Height);

        return new Point(pixelX, pixelY);
    }

    private void CenterOnCurrentScreen()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
        {
            return;
        }

        var x = screen.WorkingArea.X + Math.Max(0, (screen.WorkingArea.Width - (int)Width) / 2);
        var y = screen.WorkingArea.Y + Math.Max(0, (screen.WorkingArea.Height - (int)Height) / 2);
        Position = new PixelPoint(x, y);
    }
}
