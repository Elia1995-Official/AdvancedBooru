namespace BooruManager.Models;

public sealed class ResultSortOption
{
    public ResultSortOption(string key, string label)
    {
        Key = key;
        Label = label;
    }

    public string Key { get; }
    public string Label { get; }

    public override string ToString() => Label;
}
