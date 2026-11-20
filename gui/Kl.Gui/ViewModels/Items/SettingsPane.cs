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

public enum SettingsPane
{
    General,
    Notifications,
    Import,
    Appearance,
    Archived,
    Storage,
    Config,
    Personalization,
    Shortcuts,
    Providers,
    Usage,
    Plugins,
    Passwords,
    CloudPc,
    Hooks,
    Connections,
    CodeReview,
    Git,
    Environments,
    Worktrees,
    Memory,
    Models,
}
