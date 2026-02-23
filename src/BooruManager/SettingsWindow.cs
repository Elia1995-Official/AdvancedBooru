using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using BooruManager.Services;

namespace BooruManager;

public class SettingsWindow : Window
{
    private readonly TextBox _downloadFolderTextBox;
    private readonly ComboBox _languageComboBox;
    private readonly NumericUpDown _slideshowIntervalNumeric;
    private readonly NumericUpDown _maxDownloadsNumeric;
    private readonly NumericUpDown _timeoutNumeric;
    private readonly NumericUpDown _cardWidthNumeric;
    private readonly NumericUpDown _cardHeightNumeric;
    private readonly CheckBox _createSubfoldersCheckBox;
    private readonly CheckBox _preserveFilenamesCheckBox;
    private readonly CheckBox _confirmDownloadCheckBox;
    private readonly CheckBox _showNotificationsCheckBox;
    private readonly CheckBox _autoStartSlideshowCheckBox;
    private readonly TextBox _userAgentTextBox;

    public string? SelectedDownloadFolder { get; private set; }
    public string? SelectedLanguage { get; private set; }
    public int SlideshowInterval { get; private set; } = 3;
    public int MaxConcurrentDownloads { get; private set; } = 3;
    public int RequestTimeout { get; private set; } = 30;
    public int CardWidth { get; private set; } = 340;
    public int CardHeight { get; private set; } = 400;
    public bool CreateSubfolders { get; private set; } = true;
    public bool PreserveFilenames { get; private set; }
    public bool ConfirmDownload { get; private set; } = true;
    public bool ShowNotifications { get; private set; } = true;
    public bool AutoStartSlideshow { get; private set; }
    public string CustomUserAgent { get; private set; } = string.Empty;
    public bool SettingsChanged { get; private set; }

    public SettingsWindow(
        string currentDownloadFolder,
        string currentLanguage,
        int slideshowInterval,
        int maxDownloads,
        int timeout,
        int cardWidth,
        int cardHeight,
        bool createSubfolders,
        bool preserveFilenames,
        bool confirmDownload,
        bool showNotifications,
        bool autoStartSlideshow,
        string customUserAgent)
    {
        Title = LocalizationService.Instance["SettingsWindowTitle"];
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Width = 550;
        Height = 620;
        Background = new SolidColorBrush(Color.Parse("#10151C"));
        CanResize = true;
        MinWidth = 450;
        MinHeight = 500;

        var scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        var mainPanel = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16
        };

        var downloadSectionPanel = new StackPanel();
        var downloadTitle = new TextBlock
        {
            Text = LocalizationService.Instance["DownloadSettings"],
            FontSize = 14,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#59BEF9")),
            Margin = new Thickness(0, 0, 0, 8)
        };
        downloadSectionPanel.Children.Add(downloadTitle);

        var folderLabel = new TextBlock
        {
            Text = LocalizationService.Instance["DownloadFolder"],
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#8FB0CF"))
        };
        downloadSectionPanel.Children.Add(folderLabel);

        _downloadFolderTextBox = new TextBox
        {
            Text = currentDownloadFolder,
            Watermark = LocalizationService.Instance["DownloadFolderPlaceholder"],
            Width = 350,
            IsReadOnly = true,
            Margin = new Thickness(0, 4, 0, 0)
        };

        var browseButton = new Button
        {
            Content = LocalizationService.Instance["Browse"],
            Width = 80,
            Margin = new Thickness(8, 0, 0, 0)
        };
        browseButton.Click += BrowseButton_OnClick;

        var folderRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 4, 0, 0)
        };
        folderRow.Children.Add(_downloadFolderTextBox);
        folderRow.Children.Add(browseButton);
        downloadSectionPanel.Children.Add(folderRow);

        _createSubfoldersCheckBox = new CheckBox
        {
            Content = LocalizationService.Instance["CreateSubfolders"],
            IsChecked = createSubfolders,
            Margin = new Thickness(0, 8, 0, 0)
        };
        downloadSectionPanel.Children.Add(_createSubfoldersCheckBox);

        _preserveFilenamesCheckBox = new CheckBox
        {
            Content = LocalizationService.Instance["PreserveOriginalFilenames"],
            IsChecked = preserveFilenames,
            Margin = new Thickness(0, 4, 0, 0)
        };
        downloadSectionPanel.Children.Add(_preserveFilenamesCheckBox);

        _confirmDownloadCheckBox = new CheckBox
        {
            Content = LocalizationService.Instance["ConfirmBeforeDownload"],
            IsChecked = confirmDownload,
            Margin = new Thickness(0, 4, 0, 0)
        };
        downloadSectionPanel.Children.Add(_confirmDownloadCheckBox);

        _showNotificationsCheckBox = new CheckBox
        {
            Content = LocalizationService.Instance["ShowDownloadNotifications"],
            IsChecked = showNotifications,
            Margin = new Thickness(0, 4, 0, 0)
        };
        downloadSectionPanel.Children.Add(_showNotificationsCheckBox);

        var maxDownloadsLabel = new TextBlock
        {
            Text = LocalizationService.Instance["MaxConcurrentDownloads"],
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#8FB0CF")),
            Margin = new Thickness(0, 8, 0, 0)
        };
        downloadSectionPanel.Children.Add(maxDownloadsLabel);

        _maxDownloadsNumeric = new NumericUpDown
        {
            Value = maxDownloads,
            Minimum = 1,
            Maximum = 10,
            Width = 100,
            Margin = new Thickness(0, 4, 0, 0)
        };
        downloadSectionPanel.Children.Add(_maxDownloadsNumeric);

        var downloadSection = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#1A2330")),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Child = downloadSectionPanel
        };
        mainPanel.Children.Add(downloadSection);

        var slideshowSectionPanel = new StackPanel();
        var slideshowTitle = new TextBlock
        {
            Text = LocalizationService.Instance["SlideshowSettings"],
            FontSize = 14,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#59BEF9")),
            Margin = new Thickness(0, 0, 0, 8)
        };
        slideshowSectionPanel.Children.Add(slideshowTitle);

        var intervalLabel = new TextBlock
        {
            Text = LocalizationService.Instance["SlideshowIntervalSeconds"],
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#8FB0CF"))
        };
        slideshowSectionPanel.Children.Add(intervalLabel);

        _slideshowIntervalNumeric = new NumericUpDown
        {
            Value = slideshowInterval,
            Minimum = 1,
            Maximum = 60,
            Width = 100,
            Margin = new Thickness(0, 4, 0, 0)
        };
        slideshowSectionPanel.Children.Add(_slideshowIntervalNumeric);

        _autoStartSlideshowCheckBox = new CheckBox
        {
            Content = LocalizationService.Instance["AutoStartSlideshow"],
            IsChecked = autoStartSlideshow,
            Margin = new Thickness(0, 8, 0, 0)
        };
        slideshowSectionPanel.Children.Add(_autoStartSlideshowCheckBox);

        var slideshowSection = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#1A2330")),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Child = slideshowSectionPanel
        };
        mainPanel.Children.Add(slideshowSection);

        var uiSectionPanel = new StackPanel();
        var uiTitle = new TextBlock
        {
            Text = LocalizationService.Instance["UISettings"],
            FontSize = 14,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#59BEF9")),
            Margin = new Thickness(0, 0, 0, 8)
        };
        uiSectionPanel.Children.Add(uiTitle);

        var cardWidthLabel = new TextBlock
        {
            Text = LocalizationService.Instance["CardWidth"],
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#8FB0CF"))
        };
        uiSectionPanel.Children.Add(cardWidthLabel);

        _cardWidthNumeric = new NumericUpDown
        {
            Value = cardWidth,
            Minimum = 200,
            Maximum = 600,
            Width = 100,
            Margin = new Thickness(0, 4, 0, 0)
        };
        uiSectionPanel.Children.Add(_cardWidthNumeric);

        var cardHeightLabel = new TextBlock
        {
            Text = LocalizationService.Instance["CardHeight"],
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#8FB0CF")),
            Margin = new Thickness(0, 8, 0, 0)
        };
        uiSectionPanel.Children.Add(cardHeightLabel);

        _cardHeightNumeric = new NumericUpDown
        {
            Value = cardHeight,
            Minimum = 250,
            Maximum = 600,
            Width = 100,
            Margin = new Thickness(0, 4, 0, 0)
        };
        uiSectionPanel.Children.Add(_cardHeightNumeric);

        var uiSection = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#1A2330")),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Child = uiSectionPanel
        };
        mainPanel.Children.Add(uiSection);

        var advancedSectionPanel = new StackPanel();
        var advancedTitle = new TextBlock
        {
            Text = LocalizationService.Instance["AdvancedSettings"],
            FontSize = 14,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#59BEF9")),
            Margin = new Thickness(0, 0, 0, 8)
        };
        advancedSectionPanel.Children.Add(advancedTitle);

        var timeoutLabel = new TextBlock
        {
            Text = LocalizationService.Instance["RequestTimeoutSeconds"],
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#8FB0CF"))
        };
        advancedSectionPanel.Children.Add(timeoutLabel);

        _timeoutNumeric = new NumericUpDown
        {
            Value = timeout,
            Minimum = 5,
            Maximum = 120,
            Width = 100,
            Margin = new Thickness(0, 4, 0, 0)
        };
        advancedSectionPanel.Children.Add(_timeoutNumeric);

        var userAgentLabel = new TextBlock
        {
            Text = LocalizationService.Instance["CustomUserAgent"],
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#8FB0CF")),
            Margin = new Thickness(0, 8, 0, 0)
        };
        advancedSectionPanel.Children.Add(userAgentLabel);

        _userAgentTextBox = new TextBox
        {
            Text = customUserAgent,
            Watermark = LocalizationService.Instance["UserAgentPlaceholder"],
            Width = 400,
            Margin = new Thickness(0, 4, 0, 0)
        };
        advancedSectionPanel.Children.Add(_userAgentTextBox);

        var advancedSection = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#1A2330")),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Child = advancedSectionPanel
        };
        mainPanel.Children.Add(advancedSection);

        var languageSectionPanel = new StackPanel();
        var languageTitle = new TextBlock
        {
            Text = LocalizationService.Instance["LanguageSettings"],
            FontSize = 14,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#59BEF9")),
            Margin = new Thickness(0, 0, 0, 8)
        };
        languageSectionPanel.Children.Add(languageTitle);

        var languageLabel = new TextBlock
        {
            Text = LocalizationService.Instance["Language"],
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#8FB0CF"))
        };
        languageSectionPanel.Children.Add(languageLabel);

        _languageComboBox = new ComboBox
        {
            ItemsSource = LocalizationService.Instance.AvailableLanguages,
            Width = 200,
            Margin = new Thickness(0, 4, 0, 0)
        };

        var currentLang = LocalizationService.Instance.AvailableLanguages
            .FirstOrDefault(l => l.Code == currentLanguage);
        _languageComboBox.SelectedItem = currentLang ?? LocalizationService.Instance.AvailableLanguages[0];
        languageSectionPanel.Children.Add(_languageComboBox);

        var languageSection = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#1A2330")),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Child = languageSectionPanel
        };
        mainPanel.Children.Add(languageSection);

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 16, 0, 0)
        };

        var saveButton = new Button
        {
            Content = LocalizationService.Instance["Save"],
            Width = 80
        };
        saveButton.Click += SaveButton_OnClick;

        var cancelButton = new Button
        {
            Content = LocalizationService.Instance["Cancel"],
            Width = 80
        };
        cancelButton.Click += (_, _) => Close();

        buttonPanel.Children.Add(saveButton);
        buttonPanel.Children.Add(cancelButton);
        mainPanel.Children.Add(buttonPanel);

        scrollViewer.Content = mainPanel;
        Content = scrollViewer;
    }

    private async void BrowseButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider is null)
        {
            return;
        }

        var result = await storageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = LocalizationService.Instance["SelectDownloadFolder"],
            AllowMultiple = false
        });

        if (result.Count > 0)
        {
            _downloadFolderTextBox.Text = result[0].Path.LocalPath;
        }
    }

    private void SaveButton_OnClick(object? sender, RoutedEventArgs e)
    {
        SelectedDownloadFolder = _downloadFolderTextBox.Text;
        SelectedLanguage = (_languageComboBox.SelectedItem as LanguageInfo)?.Code ?? "en";
        SlideshowInterval = (int)(_slideshowIntervalNumeric.Value ?? 3);
        MaxConcurrentDownloads = (int)(_maxDownloadsNumeric.Value ?? 3);
        RequestTimeout = (int)(_timeoutNumeric.Value ?? 30);
        CardWidth = (int)(_cardWidthNumeric.Value ?? 340);
        CardHeight = (int)(_cardHeightNumeric.Value ?? 400);
        CreateSubfolders = _createSubfoldersCheckBox.IsChecked ?? true;
        PreserveFilenames = _preserveFilenamesCheckBox.IsChecked ?? false;
        ConfirmDownload = _confirmDownloadCheckBox.IsChecked ?? true;
        ShowNotifications = _showNotificationsCheckBox.IsChecked ?? true;
        AutoStartSlideshow = _autoStartSlideshowCheckBox.IsChecked ?? false;
        CustomUserAgent = _userAgentTextBox.Text ?? string.Empty;
        SettingsChanged = true;

        Close();
    }
}
