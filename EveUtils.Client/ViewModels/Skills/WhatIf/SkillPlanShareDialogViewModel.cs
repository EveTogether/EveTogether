using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels.Skills.Plans;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Skills.Plans.Entities;

namespace EveUtils.Client.ViewModels.Skills.WhatIf;

/// <summary>
/// ET-358 SHARE dialog for a skill plan: COPY AS TEXT (the ET-355 export), PUT IT IN THE DOCTRINE (opens the ET-353
/// composition editor, prefilled — that editor's own SAVE is the only write either dialog can ever cause), and
/// EVE Workbench, always shown disabled with "later" (D8 — no such export exists yet).
/// </summary>
public sealed partial class SkillPlanShareDialogViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly IReadOnlyList<SkillPlanRow> _rows;
    private readonly ISdeAccessor _sde;
    private readonly Func<Task> _putInDoctrine;

    public SkillPlanShareDialogViewModel(IDialogService dialogs, string planName, IReadOnlyList<SkillPlanRow> rows,
        ISdeAccessor sde, Func<Task> putInDoctrine)
    {
        _dialogs = dialogs;
        PlanName = planName;
        _rows = rows;
        _sde = sde;
        _putInDoctrine = putInDoctrine;
    }

    public string PlanName { get; }

    /// <summary>D8: EVE Workbench sharing does not exist yet. Always false — there is no toggle that flips this.</summary>
    public bool IsEwbEnabled => false;
    public string EwbHint => "later";

    [ObservableProperty] private string? _statusMessage;

    /// <summary>Raised once the dialog is done — after a copy, after handing off to the doctrine editor, or on Close.</summary>
    public event Action? CloseRequested;

    [RelayCommand]
    private async Task CopyAsText()
    {
        var text = SkillPlanTextCodec.ToText(_rows.Select(row => (row.SkillTypeId, row.Level)).ToList(), _sde);
        await _dialogs.SetClipboardTextAsync(text);
        StatusMessage = "Copied.";
    }

    [RelayCommand]
    private async Task PutInDoctrine()
    {
        await _putInDoctrine();
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();
}
