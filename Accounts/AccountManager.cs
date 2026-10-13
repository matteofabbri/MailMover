using MailKit.Net.Imap;
using MailKit.Security;
using MailMover.ConsoleUi;
using MailMover.Data;
using MailMover.Mail;
using MailMover.Models;
using Spectre.Console;

namespace MailMover.Accounts;

public static class AccountManager
{
    public static void Run()
    {
        while (true)
        {
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold]Gestione account[/]")
                    .AddChoices(
                        "Elenco account",
                        "Aggiungi account",
                        "Modifica account",
                        "Elimina account",
                        "Attiva/disattiva ignora errori SSL",
                        "Testa connessione",
                        "Indietro"));

            switch (choice)
            {
                case "Elenco account":
                    ListAccounts();
                    break;
                case "Aggiungi account":
                    AddAccount();
                    break;
                case "Modifica account":
                    EditAccount();
                    break;
                case "Elimina account":
                    DeleteAccount();
                    break;
                case "Attiva/disattiva ignora errori SSL":
                    ToggleIgnoreSsl();
                    break;
                case "Testa connessione":
                    TestConnection();
                    break;
                default:
                    return;
            }
        }
    }

    private static void ListAccounts()
    {
        var accounts = Database.GetAccounts();
        if (accounts.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Nessun account configurato.[/]");
            return;
        }

        var counts = Database.GetBackupCountsPerAccount();
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Nome");
        table.AddColumn("Server");
        table.AddColumn("Porta");
        table.AddColumn("Username");
        table.AddColumn("Sicurezza");
        table.AddColumn("Messaggi in backup");
        table.AddColumn("Tunnel SSH");

        foreach (var account in accounts)
            table.AddRow(
                account.Name.EscapeMarkup(),
                account.Host.EscapeMarkup(),
                account.Port.ToString(),
                account.Username.EscapeMarkup(),
                SecurityName(account.Security) + (account.IgnoreSslErrors ? " [yellow](⚠ cert. ignorato)[/]" : string.Empty),
                counts.GetValueOrDefault(account.Id).ToString(),
                account.Tunnel is { Enabled: true } ? "[green]sì[/]" : "[grey]no[/]");

        AnsiConsole.Write(table);
    }

    private static void AddAccount()
    {
        var name = AnsiConsole.Ask<string>("[green]Nome[/] (identificativo libero, es. 'Lavoro'):");
        var username = AnsiConsole.Ask<string>("[green]Username[/] (indirizzo email):").Trim();

        var preset = ProviderPresets.Find(username);
        if (preset is not null)
            AnsiConsole.MarkupLine(
                $"Provider riconosciuto: proposto [green]{preset.Host}[/] (porta {preset.Port}, SSL/TLS).");

        var hostPrompt = new TextPrompt<string>("[green]Server IMAP[/]:");
        if (preset?.Host is { } presetHost)
        {
            hostPrompt.DefaultValue(presetHost);
        }
        else if (username.Contains('@'))
        {
            string domain = username[(username.LastIndexOf('@') + 1)..].ToLowerInvariant();
            hostPrompt.DefaultValue($"imap.{domain}");
        }
        var host = AnsiConsole.Prompt(hostPrompt).Trim();

        var securityChoice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[green]Tipo di connessione[/]")
                .AddChoices("SSL/TLS (porta 993)", "STARTTLS (porta 143)", "Senza cifratura (solo test locali)"));

        int security;
        if (securityChoice.StartsWith("STARTTLS"))
        {
            security = Account.SecurityStartTls;
        }
        else if (securityChoice.StartsWith("Senza"))
        {
            security = Account.SecurityNone;
        }
        else
        {
            security = Account.SecuritySsl;
        }

        int defaultPort = preset?.Port ?? (security == Account.SecuritySsl ? 993 : 143);
        var port = AnsiConsole.Prompt(
            new TextPrompt<int>("[green]Porta[/]:")
                .DefaultValue(defaultPort)
                .ValidationErrorMessage("[red]Inserisci una porta valida (1-65535).[/]")
                .Validate(p => p is > 0 and < 65536 ? ValidationResult.Success() : ValidationResult.Error()));

        bool ignoreSslErrors = AnsiConsole.Confirm(
            "[yellow]Ignorare gli errori del certificato SSL?[/] " +
            "(server con certificato self-signed o non valido: usare solo su server di cui ti fidi)",
            defaultValue: false);

        var tunnel = AskTunnel(host, current: null);

        if (preset?.RequiresAppPassword == true)
            AnsiConsole.MarkupLine(
                "[yellow]Attenzione: questo provider richiede una 'password per app' al posto di quella normale" +
                " (si genera dalle impostazioni di sicurezza dell'account, con la verifica in due passaggi attiva).[/]");

        var password = AnsiConsole.Prompt(new TextPrompt<string>("[green]Password[/]:").Secret());

        var account = new Account(
            0, name.Trim(), host, port, username,
            PasswordVault.Protect(password), security, ignoreSslErrors, tunnel);
        Database.AddAccount(account);

        AnsiConsole.MarkupLine($"[green]✓ Account '{name.EscapeMarkup()}' salvato.[/]");

        if (AnsiConsole.Confirm("Vuoi testare subito la connessione?", defaultValue: true))
            TestAccount(account);
    }

    private static void DeleteAccount()
    {
        var account = Ui.PickAccount("Quale account vuoi eliminare?");
        if (account is null)
            return;

        long backupCount = Database.CountBackedUpMessages(account.Id);
        string warning = backupCount > 0
            ? $"Eliminare '{account.Name.EscapeMarkup()}' e [red]{backupCount} messaggi del suo backup[/]?"
            : $"Eliminare '{account.Name.EscapeMarkup()}'?";

        if (!AnsiConsole.Confirm(warning, defaultValue: false))
            return;

        Database.DeleteAccount(account.Id);
        AnsiConsole.MarkupLine("[green]✓ Account eliminato.[/]");
    }

    private static void EditAccount()
    {
        var account = Ui.PickAccount("Quale account vuoi modificare?");
        if (account is null)
            return;

        AnsiConsole.MarkupLine(
            "[grey]Premi Invio per mantenere il valore attuale di ogni campo.[/]");

        var name = AnsiConsole.Prompt(
            new TextPrompt<string>("[green]Nome[/]:").DefaultValue(account.Name));
        var host = AnsiConsole.Prompt(
            new TextPrompt<string>("[green]Server IMAP[/]:").DefaultValue(account.Host));

        var securityChoice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[green]Tipo di connessione[/]")
                .AddChoices(
                    $"SSL/TLS (porta 993){(account.Security == Account.SecuritySsl ? " [grey]— attuale[/]" : "")}",
                    $"STARTTLS (porta 143){(account.Security == Account.SecurityStartTls ? " [grey]— attuale[/]" : "")}",
                    $"Senza cifratura (solo test locali){(account.Security == Account.SecurityNone ? " [grey]— attuale[/]" : "")}"));

        int security = securityChoice.StartsWith("STARTTLS")
            ? Account.SecurityStartTls
            : securityChoice.StartsWith("Senza") ? Account.SecurityNone : Account.SecuritySsl;

        var port = AnsiConsole.Prompt(
            new TextPrompt<int>("[green]Porta[/]:")
                .DefaultValue(account.Port)
                .ValidationErrorMessage("[red]Inserisci una porta valida (1-65535).[/]")
                .Validate(p => p is > 0 and < 65536 ? ValidationResult.Success() : ValidationResult.Error()));

        var username = AnsiConsole.Prompt(
            new TextPrompt<string>("[green]Username[/] (indirizzo email):").DefaultValue(account.Username));

        string protectedPassword = account.Password;
        if (AnsiConsole.Confirm("Cambiare anche la [green]password[/]?", defaultValue: false))
        {
            if (ProviderPresets.Find(username)?.RequiresAppPassword == true)
                AnsiConsole.MarkupLine(
                    "[yellow]Ricorda: per questo provider serve una 'password per app', non la password normale del login.[/]");

            protectedPassword = PasswordVault.Protect(
                AnsiConsole.Prompt(new TextPrompt<string>("[green]Nuova password[/]:").Secret()));
        }

        bool ignoreSslErrors = AnsiConsole.Confirm(
            "[yellow]Ignorare gli errori del certificato SSL?[/]",
            defaultValue: account.IgnoreSslErrors);

        var tunnel = AskTunnel(host.Trim(), account.Tunnel);

        var updated = account with
        {
            Name = name.Trim(),
            Host = host.Trim(),
            Port = port,
            Username = username.Trim(),
            Password = protectedPassword,
            Security = security,
            IgnoreSslErrors = ignoreSslErrors,
            Tunnel = tunnel,
        };

        Database.UpdateAccount(updated);
        AnsiConsole.MarkupLine($"[green]✓ Account '{updated.Name.EscapeMarkup()}' aggiornato.[/]");

        if (AnsiConsole.Confirm("Vuoi testare subito la connessione?", defaultValue: true))
            TestAccount(updated);
    }

    // Configurazione del tunnel SSH di fallback (plink/PuTTY). La password,
    // quando presente, viene salvata protetta come quella dell'account.
    private static SshTunnelConfig? AskTunnel(string imapHost, SshTunnelConfig? current)
    {
        bool enabled = AnsiConsole.Confirm(
            "[yellow]Tunnel SSH via PuTTY come fallback?[/] " +
            "(se la connessione diretta fallisce 3 volte, il programma apre un tunnel con plink.exe e passa di lì)",
            defaultValue: current?.Enabled ?? false);

        if (!enabled)
            return null;

        if (SshTunnel.LocatePlink() is null)
            AnsiConsole.MarkupLine(
                "[yellow]plink.exe (PuTTY) non trovato: il fallback funzionerà dopo aver installato PuTTY[/] " +
                "(viene cercato nel PATH e in 'C:\\Program Files\\PuTTY\\plink.exe').");

        string sshHost = AnsiConsole.Prompt(
            new TextPrompt<string>("[green]Server SSH[/]:")
                .DefaultValue(current?.Host is { Length: > 0 } h ? h : imapHost));
        int sshPort = AnsiConsole.Prompt(
            new TextPrompt<int>("[green]Porta SSH[/]:")
                .DefaultValue(current is { Port: > 0 } ? current.Port : SshTunnelConfig.DefaultPort));
        string sshUser = AnsiConsole.Prompt(
            new TextPrompt<string>("[green]Utente SSH[/]:")
                .DefaultValue(current?.User ?? string.Empty));

        string sshPassword;
        if (current is { Password.Length: > 0 })
        {
            sshPassword = AnsiConsole.Confirm("[green]Mantenere la password SSH attuale?[/]", defaultValue: true)
                ? current.Password
                : PasswordVault.Protect(AnsiConsole.Prompt(
                    new TextPrompt<string>("[green]Nuova password SSH[/]:").Secret().AllowEmpty()));
        }
        else
        {
            sshPassword = PasswordVault.Protect(AnsiConsole.Prompt(
                new TextPrompt<string>("[green]Password SSH[/] (Invio per usare una chiave):").Secret().AllowEmpty()));
        }

        return new SshTunnelConfig(true, sshHost.Trim(), sshPort, sshUser.Trim(), sshPassword);
    }

    private static void ToggleIgnoreSsl()
    {
        var account = Ui.PickAccount("Per quale account attivare o disattivare l'opzione?");
        if (account is null)
            return;

        bool newValue = !account.IgnoreSslErrors;
        string state = newValue ? "attivare" : "disattivare";
        if (!AnsiConsole.Confirm(
                $"[yellow]{state}[/] l'ignora errori del certificato SSL per '{account.Name.EscapeMarkup()}'?",
                defaultValue: true))
            return;

        Database.SetIgnoreSslErrors(account.Id, newValue);
        AnsiConsole.MarkupLine(newValue
            ? "[green]✓ Opzione attivata:[/] i certificati SSL del server non verranno più verificati."
            : "[green]✓ Opzione disattivata:[/] i certificati SSL verranno verificati di nuovo.");

        if (AnsiConsole.Confirm("Vuoi testare subito la connessione?", defaultValue: true))
            TestAccount(account with { IgnoreSslErrors = newValue });
    }

    private static void TestConnection()
    {
        var account = Ui.PickAccount("Quale account vuoi verificare?");
        if (account is null)
            return;

        TestAccount(account);
    }

    private static void TestAccount(Account account)
    {
        try
        {
            using var client = new ImapClient();
            AnsiConsole.Status()
                .Start($"Connessione a [green]{account.Host.EscapeMarkup()}[/]...", _ => ImapConnectionFactory.Connect(client, account));
            int folderCount = client.GetFolders(client.PersonalNamespaces[0], false).Count;
            client.Disconnect(true);
            AnsiConsole.MarkupLine($"[green]✓ Connessione riuscita: trovate {folderCount} cartelle.[/]");
        }
        catch (Exception ex)
        {
            Ui.ShowError("Connessione non riuscita.", ex);

            bool sslError = ex is SslHandshakeException || ex.InnerException is SslHandshakeException;
            bool authError = ex is AuthenticationException || ex.InnerException is AuthenticationException;

            if (sslError)
            {
                AnsiConsole.MarkupLine(
                    "[yellow]Errore di handshake SSL (certificato non valido o self-signed): " +
                    "se il server è fidato puoi usare 'Attiva/disattiva ignora errori SSL' nel menu Gestisci account.[/]");
            }
            else if (authError)
            {
                AnsiConsole.MarkupLine(
                    "[yellow]Suggerimento: se sull'account è attiva la verifica in due passaggi, molti provider" +
                    " (Gmail, Outlook, iCloud, Yahoo…) richiedono una 'password per app' al posto di quella normale.[/]");
            }
        }
    }

    public static string SecurityName(int security) => security switch
    {
        Account.SecurityNone => "Nessuna",
        Account.SecurityStartTls => "STARTTLS",
        _ => "SSL/TLS"
    };
}
