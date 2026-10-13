using MailKit;
using MailKit.Net.Imap;
using MailMover.ConsoleUi;
using MailMover.Mail;
using Spectre.Console;

namespace MailMover.Services;

/// <summary>
/// Diagnostica in sola lettura: per ogni cartella di un account conta i
/// messaggi, i Message-ID distinti, i duplicati interni e le mail senza
/// Message-ID; segnala inoltre i Message-ID presenti in più cartelle.
/// </summary>
public static class AnalyzerService
{
    private sealed record FolderStats(string Folder, long Total, long Distinct, long Duplicates, long WithoutMessageId);

    public static void Run()
    {
        var account = Ui.PickAccount("Quale account vuoi analizzare?");
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
            Analyze(client);
        }
        catch (Exception ex)
        {
            Ui.ShowError("Analisi interrotta da un errore imprevisto.", ex);
        }
        finally
        {
            client.Disconnect(true);
        }
    }

    private static void Analyze(ImapClient client)
    {
        var folders = new List<IMailFolder>();
        foreach (var folder in client.GetFolders(client.PersonalNamespaces[0], false))
        {
            if (!folder.Attributes.HasFlag(FolderAttributes.NoSelect))
                folders.Add(folder);
        }

        if (folders.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Nessuna cartella selezionabile su questo account.[/]");
            return;
        }

        var rows = new List<FolderStats>();
        var errors = new List<string>();
        var idFolderCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        AnsiConsole.Status().Start("Analisi delle cartelle...", ctx =>
        {
            foreach (var folder in folders)
            {
                ctx.Status($"Analisi di [green]{folder.FullName.EscapeMarkup()}[/]...");
                try
                {
                    folder.Open(FolderAccess.ReadOnly);
                    int total = folder.Count;
                    if (total == 0)
                    {
                        rows.Add(new FolderStats(folder.FullName, 0, 0, 0, 0));
                        continue;
                    }

                    var idUids = new Dictionary<string, List<UniqueId>>(StringComparer.OrdinalIgnoreCase);
                    long analyzed = 0;
                    long withoutId = 0;
                    foreach (var item in folder.Fetch(0, total - 1,
                                 MessageSummaryItems.Envelope | MessageSummaryItems.UniqueId))
                    {
                        analyzed++;
                        string? messageId = item.Envelope?.MessageId;
                        if (string.IsNullOrEmpty(messageId))
                        {
                            withoutId++;
                        }
                        else
                        {
                            if (!idUids.TryGetValue(messageId, out var uidsOfId))
                                idUids[messageId] = uidsOfId = new List<UniqueId>();
                            uidsOfId.Add(item.UniqueId);
                            idFolderCount[messageId] = idFolderCount.GetValueOrDefault(messageId) + 1;
                        }
                    }

                    // Regola dello stesso Message-ID: doppioni solo se anche i
                    // campi coincidono (oggetto, mittente, data, corpi testo/HTML).
                    // Il contenuto si scarica solo per gli id ripetuti; se il
                    // confronto fallisce, la mail non viene dichiarata doppione.
                    long distinct = 0;
                    long duplicates = 0;
                    foreach (var (messageId, uidsOfId) in idUids)
                    {
                        if (uidsOfId.Count == 1)
                        {
                            distinct++;
                            continue;
                        }

                        var hashes = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var uid in uidsOfId)
                        {
                            try
                            {
                                hashes.Add(MailKitUtils.ContentHash(folder, uid));
                            }
                            catch (Exception ex)
                            {
                                hashes.Add($"!nonverificabile-{uid.Id}");
                                errors.Add($"Cartella {folder.FullName} id {messageId} uid {uid.Id}: confronto impossibile: {ex.Message}");
                            }
                        }

                        distinct += hashes.Count;
                        duplicates += uidsOfId.Count - hashes.Count;
                    }

                    rows.Add(new FolderStats(folder.FullName, analyzed, distinct, duplicates, withoutId));
                }
                catch (Exception ex)
                {
                    errors.Add($"Cartella {folder.FullName}: {ex.Message}");
                }
            }
        });

        var table = new Table
        {
            Border = TableBorder.Rounded,
            Title = new TableTitle("[bold]Analisi per cartella[/]"),
        };
        table.AddColumn("Cartella");
        table.AddColumn(new TableColumn("Messaggi") { Alignment = Justify.Right });
        table.AddColumn(new TableColumn("Distinti") { Alignment = Justify.Right });
        table.AddColumn(new TableColumn("Duplicati in cartella") { Alignment = Justify.Right });
        table.AddColumn(new TableColumn("Senza Message-ID") { Alignment = Justify.Right });

        foreach (var row in rows)
        {
            table.AddRow(
                Ui.Truncate(row.Folder.EscapeMarkup(), 45),
                row.Total.ToString(),
                row.Distinct.ToString(),
                row.Duplicates == 0 ? "[grey]0[/]" : $"[red]{row.Duplicates}[/]",
                row.WithoutMessageId == 0 ? "[grey]0[/]" : $"[yellow]{row.WithoutMessageId}[/]");
        }

        table.AddRow(
            "[bold]Totale[/]",
            $"[bold]{rows.Sum(r => r.Total)}[/]",
            $"[bold]{rows.Sum(r => r.Distinct)}[/]",
            $"[bold]{rows.Sum(r => r.Duplicates)}[/]",
            $"[bold]{rows.Sum(r => r.WithoutMessageId)}[/]");

        AnsiConsole.Write(table);

        long inMultipleFolders = idFolderCount.Values.Count(v => v > 1);
        if (inMultipleFolders > 0)
            AnsiConsole.MarkupLine(
                $"[yellow]{inMultipleFolders}[/] Message-ID risultano presenti in più di una cartella " +
                "(copie in cartelle diverse, oppure cartelle di sistema che raggruppano le mail).");

        if (errors.Count > 0)
        {
            var details = string.Join("\n", errors.Take(10).Select(e => $"• {e.EscapeMarkup()}"));
            AnsiConsole.Write(new Panel(details)
            {
                Header = new PanelHeader($"Errori: {errors.Count}"),
                Border = BoxBorder.Rounded
            });
        }
    }
}
