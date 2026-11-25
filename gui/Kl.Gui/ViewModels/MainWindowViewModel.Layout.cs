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

public sealed partial class MainWindowViewModel : ObservableObject
{
    private bool _isCreateProjectDialogOpen;
    private string _projectNameDraft = string.Empty;
    private string _projectSourcePath = string.Empty;

    public bool IsCreateProjectDialogOpen
    {
        get => _isCreateProjectDialogOpen;
        private set
        {
            if (SetProperty(ref _isCreateProjectDialogOpen, value))
            {
                OnPropertyChanged(nameof(IsModalDialogOpen));
            }
        }
    }

    public string ProjectNameDraft
    {
        get => _projectNameDraft;
        set
        {
            if (SetProperty(ref _projectNameDraft, value))
            {
                OnPropertyChanged(nameof(CanCreateProject));
            }
        }
    }

    public string ProjectSourcePath
    {
        get => _projectSourcePath;
        set
        {
            if (SetProperty(ref _projectSourcePath, value))
            {
                OnPropertyChanged(nameof(CanCreateProject));
                OnPropertyChanged(nameof(HasProjectSourcePath));
            }
        }
    }

    public bool HasProjectSourcePath => !string.IsNullOrWhiteSpace(ProjectSourcePath);
    public bool CanCreateProject => !string.IsNullOrWhiteSpace(ProjectNameDraft) && HasProjectSourcePath;

    public void OpenCreateProjectDialog()
    {
        ProjectNameDraft = string.Empty;
        ProjectSourcePath = string.Empty;
        IsCreateProjectDialogOpen = true;
    }

    public void CloseCreateProjectDialog() => IsCreateProjectDialogOpen = false;

    public void SetProjectSourcePath(string path)
    {
        ProjectSourcePath = path;
        if (string.IsNullOrWhiteSpace(ProjectNameDraft))
        {
            var name = WorkspaceNameFromPath(path);
            if (!string.IsNullOrWhiteSpace(name))
            {
                ProjectNameDraft = name;
            }
        }
    }

    public bool CreateProjectFromDraft()
    {
        var name = ProjectNameDraft.Trim();
        var path = ProjectSourcePath.Trim();
        if (name.Length == 0 || path.Length == 0 || path.Any(char.IsControl))
        {
            return false;
        }
        if (!IsRemoteCore && !Directory.Exists(path))
        {
            ErrorMessage = Loc.T("项目目录不存在。");
            return false;
        }

        if (Workspaces.Any(workspace => string.Equals(workspace.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var state = new WorkspaceState
        {
            Name = name,
            Path = path,
            IsExpanded = IsWorkMode
        };
        _state.Workspaces.Add(state);
        var item = new WorkspaceItemViewModel(state);
        Workspaces.Add(item);
        RefreshWorkspacePathLabels();
        _state.ActiveWorkspaceId = state.Id;
        foreach (var workspace in Workspaces)
        {
            workspace.IsSelected = ReferenceEquals(workspace, item);
        }

        CloseCreateProjectDialog();
        SaveState();
        _ = InitializeWorkspaceAsync(path);
        return true;
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public ObservableCollection<string> SettingsModelChoices { get; } = [];
    public ObservableCollection<string> DoctorChecks { get; } = [];

    public string SettingsSelectedModel
    {
        get => _settingsSelectedModel;
        set => SetProperty(ref _settingsSelectedModel, value);
    }

    public string SettingsApiKeyDraft
    {
        get => _settingsApiKeyDraft;
        set => SetProperty(ref _settingsApiKeyDraft, value);
    }

    public bool HasSettingsStatus => !string.IsNullOrWhiteSpace(SettingsStatus);
    public bool IsSettingsStatusError => _settingsStatusIsError;
    public bool IsSettingsStatusSuccess => HasSettingsStatus && !_settingsStatusIsError;
    public string TestConnectionButtonText => IsTestingConnection ? Loc.T("测试中…") : Loc.T("测试连接");
    public bool IsTestingConnection => _isTestingConnection;
    public bool CanTestConnection => !IsTestingConnection;
    public string StateFilePath => _stateStore.Path;
    public string StateDirectory => Path.GetDirectoryName(_stateStore.Path) ?? string.Empty;
    public int WorkspaceCount => Workspaces.Count;
    public int SavedSessionCount => Workspaces.Sum(workspace => workspace.Sessions.Count);
    public bool HasAutomations => Automations.Count > 0;
    public bool ShowNoAutomations => !Automations.Any(item => item.IsVisible);
    public string AutomationSearch
    {
        get => _automationSearch;
        set
        {
            if (SetProperty(ref _automationSearch, value))
            {
                ApplyAutomationFilter();
            }
        }
    }
    public string AutomationPageSummary
    {
        get
        {
            if (Automations.Count == 0)
            {
                return Loc.T("尚未创建自动化任务。");
            }
            var enabled = Automations.Count(item => item.IsEnabled);
            return Loc.F("{0} 个任务，{1} 个已启用。", (Automations.Count), (enabled));
        }
    }

    public bool IsAutomationDialogOpen
    {
        get => _isAutomationDialogOpen;
        private set
        {
            if (SetProperty(ref _isAutomationDialogOpen, value))
            {
                OnPropertyChanged(nameof(IsModalDialogOpen));
            }
        }
    }

    public bool IsAutomationDeleteDialogOpen
    {
        get => _isAutomationDeleteDialogOpen;
        private set
        {
            if (SetProperty(ref _isAutomationDeleteDialogOpen, value))
            {
                OnPropertyChanged(nameof(IsModalDialogOpen));
            }
        }
    }

    public string AutomationDialogTitle => _automationDialogTarget is null ? Loc.T("新建自动化任务") : Loc.T("编辑自动化任务");

    public string AutomationName
    {
        get => _automationName;
        set => SetProperty(ref _automationName, value);
    }

    public string AutomationPrompt
    {
        get => _automationPrompt;
        set => SetProperty(ref _automationPrompt, value);
    }

    public AutomationIntervalOption? SelectedAutomationInterval
    {
        get => _selectedAutomationInterval;
        set => SetProperty(ref _selectedAutomationInterval, value);
    }

    public string AutomationDialogStatus
    {
        get => _automationDialogStatus;
        private set
        {
            if (SetProperty(ref _automationDialogStatus, value))
            {
                OnPropertyChanged(nameof(HasAutomationDialogStatus));
            }
        }
    }

    public bool HasAutomationDialogStatus => !string.IsNullOrWhiteSpace(AutomationDialogStatus);

    public string AutomationDeletePrompt
    {
        get => _automationDeletePrompt;
        private set => SetProperty(ref _automationDeletePrompt, value);
    }

    public bool HasPlugins => Plugins.Count > 0;
    public bool ShowNoPlugins => _allPlugins.Count == 0;
    public bool ShowNoPluginMatches =>
        _pluginSearch.Trim().Length > 0 && Plugins.Count == 0 && _allPlugins.Count > 0;
    public string PluginSearch
    {
        get => _pluginSearch;
        set
        {
            if (SetProperty(ref _pluginSearch, value))
            {
                ApplyPluginFilter();
            }
        }
    }
    public string PluginDirectoryPath => _pluginCatalogService.DirectoryPath;
    public string PluginStatus
    {
        get => _pluginStatus;
        private set => SetProperty(ref _pluginStatus, value);
    }
    public bool IsWorkMode => _state.InteractionMode != "chat";
    public bool IsChatMode => !IsWorkMode;

    /// <summary>The desktop app's home composer is narrower than in a conversation.</summary>
    public double ComposerMaxWidth => IsChatMode && IsEmpty ? 640 : 734;

    /// <summary>The desktop home floats the composer under the headline instead of pinning it to the bottom.</summary>
    public Thickness ComposerMargin => IsChatMode && IsEmpty
        ? new Thickness(1, 0, 0, 326)
        : new Thickness(1, 0, 0, 20);

    /// <summary>Codex Work keeps the workspace strip above the composer.</summary>
    public bool ShowWorkspaceStrip => IsEmpty && IsWorkMode;

    /// <summary>Chat mode puts project/plugin chips below the composer instead.</summary>
    public bool ShowChatComposerChips => IsEmpty && IsChatMode;
    public bool HasSessions => Workspaces.Any(workspace => workspace.Sessions.Count > 0);
    public bool ShowNoSessions => !HasSessions && string.IsNullOrWhiteSpace(SearchText);
    public bool ShowNoSearchResults => !string.IsNullOrWhiteSpace(SearchText) && !Workspaces.Any(workspace => workspace.IsVisible);

    public string SessionTitle
    {
        get => _sessionTitle;
        private set
        {
            if (SetProperty(ref _sessionTitle, value))
            {
                OnPropertyChanged(nameof(HeaderTitle));
            }
        }
    }
    public string HeaderTitle => IsChatPage ? SessionTitle : SectionTitle;
    public string SectionTitle => IsLibraryPage ? Loc.T("资料库") : IsProjectsPage ? Loc.T("项目") : IsPluginsPage ? (IsSkillsList ? Loc.T("技能") : Loc.T("插件")) : IsAutomationPage ? Loc.T("自动化任务") : IsSettingsPage ? Loc.T("设置") : Loc.T("聊天");
    public string WorkspaceName => _activeWorkspace.Name;
    public string WorkspacePath => _activeWorkspace.Path;
    public string ModelName => _core.IsSessionOpen
        ? _core.Model
        : string.IsNullOrWhiteSpace(CoreCatalogModel) ? _state.Model : CoreCatalogModel;

    /// <summary>Codex-style provider/model label for the composer picker.</summary>
    public string ProviderModelLabel
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_composerModelLabel))
            {
                return _composerModelLabel;
            }

            var provider = _core.IsSessionOpen ? _core.Provider : CoreCatalogProvider;
            var model = _core.IsSessionOpen ? _core.Model : CoreCatalogModel;
            if (string.IsNullOrWhiteSpace(model))
            {
                return string.IsNullOrWhiteSpace(_state.Model) ? Loc.T("未连接") : _state.Model;
            }

            if (string.IsNullOrWhiteSpace(provider))
            {
                return PrettifyWords(model);
            }

            var normalizedProvider = PrettifyWords(provider);
            return model.StartsWith(provider, StringComparison.OrdinalIgnoreCase)
                ? PrettifyWords(model)
                : $"{normalizedProvider} {PrettifyWords(model)}";
        }
    }

    private static string PrettifyWords(string value)
    {
        var parts = value
            .Replace('_', ' ')
            .Replace('-', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return value;
        }

        var builder = new System.Text.StringBuilder();
        foreach (var part in parts)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }
            builder.Append(char.ToUpperInvariant(part[0]));
            if (part.Length > 1)
            {
                builder.Append(part[1..]);
            }
        }
        return builder.ToString();
    }
}
