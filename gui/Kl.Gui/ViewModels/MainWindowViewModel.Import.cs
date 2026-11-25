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
    public async Task DiscoverImportsAsync()
    {
        if (IsRemoteCore)
        {
            ImportStatusText = RemoteDisabled(Loc.T("导入扫描"));
            return;
        }
        if (IsImportScanning)
        {
            return;
        }

        IsImportScanning = true;
        ImportStatusText = Loc.T("正在扫描 Codex 和 Claude 的安全配置项…");
        try
        {
            var service = _importService;
            var candidates = await Task.Run(service.Discover).ConfigureAwait(true);
            ImportCandidates.Clear();
            foreach (var candidate in candidates)
            {
                ImportCandidates.Add(new ImportCandidateRow(candidate, NotifyImportSelection));
            }

            ImportStatusText = candidates.Count == 0
                ? Loc.T("没有找到可导入项。")
                : Loc.F("找到 {0} 个可导入项。", (candidates.Count));
        }
        catch (Exception error)
        {
            ImportStatusText = Loc.F("扫描失败：{0}", (error.Message));
        }
        finally
        {
            IsImportScanning = false;
            OnPropertyChanged(nameof(HasImportCandidates));
            OnPropertyChanged(nameof(CanImportSelected));
        }
    }

    public async Task ImportSelectedAsync()
    {
        if (IsRemoteCore)
        {
            ImportStatusText = RemoteDisabled(Loc.T("导入"));
            return;
        }
        var selected = ImportCandidates.Where(item => item.IsSelected).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        IsImportScanning = true;
        var imported = 0;
        try
        {
            foreach (var row in selected)
            {
                row.Status = Loc.T("导入中…");
                var service = _importService;
                var candidate = row.Candidate;
                var result = await Task.Run(() => service.Import(candidate)).ConfigureAwait(true);
                row.Status = result.Message;
                if (result.Success)
                {
                    imported++;
                }
            }

            ImportStatusText = Loc.F("已处理 {0} 项，成功 {1} 项。", (selected.Count), (imported));
        }
        catch (Exception error)
        {
            ImportStatusText = Loc.F("导入失败：{0}", (error.Message));
        }
        finally
        {
            IsImportScanning = false;
            OnPropertyChanged(nameof(CanImportSelected));
        }
    }

    public void RefreshSettingsDraft()
    {
        SettingsPermissionMode = _state.PermissionMode;
        SettingsContentFontSize = _state.ContentFontSize;
        SetSettingsStatus(string.Empty, isError: false);
    }

    public string DoctorSummary
    {
        get => _doctorSummary;
        private set
        {
            if (SetProperty(ref _doctorSummary, value))
            {
                OnPropertyChanged(nameof(HasDoctorSummary));
            }
        }
    }

    public bool HasDoctorSummary => _doctorSummary.Length > 0;
    public bool HasDoctorChecks => DoctorChecks.Count > 0;

    public bool IsDoctorRunning
    {
        get => _isDoctorRunning;
        private set
        {
            if (SetProperty(ref _isDoctorRunning, value))
            {
                OnPropertyChanged(nameof(CanRunDoctor));
            }
        }
    }

    public bool CanRunDoctor => !IsDoctorRunning;

    public string LibraryDraftTitle
    {
        get => _libraryDraftTitle;
        set => SetProperty(ref _libraryDraftTitle, value);
    }

    public string LibraryDraftContent
    {
        get => _libraryDraftContent;
        set => SetProperty(ref _libraryDraftContent, value);
    }

    public bool HasLibraryItems => LibraryItems.Count > 0;

    /// <summary>The first four saved prompts, offered as chips on the empty home screen.</summary>
    private void RefreshHomeSuggestions()
    {
        HomeSuggestions.Clear();
        foreach (var item in LibraryItems.Take(4))
        {
            HomeSuggestions.Add(item);
        }
        OnPropertyChanged(nameof(HasHomeSuggestions));
    }

    public string LibraryEmptyText =>
        Loc.T("资料库还没有条目。保存常用提示词或规范，之后可一键插入输入框。");

    /// <summary>Saves the draft as a GUI-local library entry.</summary>
    public void SaveLibraryItem()
    {
        var content = LibraryDraftContent.Trim();
        if (content.Length == 0)
        {
            return;
        }

        var state = new LibraryItemState
        {
            Title = LibraryDraftTitle.Trim(),
            Content = content
        };
        _state.LibraryItems.Insert(0, state);
        LibraryItems.Insert(0, new LibraryItemViewModel(state));
        LibraryDraftTitle = string.Empty;
        LibraryDraftContent = string.Empty;
        OnPropertyChanged(nameof(HasLibraryItems));
        RefreshHomeSuggestions();
        SaveState();
    }

    public void DeleteLibraryItem(LibraryItemViewModel item)
    {
        _state.LibraryItems.Remove(item.State);
        LibraryItems.Remove(item);
        OnPropertyChanged(nameof(HasLibraryItems));
        RefreshHomeSuggestions();
        SaveState();
    }

    /// <summary>Copies an entry into the composer and goes back to the chat draft.</summary>
    public void InsertLibraryItem(LibraryItemViewModel item)
    {
        Composer = string.IsNullOrWhiteSpace(Composer)
            ? item.Content
            : Composer + "\n" + item.Content;
        Navigate(AppSection.Chat);
    }

    public void BeginCreateAutomation()
    {
        _automationDialogTarget = null;
        AutomationName = string.Empty;
        AutomationPrompt = string.Empty;
        SelectedAutomationInterval = AutomationIntervals[1];
        AutomationDialogStatus = string.Empty;
        IsAutomationDialogOpen = true;
        OnPropertyChanged(nameof(AutomationDialogTitle));
    }

    public void BeginEditAutomation(AutomationItemViewModel automation)
    {
        _automationDialogTarget = automation;
        AutomationName = automation.Name;
        AutomationPrompt = automation.Prompt;
        SelectedAutomationInterval = AutomationIntervals.FirstOrDefault(
            option => option.Minutes == automation.IntervalMinutes) ?? AutomationIntervals[1];
        AutomationDialogStatus = string.Empty;
        IsAutomationDialogOpen = true;
        OnPropertyChanged(nameof(AutomationDialogTitle));
    }

    public void SaveAutomation()
    {
        var name = AutomationName.Trim();
        var prompt = AutomationPrompt.Trim();
        if (name.Length == 0)
        {
            AutomationDialogStatus = Loc.T("任务名称不能为空。");
            return;
        }
        if (prompt.Length == 0)
        {
            AutomationDialogStatus = Loc.T("任务指令不能为空。");
            return;
        }
        if (SelectedAutomationInterval is null)
        {
            AutomationDialogStatus = Loc.T("请选择运行间隔。");
            return;
        }

        if (name.Length > 80)
        {
            name = name[..80];
        }

        if (_automationDialogTarget is null)
        {
            var state = new AutomationState
            {
                Name = name,
                Prompt = prompt,
                IntervalMinutes = SelectedAutomationInterval.Minutes,
                IsEnabled = false,
                CreatedAt = DateTimeOffset.Now
            };
            _state.Automations.Add(state);
            Automations.Insert(0, new AutomationItemViewModel(state));
        }
        else
        {
            _automationDialogTarget.State.Name = name;
            _automationDialogTarget.State.Prompt = prompt;
            _automationDialogTarget.State.IntervalMinutes = SelectedAutomationInterval.Minutes;
            _automationDialogTarget.NotifyStateChanged();
        }

        CloseAutomationDialogs();
        SaveState();
        NotifyAutomationCollectionChanged();
    }

    public void CloseAutomationDialogs()
    {
        IsAutomationDialogOpen = false;
        IsAutomationDeleteDialogOpen = false;
        _automationDialogTarget = null;
        _automationDeleteTarget = null;
        AutomationDialogStatus = string.Empty;
        AutomationDeletePrompt = string.Empty;
    }

    public void ToggleAutomation(AutomationItemViewModel automation)
    {
        if (automation.IsRunning)
        {
            return;
        }
        automation.State.IsEnabled = !automation.State.IsEnabled;
        automation.State.NextRunAt = automation.State.IsEnabled
            ? DateTimeOffset.Now.AddMinutes(automation.IntervalMinutes)
            : null;
        automation.NotifyStateChanged();
        SaveState();
        NotifyAutomationCollectionChanged();
    }

    public void BeginDeleteAutomation(AutomationItemViewModel automation)
    {
        _automationDeleteTarget = automation;
        AutomationDeletePrompt = Loc.F("删除自动化“{0}”？历史运行结果也会一起删除。", (automation.Name));
        IsAutomationDeleteDialogOpen = true;
    }

    public void ConfirmDeleteAutomation()
    {
        if (_automationDeleteTarget is null)
        {
            return;
        }
        _state.Automations.Remove(_automationDeleteTarget.State);
        Automations.Remove(_automationDeleteTarget);
        CloseAutomationDialogs();
        SaveState();
        NotifyAutomationCollectionChanged();
    }
}
