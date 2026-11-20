namespace Kl.Gui.ViewModels;

/// <summary>One KL Core audit record rendered in the audit settings pane.</summary>
public sealed record AuditRow(
    string Time,
    string Tool,
    string Action,
    string Decision,
    string Session,
    string Resource,
    string Error,
    bool Ok)
{
    public string OkLabel => Ok ? "成功" : "失败";
    public string Summary => $"{Tool} · {Action} · {Decision}";
    public bool HasResource => Resource.Length > 0;
    public bool HasError => Error.Length > 0;
}
