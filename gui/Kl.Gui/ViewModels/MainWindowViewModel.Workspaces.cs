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

public sealed partial class MainWindowViewModel : ObservableObject
{
    public string RemoteCoreConnectionLabel => string.IsNullOrWhiteSpace(_state.RemoteCoreEndpoint)
        ? "本机 Core（未配置远程）"
        : _state.RemoteCoreEndpoint.Trim();

    public string CredentialsDirectory => Path.Combine(_homeDirectory, "credentials");
    public string HooksFilePath => Path.Combine(_homeDirectory, "hooks.toml");

    private string _gitBranchText = "—";
    private string _gitStatusText = "打开此页时读取当前项目的 Git 状态。";
    public string GitBranchText
    {
        get => _gitBranchText;
        private set => SetProperty(ref _gitBranchText, value);
    }

    public string GitStatusText
    {
        get => _gitStatusText;
        private set => SetProperty(ref _gitStatusText, value);
    }

    /// <summary>Reads the active workspace's branch and dirty count with the git CLI.</summary>
    public async Task RefreshGitAsync()
    {
        var workspace = WorkspacePath;
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
        {
            GitBranchText = "—";
            GitStatusText = "当前项目路径不可用。";
            return;
        }

        var (isRepo, branch, status, log) = await Task.Run(() =>
        {
            var branch = RunGit(workspace, "symbolic-ref --short HEAD")
                ?? RunGit(workspace, "rev-parse --short HEAD");
            if (branch is null)
            {
                return (false, string.Empty, "当前项目不是 Git 仓库。", string.Empty);
            }

            var changes = RunGit(workspace, "status --porcelain");
            var count = string.IsNullOrWhiteSpace(changes)
                ? 0
                : changes.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            var log = RunGit(workspace, "log -1 --pretty=%h %s") ?? string.Empty;
            return (true, branch.Trim(), count == 0 ? "工作区干净" : $"{count} 个未提交更改", log.Trim());
        }).ConfigureAwait(true);

        GitBranchText = isRepo ? branch : "—";
        GitStatusText = isRepo
            ? $"{status}" + (log.Length > 0 ? $" · {log}" : string.Empty)
            : status;
    }

    public ObservableCollection<WorktreeRow> Worktrees { get; } = [];
    public bool HasWorktrees => Worktrees.Count > 0;
    public string WorktreesStatusText { get; private set; } = "打开此页时读取当前项目的 worktree。";

    /// <summary>Lists real git worktrees for the active project.</summary>
    public async Task RefreshWorktreesAsync()
    {
        var workspace = WorkspacePath;
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
        {
            Worktrees.Clear();
            WorktreesStatusText = "当前项目路径不可用。";
            NotifyWorktrees();
            return;
        }

        var output = await Task.Run(() => RunGit(workspace, "worktree list --porcelain")).ConfigureAwait(true);
        Worktrees.Clear();
        if (output is null)
        {
            WorktreesStatusText = "当前项目不是 Git 仓库。";
        }
        else
        {
            foreach (var row in ParseWorktrees(output, workspace))
            {
                Worktrees.Add(row);
            }
            WorktreesStatusText = Worktrees.Count == 0 ? "Git 没有报告 worktree。" : $"共 {Worktrees.Count} 个工作树";
        }
        NotifyWorktrees();
    }

    private void NotifyWorktrees()
    {
        OnPropertyChanged(nameof(HasWorktrees));
        OnPropertyChanged(nameof(WorktreesStatusText));
    }

    public string WorktreeRoot
    {
        get => _state.WorktreeRoot;
        set
        {
            var root = value?.Trim() ?? string.Empty;
            if (_state.WorktreeRoot == root)
            {
                return;
            }
            _state.WorktreeRoot = root;
            SaveState();
            OnPropertyChanged();
        }
    }

    public string GitBranchPrefix
    {
        get => _state.GitBranchPrefix;
        set
        {
            var prefix = value?.Trim() ?? string.Empty;
            if (_state.GitBranchPrefix == prefix)
            {
                return;
            }
            _state.GitBranchPrefix = prefix;
            SaveState();
            OnPropertyChanged();
        }
    }

    /// <summary>Shown as the placeholder when the root is left blank.</summary>
    public string DefaultWorktreeRoot => string.IsNullOrWhiteSpace(WorkspacePath)
        ? ".kl/worktrees"
        : Path.Combine(WorkspacePath, ".kl", "worktrees");

    public bool WorktreeFetchBeforeCreate
    {
        get => _state.WorktreeFetchBeforeCreate;
        set
        {
            if (_state.WorktreeFetchBeforeCreate == value)
            {
                return;
            }
            _state.WorktreeFetchBeforeCreate = value;
            SaveState();
            OnPropertyChanged();
        }
    }

    public bool WorktreeConfirmDelete
    {
        get => _state.WorktreeConfirmDelete;
        set
        {
            if (_state.WorktreeConfirmDelete == value)
            {
                return;
            }
            _state.WorktreeConfirmDelete = value;
            SaveState();
            OnPropertyChanged();
        }
    }

    private string _newWorktreeName = string.Empty;
    public string NewWorktreeName
    {
        get => _newWorktreeName;
        set
        {
            if (SetProperty(ref _newWorktreeName, value))
            {
                OnPropertyChanged(nameof(CanCreateWorktree));
            }
        }
    }

    public bool CanCreateWorktree => IsValidWorktreeName(_newWorktreeName.Trim());

    private static string ExpandHome(string path)
    {
        var trimmed = path.Trim();
        if (trimmed == "~")
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        if (trimmed.StartsWith("~/", StringComparison.Ordinal)
            || trimmed.StartsWith("~\\", StringComparison.Ordinal))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                trimmed[2..]);
        }
        return Environment.ExpandEnvironmentVariables(trimmed);
    }

    private static bool IsValidWorktreeName(string name) =>
        name.Length > 0
        && name != "."
        && name != ".."
        && name.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static bool IsValidBranchPrefix(string prefix) =>
        prefix.Length <= 64
        && !prefix.Contains("..", StringComparison.Ordinal)
        && !prefix.StartsWith('/')
        && !prefix.StartsWith('.')
        && !prefix.StartsWith('-')
        && prefix.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '/' or '-' or '_' or '.');

    public async Task CreateWorktreeAsync()
    {
        var workspace = WorkspacePath;
        var name = _newWorktreeName.Trim();
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
        {
            WorktreesStatusText = "当前项目路径不可用。";
            NotifyWorktrees();
            return;
        }
        if (!IsValidWorktreeName(name))
        {
            WorktreesStatusText = "工作树名称只能包含字母、数字、点、下划线和短横线。";
            NotifyWorktrees();
            return;
        }

        var branchPrefix = _state.GitBranchPrefix.Trim();
        if (!IsValidBranchPrefix(branchPrefix))
        {
            WorktreesStatusText = "分支前缀包含非法字符，请检查 Git 设置。";
            NotifyWorktrees();
            return;
        }
        var branch = branchPrefix + name;

        var root = string.IsNullOrWhiteSpace(_state.WorktreeRoot)
            ? Path.Combine(workspace, ".kl", "worktrees")
            : ExpandHome(_state.WorktreeRoot);
        var path = Path.GetFullPath(Path.Combine(root, name));
        if (Directory.Exists(path) || File.Exists(path))
        {
            WorktreesStatusText = "目标工作树已存在。";
            NotifyWorktrees();
            return;
        }

        WorktreesStatusText = "正在创建工作树…";
        NotifyWorktrees();
        var fetch = _state.WorktreeFetchBeforeCreate;
        var (ok, output) = await Task.Run(() =>
        {
            if (fetch)
            {
                var fetched = RunGitArgs(workspace, "fetch", "--all", "--prune");
                if (!fetched.Ok)
                {
                    return fetched;
                }
            }
            try
            {
                Directory.CreateDirectory(root);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return (false, error.Message);
            }
            return RunGitArgs(workspace, "worktree", "add", "-b", branch, path, "HEAD");
        }).ConfigureAwait(true);

        if (ok)
        {
            _newWorktreeName = string.Empty;
            OnPropertyChanged(nameof(NewWorktreeName));
            OnPropertyChanged(nameof(CanCreateWorktree));
            await RefreshWorktreesAsync().ConfigureAwait(true);
            WorktreesStatusText = $"已创建工作树 {name}（分支 {branch}）。";
        }
        else
        {
            WorktreesStatusText = $"创建工作树失败：{output.Trim()}";
        }
        NotifyWorktrees();
    }

    private WorktreeRow? _pendingWorktreeDelete;
    public bool IsWorktreeDeleteConfirmOpen => _pendingWorktreeDelete is not null;
    public string WorktreeDeleteConfirmText => _pendingWorktreeDelete is null
        ? string.Empty
        : $"删除工作树 {_pendingWorktreeDelete.Path}？";

    public void RequestDeleteWorktree(WorktreeRow row)
    {
        if (!_state.WorktreeConfirmDelete)
        {
            _ = DeleteWorktreeAsync(row);
            return;
        }
        _pendingWorktreeDelete = row;
        OnPropertyChanged(nameof(IsWorktreeDeleteConfirmOpen));
        OnPropertyChanged(nameof(WorktreeDeleteConfirmText));
        OnPropertyChanged(nameof(IsModalDialogOpen));
    }

    public void CancelDeleteWorktree()
    {
        _pendingWorktreeDelete = null;
        OnPropertyChanged(nameof(IsWorktreeDeleteConfirmOpen));
        OnPropertyChanged(nameof(WorktreeDeleteConfirmText));
        OnPropertyChanged(nameof(IsModalDialogOpen));
    }

    public async Task ConfirmDeleteWorktreeAsync()
    {
        var row = _pendingWorktreeDelete;
        CancelDeleteWorktree();
        if (row is not null)
        {
            await DeleteWorktreeAsync(row).ConfigureAwait(true);
        }
    }

    private async Task DeleteWorktreeAsync(WorktreeRow row)
    {
        var workspace = WorkspacePath;
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
        {
            WorktreesStatusText = "当前项目路径不可用。";
            NotifyWorktrees();
            return;
        }

        WorktreesStatusText = "正在删除工作树…";
        NotifyWorktrees();
        var (ok, output) = await Task.Run(() => RunGitArgs(workspace, "worktree", "remove", row.Path))
            .ConfigureAwait(true);
        if (ok)
        {
            await RefreshWorktreesAsync().ConfigureAwait(true);
            WorktreesStatusText = "已删除工作树。";
        }
        else
        {
            WorktreesStatusText = $"删除工作树失败：{output.Trim()}";
        }
        NotifyWorktrees();
    }

    private static IEnumerable<WorktreeRow> ParseWorktrees(string output, string currentWorkspace)
    {
        var path = string.Empty;
        var branch = string.Empty;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                if (path.Length > 0)
                {
                    yield return BuildWorktreeRow(path, branch, currentWorkspace);
                }
                path = string.Empty;
                branch = string.Empty;
                continue;
            }

            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                path = line["worktree ".Length..];
            }
            else if (line.StartsWith("branch refs/heads/", StringComparison.Ordinal))
            {
                branch = line["branch refs/heads/".Length..];
            }
            else if (line == "detached")
            {
                branch = "detached";
            }
        }

        if (path.Length > 0)
        {
            yield return BuildWorktreeRow(path, branch, currentWorkspace);
        }
    }

    private static WorktreeRow BuildWorktreeRow(string path, string branch, string currentWorkspace) =>
        new(path,
            branch.Length == 0 ? "—" : branch,
            string.Equals(
                Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(currentWorkspace).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase)
                ? "当前"
                : string.Empty);

    private static (bool Ok, string Output) RunGitArgs(string workspace, params string[] arguments)
    {
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("-C");
            start.ArgumentList.Add(workspace);
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            using var process = System.Diagnostics.Process.Start(start);
            if (process is null)
            {
                return (false, "无法启动 git。");
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(15000);
            return process.ExitCode == 0
                ? (true, stdout)
                : (false, string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception
            or InvalidOperationException
            or IOException)
        {
            return (false, error.Message);
        }
    }

    private static string? RunGit(string workspace, string arguments)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = $"-C \"{workspace}\" {arguments}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
