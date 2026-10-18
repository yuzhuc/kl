namespace Kl.Cli;

public enum HistoryFilter
{
    All,
    NeedsYou,
    Working,
    Ready,
    Inactive
}

public enum HistoryTaskStatus
{
    NeedsYou,
    Working,
    Ready,
    Inactive
}

public sealed record HistoryTask(HistorySession Session, HistoryTaskStatus Status);

public sealed record HistoryGroup(string Workspace, IReadOnlyList<HistoryTask> Tasks);

public sealed class HistoryView
{
    private static readonly HistoryFilter[] FilterOrder =
    [
        HistoryFilter.All,
        HistoryFilter.NeedsYou,
        HistoryFilter.Working,
        HistoryFilter.Ready,
        HistoryFilter.Inactive
    ];

    private readonly Dictionary<HistoryFilter, int> _counts;

    private HistoryView(
        HistoryFilter filter,
        string search,
        IReadOnlyList<HistoryGroup> groups,
        Dictionary<HistoryFilter, int> counts)
    {
        Filter = filter;
        Search = search;
        Groups = groups;
        Tasks = groups.SelectMany(group => group.Tasks).ToList();
        _counts = counts;
    }

    public HistoryFilter Filter { get; }

    public string Search { get; }

    public IReadOnlyList<HistoryGroup> Groups { get; }

    public IReadOnlyList<HistoryTask> Tasks { get; }

    public int Count(HistoryFilter filter) => _counts[filter];

    public HistoryTask? Selected(int selection) =>
        Tasks.Count == 0
            ? null
            : Tasks[Math.Clamp(selection, 0, Tasks.Count - 1)];

    public static HistoryView Build(
        IEnumerable<HistorySession> sessions,
        HistoryFilter filter,
        string? search,
        string currentSessionId,
        bool hasPendingApproval,
        bool isTurnActive)
    {
        var tasks = sessions
            .Select(session => new HistoryTask(
                session,
                ResolveStatus(
                    session,
                    currentSessionId,
                    hasPendingApproval,
                    isTurnActive)))
            .ToList();

        var counts = FilterOrder.ToDictionary(
            item => item,
            item => item == HistoryFilter.All
                ? tasks.Count
                : tasks.Count(task => task.Status == ToStatus(item)));

        var query = search?.Trim() ?? string.Empty;
        var groups = tasks
            .Where(task => filter == HistoryFilter.All || task.Status == ToStatus(filter))
            .Where(task => Matches(task.Session, query))
            .GroupBy(task => task.Session.Workspace)
            .Select(group => new HistoryGroup(group.Key, group.ToList()))
            .ToList();

        return new HistoryView(filter, query, groups, counts);
    }

    public static HistoryFilter MoveFilter(HistoryFilter filter, int delta)
    {
        var index = Array.IndexOf(FilterOrder, filter);
        var next = (index + delta + FilterOrder.Length) % FilterOrder.Length;
        return FilterOrder[next];
    }

    public static int MoveSelection(int selection, int delta, int count) =>
        count == 0 ? 0 : Math.Clamp(selection + delta, 0, count - 1);

    private static HistoryTaskStatus ResolveStatus(
        HistorySession session,
        string currentSessionId,
        bool hasPendingApproval,
        bool isTurnActive)
    {
        if (!string.IsNullOrEmpty(currentSessionId)
            && session.SessionId == currentSessionId)
        {
            if (hasPendingApproval)
            {
                return HistoryTaskStatus.NeedsYou;
            }
            if (isTurnActive)
            {
                return HistoryTaskStatus.Working;
            }
        }

        if (string.IsNullOrWhiteSpace(session.Status)
            || session.Status.Trim().Equals("ready", StringComparison.OrdinalIgnoreCase))
        {
            return HistoryTaskStatus.Ready;
        }
        return HistoryTaskStatus.Inactive;
    }

    private static HistoryTaskStatus ToStatus(HistoryFilter filter) =>
        filter switch
        {
            HistoryFilter.NeedsYou => HistoryTaskStatus.NeedsYou,
            HistoryFilter.Working => HistoryTaskStatus.Working,
            HistoryFilter.Ready => HistoryTaskStatus.Ready,
            HistoryFilter.Inactive => HistoryTaskStatus.Inactive,
            _ => HistoryTaskStatus.Ready
        };

    private static bool Matches(HistorySession session, string query)
    {
        if (query.Length == 0)
        {
            return true;
        }

        var providerModel = string.Join(
            '/',
            new[] { session.Provider, session.Model }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        return new[]
        {
            session.Task,
            session.Prompt,
            session.LastMessage,
            session.Workspace,
            session.Provider,
            session.Model,
            providerModel
        }.Any(value => value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true);
    }
}