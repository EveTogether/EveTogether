namespace EveUtils.Server.Transport;

/// <summary>The running server's TLS cert fingerprint — shown in the admin panel and the Ping reply.</summary>
public sealed record ServerCertificateInfo(string Fingerprint);
