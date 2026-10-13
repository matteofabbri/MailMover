namespace MailMover.Models;

/// <summary>Configurazione del tunnel SSH di fallback, aperto con PuTTY (plink.exe).</summary>
public sealed record SshTunnelConfig(bool Enabled, string Host, int Port, string User, string Password)
{
    public const int DefaultPort = 22;
}

/// <summary>
/// Account IMAP; le password (account e tunnel) sono salvate offuscate
/// (vedi <see cref="PasswordVault"/>).
/// </summary>
public sealed record Account(
    int Id,
    string Name,
    string Host,
    int Port,
    string Username,
    string Password,
    int Security,
    bool IgnoreSslErrors,
    SshTunnelConfig? Tunnel)
{
    public const int SecurityNone = 0;
    public const int SecurityStartTls = 1;
    public const int SecuritySsl = 2;

    public string Describe() => $"{Name} ({Username} @ {Host})";
}
