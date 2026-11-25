using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Kl.Client;
using Kl.Gui.Models;
using Kl.Gui.Services;
using Loc = Kl.Gui.Services.Localization;

namespace Kl.Gui.ViewModels;

public sealed class PaletteFileGroupViewModel
{
    public PaletteFileGroupViewModel(
        string relativePath,
        IReadOnlyList<PaletteLineMatch> lines,
        string query = "")
    {
        RelativePath = relativePath;
        Query = query;
        Lines = lines.Select(line => new PaletteLineViewModel(relativePath, line, query)).ToList();
    }

    public string RelativePath { get; }
    public string Query { get; }
    public string Name => Path.GetFileName(RelativePath);
    public string Folder
    {
        get
        {
            var folder = Path.GetDirectoryName(RelativePath)?.Replace('\\', '/');
            return string.IsNullOrEmpty(folder) ? Loc.T("根目录") : folder;
        }
    }

    public IReadOnlyList<PaletteLineViewModel> Lines { get; }
    public string CountLabel => Lines.Count > 0 ? Loc.F("{0} 处", (Lines.Count)) : string.Empty;
}
