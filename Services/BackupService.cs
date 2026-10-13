using System.Security.Cryptography;
using MailKit;
using MailKit.Net.Imap;
using MailMover.ConsoleUi;
using MailMover.Data;
using MailMover.Mail;
using MailMover.Models;
using Microsoft.Data.Sqlite;
using MimeKit;
using Spectre.Console;

namespace MailMover.Services;

public static class BackupService
{
    private const int FetchBatchSize = 200;

    public static void Run()
    {
        var account = Ui.PickAccount("Di quale account vuoi fare il backup?");
        if (account is null)
            return;

        using var client = new ImapClient();
        try
        {
            AnsiConsole.Status()
                .Start($"Connessione a [green]{account.Host.EscapeMarkup()}[/]...",
                    _ => ImapConnectionFactory.Connect(client, account));
        }
        catch (Exception ex)
        {
            Ui.ShowError("Connessione non riuscita.", ex);
            return;
        }

        try
        {
            RunBackup(client, account);
        }
        catch (Exception ex)
        {
            Ui.ShowError("Backup interrotto da un errore imprevisto.", ex);
        }
        finally
        {
            client.Disconnect(true);
        }
    }

    private static void RunBackup(ImapClient client, Account account)
    {
        if (client.PersonalNamespaces.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]Il server non espone alcun namespace personale.[/]");
            return;
        }

        var folders = new List<IMailFolder>();
        AnsiConsole.Status().Start("Lettura dell'elenco cartelle...", _ =>
        {
            foreach (var folder in client.GetFolders(client.PersonalNamespaces[0], false))
            {
                if (folder.Attributes.HasFlag(FolderAttributes.NoSelect))
                    continue;
                folders.Add(folder);
            }
        });

        if (folders.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Nessuna cartella trovata su questo account.[/]");
            return;
        }

        string delimiter = folders.FirstOrDefault()?.DirectorySeparator is { } c ? c.ToString() : "/";
        var messageCounts = new Dictionary<IMailFolder, int>();
        AnsiConsole.Status().Start("Conteggio dei messaggi per cartella...", ctx =>
        {
            foreach (var folder in folders)
            {
                ctx.Status($"Apertura [green]{folder.FullName.EscapeMarkup()}[/]");
                try
                {
                    folder.Open(FolderAccess.ReadOnly);
                    messageCounts[folder] = folder.Count;
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine(
                        $"[yellow]Cartella '{folder.FullName.EscapeMarkup()}' non leggibile: {ex.Message.EscapeMarkup()}[/]");
                }
            }
        });

        long totalMessages = messageCounts.Values.Sum(count => (long)count);
        AnsiConsole.MarkupLine(
            $"Trovate [green]{messageCounts.Count}[/] cartelle selezionabili per un totale di [green]{totalMessages}[/] messaggi.");
        if (totalMessages == 0)
            return;

        long added = 0;
        long skipped = 0;
        var errors = new List<string>();

        using var connection = Database.Open();
        Database.UpsertBackupMeta(account.Id, delimiter);

        AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new RemainingTimeColumn(),
                new SpinnerColumn())
            .Start(ctx =>
            {
                var totalTask = ctx.AddTask("[bold]Totale[/]", maxValue: totalMessages);

                foreach (var (folder, count) in messageCounts)
                {
                    if (count == 0)
                        continue;

                    var folderTask = ctx.AddTask(
                        $"[grey]{Ui.Truncate(folder.FullName.EscapeMarkup(), 40)}[/]",
                        maxValue: count);

                    try
                    {
                        BackupFolder(connection, account.Id, folder, folderTask, totalTask, ref added, ref skipped, errors);
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Cartella {folder.FullName}: {ex.Message}");
                        totalTask.Increment(count);
                    }
                }
            });

        Ui.PrintReport("Backup completato.", added, skipped, errors);
    }

    private static void BackupFolder(
        SqliteConnection connection, int accountId, IMailFolder folder,
        ProgressTask folderTask, ProgressTask totalTask,
        ref long added, ref long skipped, List<string> errors)
    {
        folder.Open(FolderAccess.ReadOnly);
        var existingUids = Database.GetExistingUids(connection, accountId, folder.FullName);
        string uidPrefix = folder.UidValidity + ":";

        for (int start = 0; start < folder.Count; start += FetchBatchSize)
        {
            int end = Math.Min(start + FetchBatchSize, folder.Count) - 1;
            var summaries = folder.Fetch(start, end,
                MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope |
                MessageSummaryItems.Flags | MessageSummaryItems.InternalDate |
                MessageSummaryItems.Size);

            using var transaction = connection.BeginTransaction();
            foreach (var summary in summaries)
            {
                try
                {
                    string uid = uidPrefix + summary.UniqueId.Id;
                    if (existingUids.Contains(uid))
                    {
                        skipped++;
                    }
                    else
                    {
                        byte[] raw;
                        using (var messageStream = folder.GetStream(summary.UniqueId))
                        using (var buffer = new MemoryStream())
                        {
                            messageStream.CopyTo(buffer);
                            raw = buffer.ToArray();
                        }

                        string hash = Convert.ToHexString(SHA256.HashData(raw));
                        int inserted = Database.InsertMessage(
                            connection, transaction, accountId, folder.FullName, uid,
                            summary.Envelope?.MessageId, summary.Envelope?.Subject,
                            summary.Envelope?.From?.ToString(),
                            summary.InternalDate?.ToString("o"),
                            summary.Flags?.ToString(),
                            summary.Keywords is { Count: > 0 } keywords ? string.Join(",", keywords) : null,
                            raw.Length, hash, raw);

                        if (inserted == 1)
                            added++;
                        else
                            skipped++;
                        existingUids.Add(uid);
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"{folder.FullName} uid {summary.UniqueId.Id}: {ex.Message}");
                }

                folderTask.Increment(1);
                totalTask.Increment(1);
            }

            transaction.Commit();
        }
    }
}
