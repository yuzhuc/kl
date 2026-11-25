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

/// <summary>One Library row. The GUI keeps these locally; Core owns session data.</summary>
public sealed class LibraryItemViewModel
{
    public LibraryItemViewModel(LibraryItemState state) => State = state;

    public LibraryItemState State { get; }
    public string Content => State.Content;
    public string Title => string.IsNullOrWhiteSpace(State.Title) ? FirstLine(State.Content) : State.Title;
    public string Preview => State.Content.Length <= 180 ? State.Content : State.Content[..180] + "…";
    public string CreatedLabel => State.CreatedAt.ToLocalTime().ToString("MM-dd");

    private static string FirstLine(string text)
    {
        var line = text.Split('\n').FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
        if (string.IsNullOrEmpty(line))
        {
            return Loc.T("未命名条目");
        }

        return line.Length <= 32 ? line : line[..32] + "…";
    }
}
