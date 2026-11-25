using Loc = Kl.Gui.Services.Localization;
namespace Kl.Gui.ViewModels;

public sealed class AttachmentItemViewModel
{
    public AttachmentItemViewModel(string path, string name, string content)
    {
        Path = path;
        Name = name;
        Content = content;
    }

    public string Path { get; }
    public string Name { get; }
    public string Content { get; }
    public string SizeLabel => Content.Length >= 1000
        ? Loc.F("{0:0.#}k 字符", (Content.Length / 1000.0))
        : Loc.F("{0} 字符", (Content.Length));
}
