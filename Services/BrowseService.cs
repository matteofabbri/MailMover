using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailMover.ConsoleUi;
using MailMover.Mail;
using MailMover.Models;
using MimeKit;
using Spectre.Console;

namespace MailMover.Services;

/// <summary>
/// Esplorazione e gestione manuale delle mail di un account: elenco per
/// cartella, lettura, cambio flag letto, eliminazione singola, svuotamento
/// cartella ed eliminazione totale del contenuto dell'account.
/// </summary>
public static class BrowseService
{
    private const int PageSize = 15;
    private const string NukeChoice = "⚡ Svuota completamente l'account (mail e cartelle)";
    private const string BackToFolders = "↩ Torna alle cartelle";
    private const string EmptyFolderChoice = "🗑 Svuota questa cartella";
    private const string PrevPage = "← Pagina precedente";
    private const string NextPage = "→ Pagina successiva";

    public static void Run()
    {
        var account = Ui.PickAccount("Quale account vuoi esplorare?");
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
            BrowseFolders(client, account);
        }
        catch (Exception ex)
        {
            Ui.ShowError("Esplorazione interrotta da un errore imprevisto.", ex);
        }
        finally
        {
            client.Disconnect(true);
        }
    }

    private static void BrowseFolders(ImapClient client, Account account)
    {
        while (true)
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

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title($"[bold]Account '{account.Name.EscapeMarkup()}' — cartelle[/]")
                    .PageSize(14)
                    .AddChoices(folders.Select(f => f.FullName.EscapeMarkup())
                        .Append(NukeChoice)
                        .Append(BackToFolders)));

            if (choice == BackToFolders)
                return;

            if (choice == NukeChoice)
            {
                NukeAll(client);
                continue;
            }

            var selected = folders.First(f => f.FullName.EscapeMarkup() == choice);
            BrowseMails(selected);
        }
    }

    private static void BrowseMails(IMailFolder folder)
    {
        folder.Open(FolderAccess.ReadWrite);
        int page = 0;

        while (true)
        {
            int total = folder.Count;
            if (total == 0)
            {
                AnsiConsole.MarkupLine("[grey]Cartella vuota.[/]");
                return;
            }

            int lastPage = (total - 1) / PageSize;
            page = Math.Clamp(page, 0, lastPage);

            // Più recenti prima: la pagina scorre partendo dal fondo della cartella.
            int end = total - 1 - page * PageSize;
            int start = Math.Max(0, end - PageSize + 1);

            var summaries = folder.Fetch(start, end,
                MessageSummaryItems.Envelope | MessageSummaryItems.Flags |
                MessageSummaryItems.InternalDate | MessageSummaryItems.UniqueId);

            var displayToSummary = new Dictionary<string, IMessageSummary>();
            var choices = new List<string>();
            foreach (var summary in summaries.Reverse())
            {
                string display = FormatMail(summary);
                displayToSummary[display] = summary;
                choices.Add(display);
            }

            choices.Add(EmptyFolderChoice);
            if (page < lastPage)
                choices.Add(NextPage);
            if (page > 0)
                choices.Add(PrevPage);
            choices.Add(BackToFolders);

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title($"[bold]{folder.FullName.EscapeMarkup()}[/] — [green]{total}[/] mail, pagina {page + 1}/{lastPage + 1}")
                    .PageSize(18)
                    .AddChoices(choices));

            if (choice == BackToFolders)
                return;

            if (choice == NextPage)
            {
                page++;
                continue;
            }

            if (choice == PrevPage)
            {
                page--;
                continue;
            }

            if (choice == EmptyFolderChoice)
            {
                EmptyFolder(folder);
                page = 0;
                continue;
            }

            MailActions(folder, displayToSummary[choice]);
        }
    }

    private static void MailActions(IMailFolder folder, IMessageSummary summary)
    {
        while (true)
        {
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title($"[bold]{Ui.Truncate(summary.Envelope?.Subject?.EscapeMarkup() ?? "(senza oggetto)", 60)}[/]")
                    .AddChoices("Leggi la mail", "Segna letta / non letta", "Elimina la mail", BackToFolders));

            switch (choice)
            {
                case "Leggi la mail":
                    ShowMessage(folder, summary);
                    break;

                case "Segna letta / non letta":
                {
                    bool seen = summary.Flags?.HasFlag(MessageFlags.Seen) == true;
                    if (seen)
                        folder.RemoveFlags(summary.UniqueId, MessageFlags.Seen, silent: true);
                    else
                        folder.AddFlags(summary.UniqueId, MessageFlags.Seen, silent: true);
                    AnsiConsole.MarkupLine(seen ? "[green]✓ Segnata come non letta.[/]" : "[green]✓ Segnata come letta.[/]");
                    return;
                }

                case "Elimina la mail":
                {
                    if (!AnsiConsole.Confirm("Eliminare [red]definitivamente[/] questa mail?", defaultValue: false))
                        break;

                    folder.AddFlags(summary.UniqueId, MessageFlags.Deleted, silent: true);
                    folder.Expunge();
                    AnsiConsole.MarkupLine("[green]✓ Mail eliminata.[/]");
                    return;
                }

                default:
                    return;
            }
        }
    }

    private static void ShowMessage(IMailFolder folder, IMessageSummary summary)
    {
        MimeMessage message;
        using (var stream = folder.GetStream(summary.UniqueId))
            message = MimeMessage.Load(stream);

        var header = new Table().Border(TableBorder.Minimal).HideHeaders();
        header.AddColumn("k");
        header.AddColumn("v");
        header.AddRow("[grey]Da[/]", (message.From?.ToString() ?? "?").EscapeMarkup());
        header.AddRow("[grey]A[/]", (message.To?.ToString() ?? "?").EscapeMarkup());
        header.AddRow("[grey]Data[/]", message.Date.LocalDateTime.ToString("g"));
        header.AddRow("[grey]Oggetto[/]", (message.Subject ?? "(senza oggetto)").EscapeMarkup());

        if (message.Attachments.Any())
        {
            string names = string.Join(", ",
                message.Attachments.Select(a => a is MimePart part ? part.FileName ?? "(allegato)" : "(allegato)"));
            header.AddRow("[grey]Allegati[/]", names.EscapeMarkup());
        }

        AnsiConsole.Write(header);

        string? body = message.TextBody;
        if (string.IsNullOrWhiteSpace(body))
            body = message.HtmlBody is null
                ? "(mail senza corpo testuale)"
                : "(mail in solo HTML: il contenuto è nella parte HTML, non mostrata qui)";

        const int max = 4000;
        if (body.Length > max)
            body = body[..max] + $"\n… (testo troncato, altri {body.Length - max} caratteri)";

        AnsiConsole.Write(new Panel(body.EscapeMarkup())
        {
            Header = new PanelHeader("Corpo della mail"),
            Border = BoxBorder.Rounded
        });
    }

    private static string FormatMail(IMessageSummary summary)
    {
        string date = summary.InternalDate?.LocalDateTime.ToString("yy/MM/dd HH:mm") ?? "  --/--/----";
        string from = Ui.Truncate(summary.Envelope?.From?.ToString() ?? "?", 28).EscapeMarkup();
        string seen = summary.Flags?.HasFlag(MessageFlags.Seen) == true ? " " : "✉";
        string subject = Ui.Truncate(summary.Envelope?.Subject ?? "(senza oggetto)", 55).EscapeMarkup();
        return $"{seen} {date} | {from} | {subject} [grey](#{summary.UniqueId.Id})[/]";
    }

    private static void EmptyFolder(IMailFolder folder)
    {
        int total = folder.Count;
        if (total == 0)
            return;

        if (!AnsiConsole.Confirm($"Eliminare [red]definitivamente tutte le {total} mail[/] di '{folder.FullName.EscapeMarkup()}'?", defaultValue: false))
            return;

        var uids = folder.Search(SearchQuery.All);
        foreach (var chunk in uids.Chunk(500))
        {
            folder.AddFlags(chunk.ToList(), MessageFlags.Deleted, silent: true);
            folder.Expunge();
        }

        AnsiConsole.MarkupLine($"[green]✓ Cartella svuotata ({total} mail eliminate).[/]");
    }

    private static void NukeAll(ImapClient client)
    {
        // Per eliminare anche i contenitori (cartelle senza messaggi, non
        // selezionabili) serve l'elenco completo, non solo quello selezionabile.
        var allFolders = client.GetFolders(client.PersonalNamespaces[0], false).ToList();
        if (allFolders.Count == 0)
            return;

        AnsiConsole.MarkupLine(
            "[red]ATTENZIONE:[/] stai per eliminare [red]tutte le mail e tutte le cartelle[/] di questo account, " +
            "cartelle annidate comprese. L'unica cartella che sopravvive è INBOX (IMAP non ne permette la rimozione), " +
            "che verrà comunque svuotata. L'operazione è definitiva e non reversibile.");

        if (!AnsiConsole.Confirm("Procedere?", defaultValue: false))
        {
            AnsiConsole.MarkupLine("[grey]Operazione annullata.[/]");
            return;
        }

        var phrase = AnsiConsole.Prompt(new TextPrompt<string>("Digita [red]ELIMINA[/] per confermare l'eliminazione totale:"));
        if (!string.Equals(phrase, "ELIMINA", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine("[grey]Conferma non ricevuta: operazione annullata.[/]");
            return;
        }

        // Dalle cartelle più profonde alle più esterne: i figli spariti prima
        // dei padri, così ogni eliminazione trova il contenitore vuoto.
        char delimiter = allFolders.FirstOrDefault()?.DirectorySeparator is { } d ? d : '/';
        var ordered = allFolders
            .OrderByDescending(f => f.FullName.Split(delimiter, StringSplitOptions.RemoveEmptyEntries).Length)
            .ToList();

        long removed = 0;
        long deletedFolders = 0;
        var errors = new List<string>();

        AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .Start(ctx =>
            {
                var totalTask = ctx.AddTask("[bold]Eliminazione totale account[/]", maxValue: Math.Max(1, ordered.Count));

                foreach (var folder in ordered)
                {
                    totalTask.Description = $"[grey]{Ui.Truncate(folder.FullName.EscapeMarkup(), 40)}[/]";
                    bool isInbox = string.Equals(folder.FullName, "INBOX", StringComparison.OrdinalIgnoreCase);

                    try
                    {
                        if (!folder.Attributes.HasFlag(FolderAttributes.NoSelect))
                        {
                            folder.Open(FolderAccess.ReadWrite);
                            int count = folder.Count;
                            if (count > 0)
                            {
                                var uids = folder.Search(SearchQuery.All);
                                foreach (var chunk in uids.Chunk(500))
                                {
                                    folder.AddFlags(chunk.ToList(), MessageFlags.Deleted, silent: true);
                                    folder.Expunge();
                                }

                                removed += count;
                            }

                            folder.Close();
                        }

                        if (!isInbox)
                        {
                            folder.Delete();
                            deletedFolders++;
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{folder.FullName}: {ex.Message}");
                    }

                    totalTask.Increment(1);
                }
            });

        AnsiConsole.MarkupLine(
            $"[green]✓ Eliminati {removed} messaggi e {deletedFolders} cartelle[/] " +
            "(INBOX è stata svuotata ma non può essere rimossa da IMAP).");

        if (errors.Count > 0)
            Ui.PrintReport("Eliminazione totale completata con errori.", 0, 0, errors);
    }
}
