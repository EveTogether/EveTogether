using System;
using Avalonia.Media;
using EveUtils.Client.Opsec;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Modules.Gamelog.Models;

namespace EveUtils.Client.ViewModels.GameLogs;

/// <summary>One line in the GAME LOGS list (ET-410). Immutable and built once per line, so filtering only picks
/// references and never allocates.</summary>
public sealed class GameLogRowViewModel(GameLogLine line, CharacterFaceViewModel face)
{
    public GameLogLine Line { get; } = line;

    public CharacterFaceViewModel Face { get; } = face;

    public DateTime Timestamp => Line.Timestamp;

    public string TimeText { get; } = line.Timestamp.ToString("HH:mm:ss");

    public string CharacterName => Line.Character;

    public GameLogLineKind Kind => Line.Kind;

    public string KindLabel { get; } = GameLogKindPalette.LabelOf(line.Kind);

    public IBrush KindBrush { get; } = GameLogKindPalette.BrushOf(line.Kind);

    /// <summary>Travel, docking and notification lines name systems and stations in free text, which cannot be cut
    /// apart reliably, so OPSEC masks those lines whole (ET-417). Fights, mining and bounties name no place.</summary>
    public string Text { get; } = line.Kind is GameLogLineKind.Combat or GameLogLineKind.Mining or GameLogLineKind.Bounty
        ? line.Text
        : OpsecText.Mark(line.Text);
}
