using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using MailMover.Data;
using MailMover.Models;

namespace MailMover.Mail;

/// <summary>
/// Tunnel SSH aperto con PuTTY (plink.exe), usato come fallback quando la
/// connessione IMAP diretta fallisce: plink inoltra 127.0.0.1:&lt;porta locale&gt;
/// verso il server IMAP e il client si connette alla porta locale.
/// </summary>
public sealed class SshTunnel : IDisposable
{
    private static readonly Dictionary<string, SshTunnel> Active = new();
    private static readonly object Gate = new();

    static SshTunnel()
    {
        // I processi plink non devono sopravvivere alla chiusura di MailMover.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DisposeAll();
    }

    private readonly Process _process;

    public int LocalPort { get; }

    private SshTunnel(Process process, int localPort)
    {
        _process = process;
        LocalPort = localPort;
    }

    public static SshTunnel? GetOrStart(Account account, out string? error)
    {
        error = null;
        if (account.Tunnel is not { Enabled: true } tunnel)
        {
            error = "nessun tunnel SSH configurato per questo account";
            return null;
        }

        string key = $"{tunnel.User}@{tunnel.Host}:{tunnel.Port}->{account.Host}:{account.Port}";
        lock (Gate)
        {
            if (Active.TryGetValue(key, out var existing))
            {
                if (!existing._process.HasExited)
                    return existing;
                Active.Remove(key);
                existing.Dispose();
            }

            string? plink = LocatePlink();
            if (plink is null)
            {
                error = "plink.exe (PuTTY) non trovato: installa PuTTY o aggiungi plink al PATH";
                return null;
            }

            int localPort = ReserveFreeTcpPort();

            var psi = new ProcessStartInfo(plink)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-ssh");
            psi.ArgumentList.Add("-batch");
            psi.ArgumentList.Add("-N");
            psi.ArgumentList.Add("-L");
            psi.ArgumentList.Add($"127.0.0.1:{localPort}:{account.Host}:{account.Port}");
            psi.ArgumentList.Add("-P");
            psi.ArgumentList.Add(tunnel.Port.ToString());
            if (!string.IsNullOrEmpty(tunnel.Password))
            {
                // Nota: la password passa sulla riga di comando di plink
                // (visibile nell'elenco processi). Per maggiore sicurezza usare
                // l'autenticazione con chiave e lasciare vuoto questo campo.
                psi.ArgumentList.Add("-pw");
                psi.ArgumentList.Add(PasswordVault.Unprotect(tunnel.Password));
            }
            psi.ArgumentList.Add($"{tunnel.User}@{tunnel.Host}");

            Process process;
            try
            {
                process = Process.Start(psi)!;
            }
            catch (Exception ex)
            {
                error = $"avvio di plink fallito: {ex.Message}";
                return null;
            }

            var runner = new SshTunnel(process, localPort);

            // Attende che la porta locale accetti connessioni (massimo ~15 s).
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                if (process.HasExited)
                {
                    string stderr = process.StandardError.ReadToEnd();
                    error = "plink è terminato subito" +
                        (stderr.Contains("host key", StringComparison.OrdinalIgnoreCase) ||
                         stderr.Contains("cached", StringComparison.OrdinalIgnoreCase)
                            ? ": la chiave del server SSH non è ancora accettata. Apri una volta PuTTY (o plink senza -batch) verso il server, conferma la chiave, poi riprova."
                            : $": {FirstLine(stderr)}");
                    runner.Dispose();
                    return null;
                }

                try
                {
                    using var probe = new TcpClient();
                    probe.Connect("127.0.0.1", localPort);
                    Active[key] = runner;
                    return runner;
                }
                catch (SocketException)
                {
                    Thread.Sleep(300);
                }
            }

            error = "il tunnel non si è aperto entro 15 secondi";
            runner.Dispose();
            return null;
        }
    }

    internal static string? LocatePlink()
    {
        string beside = Path.Combine(AppContext.BaseDirectory, "plink.exe");
        if (File.Exists(beside))
            return beside;

        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(dir.Trim(), "plink.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
            }
        }

        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            if (string.IsNullOrEmpty(root))
                continue;
            string candidate = Path.Combine(root, "PuTTY", "plink.exe");
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static int ReserveFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string FirstLine(string text)
    {
        string line = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim() ?? "nessun dettaglio";
        return line.Length > 160 ? line[..160] + "…" : line;
    }

    public static void DisposeAll()
    {
        lock (Gate)
        {
            foreach (var tunnel in Active.Values)
                tunnel.Dispose();
            Active.Clear();
        }
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
        _process.Dispose();
    }
}
