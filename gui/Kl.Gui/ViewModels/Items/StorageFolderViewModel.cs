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

/// <summary>One file group in the palette (name matches, or content matches).</summary>
/// <summary>One home subdirectory row in the 存储空间 settings pane.</summary>
public sealed class StorageFolderViewModel
{
    public StorageFolderViewModel(string name, string path, long bytes)
    {
        Name = name;
        Path = path;
        Bytes = bytes;
    }

    public string Name { get; }
    public string Path { get; }
    public long Bytes { get; }
    public string SizeLabel => FormatBytes(Bytes);

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}
