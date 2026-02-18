using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using BooruManager.Models;

namespace BooruManager;

public class TagSelectorWindow : Window
{
    private const double CategoryListHeight = 112;
    private const double TabContentWidth = 360;
    private readonly List<ListBox> _tagLists = new();

    public TagSelectorWindow(ImagePost post)
        : this(new[] { post })
    {
    }

    public TagSelectorWindow(IReadOnlyList<ImagePost> posts)
    {
        var normalizedPosts = (posts ?? Array.Empty<ImagePost>())
            .Where(p => p is not null && !string.IsNullOrWhiteSpace(p.SourceSite) && !string.IsNullOrWhiteSpace(p.Id))
            .GroupBy(BuildPostKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        Title = "Tag Selector";
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#11161D"));

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto")
        };

        var closeTitleButton = new Button
        {
            Content = "X",
            Width = 34,
            Height = 28,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 2, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        closeTitleButton.Click += (_, _) => Close((IReadOnlyList<string>?)null);

        var titleBarGrid = new Grid
        {
            Height = 36,
            ColumnDefinitions = new ColumnDefinitions("34,*,34"),
            Margin = new Thickness(8, 8, 8, 0)
        };
        titleBarGrid.Children.Add(new Border
        {
            Width = 34,
            Height = 28,
            Opacity = 0
        });
        var titleText = new TextBlock
        {
            Text = "Tag Selector",
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.Parse("#D7E6FA"))
        };
        Grid.SetColumn(titleText, 1);
        titleBarGrid.Children.Add(titleText);
        Grid.SetColumn(closeTitleButton, 2);
        titleBarGrid.Children.Add(closeTitleButton);

        titleBarGrid.PointerPressed += (_, e) =>
        {
            if (closeTitleButton.IsPointerOver)
            {
                return;
            }

            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginMoveDrag(e);
            }
        };

        var titleBar = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#1A2330")),
            BorderBrush = new SolidColorBrush(Color.Parse("#2A8FD7")),
            BorderThickness = new Thickness(1, 1, 1, 0),
            CornerRadius = new CornerRadius(9, 9, 0, 0),
            Child = titleBarGrid
        };

        var tabControl = new TabControl
        {
            Margin = new Thickness(12, 10, 12, 0)
        };

        if (normalizedPosts.Count == 0)
        {
            tabControl.ItemsSource = new[]
            {
                new TabItem
                {
                    Header = "No posts",
                    Content = new TextBlock
                    {
                        Text = "No tags available for selected posts.",
                        Margin = new Thickness(6)
                    }
                }
            };
        }
        else
        {
            var tabItems = normalizedPosts
                .Select((post, index) => new TabItem
                {
                    Header = BuildTabHeader(post, index),
                    Content = BuildPostTabContent(post)
                })
                .ToList();

            tabControl.ItemsSource = tabItems;
            tabControl.SelectedIndex = 0;
        }

        var useSelectedButton = new Button
        {
            Content = "Use selected",
            Width = 130
        };
        useSelectedButton.Click += (_, _) =>
        {
            var selected = CollectSelectedTags();
            Close(selected);
        };

        var closeButton = new Button
        {
            Content = "Close",
            Width = 100
        };
        closeButton.Click += (_, _) => Close((IReadOnlyList<string>?)null);

        var bottomBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Thickness(12, 8, 12, 12)
        };
        bottomBar.Children.Add(useSelectedButton);
        bottomBar.Children.Add(closeButton);

        root.Children.Add(titleBar);
        Grid.SetRow(tabControl, 1);
        root.Children.Add(tabControl);
        Grid.SetRow(bottomBar, 2);
        root.Children.Add(bottomBar);

        Content = new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse("#2A8FD7")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0, 0, 9, 9),
            Background = new SolidColorBrush(Color.Parse("#11161D")),
            Child = root
        };
    }

    private Control BuildPostTabContent(ImagePost post)
    {
        var groupsPanel = new StackPanel
        {
            Spacing = 12,
            Margin = new Thickness(6),
            Width = TabContentWidth
        };

        groupsPanel.Children.Add(new TextBlock
        {
            Text = $"{post.SourceSite} #{post.Id}",
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.Parse("#8FB3D9"))
        });

        var groupedTags = BuildGroupedTags(post);
        if (groupedTags.Count == 0)
        {
            groupsPanel.Children.Add(new TextBlock
            {
                Text = "No tags available for this post.",
                FontSize = 15
            });
            return groupsPanel;
        }

        foreach (var group in groupedTags)
        {
            var section = new StackPanel
            {
                Spacing = 6
            };

            section.Children.Add(new TextBlock
            {
                Text = $"{group.Key} ({group.Value.Count})",
                FontSize = 15,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(Color.Parse("#59BEF9"))
            });

            var listBox = new ListBox
            {
                SelectionMode = SelectionMode.Multiple | SelectionMode.Toggle,
                Height = CategoryListHeight,
                MinHeight = CategoryListHeight,
                MaxHeight = CategoryListHeight,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ItemsSource = group.Value
            };

            _tagLists.Add(listBox);
            section.Children.Add(listBox);
            groupsPanel.Children.Add(section);
        }

        return groupsPanel;
    }

    private static Control BuildTabHeader(ImagePost post, int index)
    {
        var header = new TextBlock
        {
            Text = $"P{index + 1}",
            Margin = new Thickness(2, 0)
        };

        ToolTip.SetTip(header, $"{post.SourceSite} #{post.Id}");
        return header;
    }

    private static string BuildPostKey(ImagePost post)
    {
        return $"{post.SourceSite.Trim().ToLowerInvariant()}::{post.Id.Trim()}";
    }

    private IReadOnlyList<string> CollectSelectedTags()
    {
        var selected = new List<string>();
        foreach (var listBox in _tagLists)
        {
            var selectedItems = listBox.SelectedItems;
            if (selectedItems is null)
            {
                continue;
            }

            foreach (var tag in selectedItems.OfType<string>())
            {
                if (string.IsNullOrWhiteSpace(tag) || selected.Contains(tag, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                selected.Add(tag);
            }
        }

        return selected;
    }

    private static List<KeyValuePair<string, List<string>>> BuildGroupedTags(ImagePost post)
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        if (post.TagGroups.Count > 0)
        {
            foreach (var (name, tags) in post.TagGroups)
            {
                if (string.IsNullOrWhiteSpace(name) || tags is null || tags.Count == 0)
                {
                    continue;
                }

                groups[name] = tags
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        if (groups.Count == 0 && !string.IsNullOrWhiteSpace(post.Tags))
        {
            groups["General"] = post.Tags
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return groups
            .Where(kv => kv.Value.Count > 0)
            .OrderBy(kv => GetGroupOrder(kv.Key))
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int GetGroupOrder(string group)
    {
        return group.Trim().ToLowerInvariant() switch
        {
            "artist" => 0,
            "character" => 1,
            "copyright" => 2,
            "species" => 3,
            "general" => 4,
            "meta" => 5,
            "lore" => 6,
            "invalid" => 7,
            _ => 100
        };
    }
}
