using MailMover.Data;
using MailMover.Models;
using Spectre.Console;

namespace MailMover.ConsoleUi;

public static class Ui
{
    public static Account? PickAccount(string title)
        => PickAccountFrom(Database.GetAccounts(), title);

    public static Account? PickAccountFrom(List<Account> accounts, string title)
    {
        if (accounts.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Nessun account configurato: aggiungine uno dalla voce 'Gestisci account'.[/]");
            return null;
        }

        var displayToAccount = new Dictionary<string, Account>();
        foreach (var account in accounts)
        {
            string display = account.Describe().EscapeMarkup();
            string unique = display;
            int suffix = 2;
            while (displayToAccount.ContainsKey(unique))
                unique = $"{display} (#{suffix++})";
            displayToAccount[unique] = account;
        }

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title($"[bold]{title}[/]")
                .PageSize(15)
                .MoreChoicesText("[grey](muoviti con le frecce e conferma con Invio)[/]")
                .AddChoices(displayToAccount.Keys));

        return displayToAccount[choice];
    }

    public static void ShowError(string message, Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]✗ {message}[/]");
        AnsiConsole.MarkupLine($"[red]  {ex.Message.EscapeMarkup()}[/]");
    }

    public static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..(maxLength - 1)] + "…";

    public static void PrintReport(string title, long completed, long skipped, List<string> errors, long ignoredFromPreviousRuns = 0)
    {
        string summary = $"[bold green]{title}[/] Copiate ora: [green]{completed}[/].";
        if (skipped > 0)
            summary += $" Trovate già presenti sul server e saltate: [yellow]{skipped}[/].";
        if (ignoredFromPreviousRuns > 0)
            summary += $" Ignorate perché già copiate in esecuzioni precedenti: [grey]{ignoredFromPreviousRuns}[/].";
        AnsiConsole.MarkupLine(summary);

        if (errors.Count == 0)
            return;

        var details = string.Join("\n", errors.Take(10).Select(e => $"• {e.EscapeMarkup()}"));
        if (errors.Count > 10)
            details += $"\n• … e altri {errors.Count - 10} errori.";

        AnsiConsole.Write(new Panel(details)
        {
            Header = new PanelHeader($"Errori: {errors.Count}"),
            Border = BoxBorder.Rounded
        });
    }
}
