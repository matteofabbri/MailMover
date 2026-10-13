using MailMover.Accounts;
using MailMover.ConsoleUi;
using MailMover.Data;
using MailMover.Services;
using Spectre.Console;

namespace MailMover;

internal static class Program
{
    // Incrementare a ogni modifica: mostrato all'avvio, nelle tabelle e nel log
    // per capire sempre quale versione è in esecuzione.
    internal const string BuildStamp = "2026-10-13.16";

    private static void Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args.Contains("--init-db", StringComparer.Ordinal))
        {
            Database.Initialize();
            var accounts = Database.GetAccounts();
            Console.WriteLine($"Database pronto: {Path.GetFullPath(Database.DbFileName)} — account configurati: {accounts.Count}");
            return;
        }

        AnsiConsole.Write(
            new FigletText("MailMover")
                .Centered()
                .Color(Color.BlueViolet));
        AnsiConsole.MarkupLine($"[grey]build {BuildStamp} — database: {Database.DbFileName}[/]");

        Database.Initialize();

        while (true)
        {
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold]Menu principale — cosa vuoi fare?[/]")
                    .PageSize(10)
                    .MoreChoicesText("[grey](muoviti con le frecce e conferma con Invio)[/]")
                    .AddChoices(
                        "Gestisci account",
                        "Backup di un account",
                        "Copia un backup su un altro account",
                        "Confronta e sincronizza due account",
                        "Analizza un account (duplicati e statistiche)",
                        "Esplora un account (leggi/elimina mail)",
                        "Elimina backup di un account",
                        "Esci"));

            switch (choice)
            {
                case "Gestisci account":
                    AccountManager.Run();
                    break;
                case "Backup di un account":
                    BackupService.Run();
                    break;
                case "Copia un backup su un altro account":
                    CopyService.Run();
                    break;
                case "Confronta e sincronizza due account":
                    SyncService.Run();
                    break;
                case "Analizza un account (duplicati e statistiche)":
                    AnalyzerService.Run();
                    break;
                case "Esplora un account (leggi/elimina mail)":
                    BrowseService.Run();
                    break;
                case "Elimina backup di un account":
                    DeleteBackup();
                    break;
                default:
                    return;
            }
        }
    }

    private static void DeleteBackup()
    {
        var backupAccounts = Database.GetAccountsWithBackup();
        if (backupAccounts.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Non ci sono backup salvati nel database.[/]");
            return;
        }

        var account = Ui.PickAccountFrom(backupAccounts, "Backup di quale account vuoi eliminare?");
        if (account is null)
            return;

        long count = Database.CountBackedUpMessages(account.Id);
        bool confirmed = AnsiConsole.Confirm(
            $"Eliminare [red]{count} messaggi[/] dal backup di '{account.Name.EscapeMarkup()}'?",
            defaultValue: false);
        if (!confirmed)
            return;

        Database.DeleteBackup(account.Id);
        AnsiConsole.MarkupLine("[green]✓ Backup eliminato.[/]");
    }
}
