using System.Globalization;
using System.Net;
using EveUtils.Shared.Messaging;

namespace EveUtils.Server.Backup;

/// <summary>
/// The page the browser gets back from a restore. A standalone HTML document rather than a panel route, because on
/// the success path the panel is about to go away: the database under the running server has just been replaced and
/// the process stops itself seconds later. Every value is HTML-encoded — the file name and the failure text come
/// from an uploaded archive.
/// </summary>
internal static class BackupRestorePage
{
    /// <summary>What the admin types to confirm a restore. A word, not a checkbox: this drops the database.</summary>
    public const string ConfirmationWord = "RESTORE";

    public static string Succeeded(BackupRestoreReport report)
    {
        var files = report.FilesRestored.Count == 0
            ? "none"
            : string.Join(", ", report.FilesRestored.Select(WebUtility.HtmlEncode));

        var keyWarning = report.TokenProtectorKeyRestored
            ? string.Empty
            : "<p style=\"color:#e8b04b\">This archive carried no <code>token-protector.key</code>. The refresh " +
              "tokens of every paired character cannot be decrypted, and this server will refuse to start until the " +
              "matching key is put back in the data directory.</p>";

        var body =
            $"<p>Restored the backup taken on <b>{Encoded(report.ArchiveCreatedAt.UtcDateTime.ToString("u", CultureInfo.InvariantCulture))}</b> " +
            $"by version <b>{Encoded(report.ArchiveAppVersion)}</b>.</p>" +
            $"<p>{report.Rows} rows across {report.Tables} tables, at migration " +
            $"<code>{Encoded(report.MigrationTarget)}</code>. Files put back: {files}.</p>" +
            keyWarning +
            $"<p style=\"color:#8a7e6b\">The state from just before this restore was archived to<br>" +
            $"<code>{Encoded(report.SafetyArchivePath)}</code><br>under the password you just entered. " +
            "Restore that file to undo this.</p>" +
            "<p><b>This server is stopping now</b> so it comes back up on the restored data. In Docker the restart " +
            "policy brings it back by itself; a bare-metal install has to be started again by hand.</p>";

        return Render("Restore complete", body);
    }

    public static string Failed(IReadOnlyList<ResultMessage> messages)
    {
        var body = string.Concat(messages.Select(m => $"<p>{Encoded(m.Text)}</p>"))
                   + "<p style=\"color:#8a7e6b\"><a style=\"color:#7ee0bb\" href=\"/backup\">Back to the panel</a></p>";

        return Render("Restore refused", body, accent: "#e8734a");
    }

    private static string Render(string heading, string body, string accent = "#7ee0bb") =>
        "<!doctype html><html><meta charset=\"utf-8\"><title>EVE Together — restore</title>" +
        "<body style=\"font-family:sans-serif;background:#06070a;color:#d8d0c4;display:flex;align-items:center;" +
        "justify-content:center;min-height:100vh\"><div style=\"max-width:44rem;padding:2rem\">" +
        $"<h2 style=\"color:{accent}\">{Encoded(heading)}</h2>{body}</div></body></html>";

    private static string Encoded(string? value) => WebUtility.HtmlEncode(value) ?? string.Empty;
}
