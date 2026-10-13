using System.Globalization;
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

public static class CopyService
{
    public static void Run()
    {
        var backupAccounts = Database.GetAccountsWithBackup();
        if (backupAccounts.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Non ci sono backup nel database: esegui prima il backup di un account.[/]");
            return;
        }

        var source = Ui.PickAccountFrom(backupAccounts, "Quale backup vuoi copiare?");
        if (source is null)
            return;

        var destination = Ui.PickAccount("Su quale account vuoi copiare i messaggi?");
        if (destination is null)
            return;

        long pending = Database.CountPendingCopies(source.Id, destination.Id);
        long totalInBackup = Database.CountBackedUpMessages(source.Id);
        if (pending == 0)
        {
            AnsiConsole.MarkupLine("[green]Tutti i messaggi di questo backup sono già stati copiati sull'account scelto.[/]");
            return;
        }

        long previouslyRecorded = totalInBackup - pending;
        AnsiConsole.MarkupLine(
            $"Il backup contiene [green]{totalInBackup}[/] messaggi totali: " +
            $"[grey]{previouslyRecorded}[/] risultano già copiati in esecuzioni precedenti e verranno ignorati, " +
            $"ne restano [green]{pending}[/] da elaborare.");

        bool confirmed = AnsiConsole.Confirm(
            $"Copiare [green]{pending}[/] messaggi dal backup di '{source.Name.EscapeMarkup()}' verso '{destination.Name.EscapeMarkup()}'?",
            defaultValue: true);
        if (!confirmed)
            return;

        using var client = new ImapClient();
        try
        {
            AnsiConsole.Status()
                .Start($"Connessione a [green]{destination.Host.EscapeMarkup()}[/]...",
                    _ => ImapConnectionFactory.Connect(client, destination));
        }
        catch (Exception ex)
        {
            Ui.ShowError("Connessione non riuscita.", ex);
            return;
        }

        try
        {
            RunCopy(client, source, destination, pending, previouslyRecorded);
        }
        catch (Exception ex)
        {
            Ui.ShowError("Copia interrotta da un errore imprevisto.", ex);
        }
        finally
        {
            client.Disconnect(true);
        }
    }

    private static void RunCopy(ImapClient client, Account source, Account destination, long totalToCopy, long previouslyRecorded)
    {
        string sourceDelimiter = Database.GetBackupDelimiter(source.Id) ?? "/";
        var folderPaths = Database.GetBackupFolderPaths(source.Id);

        var folderCache = new Dictionary<string, IMailFolder>(StringComparer.OrdinalIgnoreCase);
        char destinationDelimiter = '/';
        AnsiConsole.Status().Start("Lettura delle cartelle di destinazione...", _ =>
        {
            foreach (var folder in client.GetFolders(client.PersonalNamespaces[0], false))
            {
                folderCache[folder.FullName] = folder;
                destinationDelimiter = folder.DirectorySeparator;
            }
            folderCache["INBOX"] = client.Inbox;
        });

        long copied = 0;
        long skipped = 0;
        var errors = new List<string>();

        using var readConnection = Database.Open();
        using var writeConnection = Database.Open();
        var alreadyCopied = Database.GetCopiedMailPks(destination.Id);

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
                var totalTask = ctx.AddTask("[bold]Totale[/]", maxValue: totalToCopy);
                bool stopAll = false;

                foreach (var folderPath in folderPaths)
                {
                    if (stopAll)
                        break;

                    long pendingInFolder = Database.CountPendingInFolder(readConnection, source.Id, folderPath, destination.Id);
                    if (pendingInFolder == 0)
                        continue;

                    var folderTask = ctx.AddTask(
                        $"[grey]{Ui.Truncate(folderPath.EscapeMarkup(), 40)}[/]",
                        maxValue: pendingInFolder);

                    IMailFolder target;
                    var destMessageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        string[] segments = folderPath.Split(sourceDelimiter, StringSplitOptions.RemoveEmptyEntries);
                        target = MailKitUtils.GetOrCreateFolder(client, folderCache, segments, destinationDelimiter);
                        target.Open(FolderAccess.ReadWrite);

                        if (target.Count > 0)
                        {
                            // Indice dei Message-ID già presenti nella cartella di destinazione,
                            // per non inserire due volte la stessa mail.
                            foreach (var item in target.Fetch(0, target.Count - 1, MessageSummaryItems.Envelope))
                            {
                                if (!string.IsNullOrEmpty(item.Envelope?.MessageId))
                                    destMessageIds.Add(item.Envelope.MessageId);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Cartella {folderPath}: {ex.Message}");
                        totalTask.Increment(pendingInFolder);
                        if (MailKitUtils.IsConnectionLoss(ex))
                            stopAll = true;
                        continue;
                    }

                    using var transaction = writeConnection.BeginTransaction();
                    try
                    {
                        CopyFolder(readConnection, writeConnection, transaction, target, destMessageIds,
                            source.Id, destination.Id, folderPath, alreadyCopied,
                            folderTask, totalTask, ref copied, ref skipped, errors);
                        transaction.Commit();
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Cartella {folderPath}: {ex.Message}");
                        if (MailKitUtils.IsConnectionLoss(ex))
                            stopAll = true;
                    }
                }

                if (stopAll)
                    AnsiConsole.MarkupLine(
                        "[yellow]Connessione al server persa: copia arrestata. " +
                        "Rilancia la copia per riprendere da dove si è fermata (niente duplicati).[/]");
            });

        Ui.PrintReport("Copia completata.", copied, skipped, errors, previouslyRecorded);
    }

    private static void CopyFolder(
        SqliteConnection readConnection, SqliteConnection writeConnection, SqliteTransaction transaction,
        IMailFolder target, HashSet<string> destMessageIds,
        int sourceAccountId, int destinationAccountId, string folderPath,
        HashSet<long> alreadyCopied, ProgressTask folderTask, ProgressTask totalTask,
        ref long copied, ref long skipped, List<string> errors)
    {
        using var command = readConnection.CreateCommand();
        command.CommandText = """
            SELECT id, message_id, keywords, flags, internal_date, raw
            FROM messages
            WHERE account_id = @account AND folder_path = @folder
            """;
        command.Parameters.AddWithValue("@account", sourceAccountId);
        command.Parameters.AddWithValue("@folder", folderPath);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            long id = reader.GetInt64(0);
            string? messageId = reader.IsDBNull(1) ? null : reader.GetString(1);
            string? keywordsText = reader.IsDBNull(2) ? null : reader.GetString(2);
            string? flagsText = reader.IsDBNull(3) ? null : reader.GetString(3);
            string? dateText = reader.IsDBNull(4) ? null : reader.GetString(4);
            byte[] raw = reader.GetFieldValue<byte[]>(5);

            if (alreadyCopied.Contains(id))
                continue;

            if (!string.IsNullOrEmpty(messageId) && destMessageIds.Contains(messageId))
            {
                // Già presente sul server (per esempio apparsa prima di un errore di quota):
                // la registriamo come copiata e passiamo oltre.
                Database.LogCopy(id, destinationAccountId, writeConnection, transaction);
                alreadyCopied.Add(id);
                skipped++;
                folderTask.Increment(1);
                totalTask.Increment(1);
                continue;
            }

            try
            {
                MessageFlags flags = MailKitUtils.ParseFlags(flagsText) & ~MessageFlags.Recent;
                DateTimeOffset internalDate =
                    DateTimeOffset.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                        ? parsed
                        : DateTimeOffset.Now;
                IEnumerable<string> keywords = string.IsNullOrEmpty(keywordsText)
                    ? []
                    : keywordsText.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

                MimeMessage message;
                using (var rawStream = new MemoryStream(raw, writable: false))
                    message = MimeMessage.Load(rawStream);

                target.Append(FormatOptions.Default, new AppendRequest(message, flags, keywords, internalDate));

                if (!string.IsNullOrEmpty(messageId))
                    destMessageIds.Add(messageId);

                Database.LogCopy(id, destinationAccountId, writeConnection, transaction);
                alreadyCopied.Add(id);
                copied++;
            }
            catch (Exception ex) when (MailKitUtils.IsConnectionLoss(ex))
            {
                // Connessione caduta: si ferma tutto pulito, senza generare un
                // errore in cascata per ogni mail rimanente. Al prossimo giro
                // la copia riprende da questa cartella.
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"{folderPath} messaggio #{id}: {ex.Message}");
            }

            folderTask.Increment(1);
            totalTask.Increment(1);
        }
    }
}
