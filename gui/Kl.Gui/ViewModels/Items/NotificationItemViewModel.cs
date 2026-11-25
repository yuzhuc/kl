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

/// <summary>One row in the Codex-style notifications pane.</summary>
public sealed class NotificationItemViewModel
{
    public NotificationItemViewModel(SessionItemViewModel session, string groupLabel, bool showGroup)
    {
        Session = session;
        GroupLabel = groupLabel;
        ShowGroup = showGroup;
    }

    public SessionItemViewModel Session { get; }
    public string GroupLabel { get; }
    public bool ShowGroup { get; }
    public string Title => Session.Title;
    public string WorkspaceName => Session.Workspace.Name;
    public string RelativeLabel => Session.RelativeLabel;
    public string Preview => Session.State.Messages.LastOrDefault()?.Text ?? string.Empty;
}
