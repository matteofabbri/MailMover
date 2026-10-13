using MailKit.Net.Imap;
using MailKit.Security;
using MailMover.ConsoleUi;
using MailMover.Data;
using MailMover.Models;
using Spectre.Console;

namespace MailMover.Mail;

/// <summary>
/// Punto unico di creazione delle connessioni IMAP: ogni connessione diretta
/// viene riprovata fino a 3 volte e, se l'account ha un tunnel SSH configurato,
/// il fallback finale passa dal tunnel aperto con PuTTY (plink.exe).
/// </summary>
public static class ImapConnectionFactory
{
    private const int MaxAttempts = 3;

    public static void Connect(ImapClient client, Account account)
    {
        Exception? lastError = null;

        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                ConnectDirect(client, account);
                if (attempt > 1)
                    AnsiConsole.MarkupLine($"[green]✓ Connessione riuscita al tentativo {attempt}.[/]");
                return;
            }
            catch (Exception ex) when (IsAuthenticationError(ex))
            {
                // Credenziali errate: riprovare non aiuta e rischia il blocco
                // temporaneo dell'IP da parte del server.
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt < MaxAttempts)
                {
                    string reason = Ui.Truncate(ex.Message.Split('\n')[0], 120);
                    AnsiConsole.MarkupLine(
                        $"[yellow]Connessione a {account.Host.EscapeMarkup()} fallita " +
                        $"(tentativo {attempt}/{MaxAttempts}): {reason.EscapeMarkup()} — riprovo...[/]");
                    Thread.Sleep(TimeSpan.FromSeconds(attempt * 2));
                }
            }
        }

        if (account.Tunnel is { Enabled: true })
        {
            AnsiConsole.MarkupLine("[yellow]Connessione diretta non riuscita: provo il tunnel SSH con PuTTY (plink)...[/]");
            var tunnel = SshTunnel.GetOrStart(account, out string? tunnelError);
            if (tunnel is null)
                throw new InvalidOperationException(
                    $"Connessione diretta a {account.Host} fallita dopo {MaxAttempts} tentativi " +
                    $"e tunnel SSH non disponibile ({tunnelError}).", lastError);

            AnsiConsole.MarkupLine(
                $"[green]✓ Tunnel SSH attivo[/] su 127.0.0.1:{tunnel.LocalPort} → {account.Host}:{account.Port} " +
                "(il traffico è cifrato dal tunnel: certificato IMAP non verificato).");
            ConnectThroughTunnel(client, account, tunnel.LocalPort);
            return;
        }

        throw new InvalidOperationException(
            $"Connessione a {account.Host} fallita dopo {MaxAttempts} tentativi: {lastError?.Message}", lastError);
    }

    private static void ConnectDirect(ImapClient client, Account account)
    {
        client.Timeout = 60_000;
        if (account.IgnoreSslErrors)
            client.ServerCertificateValidationCallback = (_, _, _, _) => true;

        client.Connect(account.Host, account.Port, SocketOptions(account));
        client.Authenticate(account.Username, PasswordVault.Unprotect(account.Password));
    }

    private static void ConnectThroughTunnel(ImapClient client, Account account, int localPort)
    {
        client.Timeout = 60_000;
        // Con il tunnel la connessione termina su 127.0.0.1: il nome nel
        // certificato non può combaciare, ma il canale è già cifrato da SSH.
        client.ServerCertificateValidationCallback = (_, _, _, _) => true;

        client.Connect("127.0.0.1", localPort, SocketOptions(account));
        client.Authenticate(account.Username, PasswordVault.Unprotect(account.Password));
    }

    private static SecureSocketOptions SocketOptions(Account account) => account.Security switch
    {
        Account.SecurityNone => SecureSocketOptions.None,
        Account.SecurityStartTls => SecureSocketOptions.StartTlsWhenAvailable,
        _ => SecureSocketOptions.SslOnConnect
    };

    private static bool IsAuthenticationError(Exception ex)
        => ex is AuthenticationException || ex.InnerException is AuthenticationException;
}
