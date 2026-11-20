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

namespace Kl.Gui.ViewModels;

/// <summary>One matching line under a file group in the palette.</summary>
public sealed class PaletteLineViewModel
{
    public PaletteLineViewModel(string relativePath, PaletteLineMatch match, string query = "")
    {
        RelativePath = relativePath;
        LineNumber = match.LineNumber;
        Text = match.Text;
        Query = query;
    }

    public string RelativePath { get; }
    public string Query { get; }
    public int LineNumber { get; }
    public string Text { get; }
    public string LineLabel => LineNumber.ToString();
}
