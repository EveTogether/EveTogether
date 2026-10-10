namespace EveUtils.Client.Runs;

/// <summary>Where a user creates the EVE Workbench API key an upload needs.</summary>
public static class EveWorkbenchLinks
{
    public const string AbyssTrackerTokens = "https://abysstracker.com/my-account/settings/tokens";
    public const string EveJournalTokens = "https://evejournal.com/my-account/personal-access-tokens";

    public const string HowTo = "How to get an API key:\n"
        + "1. Log in on Abyss Tracker or EVE Journal with your main character.\n"
        + "2. Open My Account → Personal Access Tokens.\n"
        + "3. Create a token and copy it.\n"
        + "4. Paste it here; your alts are picked up automatically.";

    public static void Open(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // No browser to open: the dialog still names the page.
        }
    }
}
