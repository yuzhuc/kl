namespace Kl.Gui.ViewModels;

/// <summary>One selectable model in the composer's model picker.</summary>
public sealed class ComposerModelRow : ObservableObject
{
    private bool _isSelected;

    public ComposerModelRow(string label, string choice)
    {
        Label = label;
        Choice = choice;
    }

    public string Label { get; }
    public string Choice { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
