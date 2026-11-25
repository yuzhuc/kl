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

/// <summary>One discovered import item with the user's selection and result.</summary>
public sealed class ImportCandidateRow : ObservableObject
{
    private readonly Action _selectionChanged;
    private bool _isSelected = true;
    private string _status = string.Empty;

    public ImportCandidateRow(ImportCandidate candidate, Action selectionChanged)
    {
        Candidate = candidate;
        _selectionChanged = selectionChanged;
    }

    public ImportCandidate Candidate { get; }
    public string Name => Candidate.Name;
    public string Description => Candidate.Description;
    public string SourcePath => Candidate.SourcePath;
    public string DestinationPath => Candidate.DestinationPath;
    public string SizeLabel => Candidate.SizeLabel;
    public string KindLabel => Candidate.Kind == ImportKind.Instructions ? Loc.T("说明文件") : Loc.T("技能");

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                _selectionChanged();
            }
        }
    }

    public string Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(HasStatus));
            }
        }
    }

    public bool HasStatus => _status.Length > 0;
}
