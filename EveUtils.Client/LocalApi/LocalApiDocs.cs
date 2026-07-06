using System;
using System.IO;

namespace EveUtils.Client.LocalApi;

/// <summary>Serves the embedded HTML docs pages (landing index + connect guide), templating the base URL in.</summary>
public static class LocalApiDocs
{
    public const string IndexResource = "localapi.index.html";
    public const string DocsResource = "localapi.docs.html";
    public const string WidgetResource = "localapi.widget.html";

    public static string Render(string logicalName, string baseUrl)
    {
        using var stream = typeof(LocalApiDocs).Assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"Embedded local-API doc '{logicalName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("{{BASE_URL}}", baseUrl);
    }
}
