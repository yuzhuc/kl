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

/// <summary>One finding rendered on the code-review settings page.</summary>
public sealed record ReviewFindingRow(
    string Severity,
    string Location,
    string Rule,
    string Evidence,
    string Recommendation)
{
    public bool IsHigh => Severity is "critical" or "high";
}
