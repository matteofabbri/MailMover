using MailKit;
using MailKit.Net.Imap;
using MailMover.ConsoleUi;
using MailMover.Mail;
using MailMover.Models;
using MimeKit;
using Spectre.Console;

namespace MailMover.Services;

/// <summary>
/// Confronta due account IMAP dal vivo e rende la destinazione una copia 1:1
/// dell'origine: copia le mail mancanti e, su conferma, elimina definitivamente
/// quelle in più. Se il server chiude la connessione durante la copia, il
/// programma si riconnette e riprende dalla mail interrotta, ricostruendo
/// l'indice della destinazione per non duplicare nulla. Ogni esecuzione
/// produce un tracciato in mailmover-sync.log.
/// </summary>
public static class SyncService
{
    private const int MaxRecoveriesPerFolder = 10;

    private sealed record FolderReport(
        string Folder, int SourceCount, int DestCount, int Copied, int Failed, int SourceDuplicates, int Removable);

    private sealed record ExtraWork(string FolderName, uint UidValidity, List<UniqueId> Uids);

    private sealed class SyncAbortedException(string message, Exception inner) : Exception(message, inner);

    public static void Run()
    {
        var source = Ui.PickAccount("Quale account usi come ORIGINE (il vecchio account)?");
        if (source is null)
            return;

        var destination = Ui.PickAccount("E quale come DESTINAZIONE (il nuovo account da rendere identico)?");
        if (destination is null)
            return;

        if (source.Id == destination.Id)
        {
            AnsiConsole.MarkupLine("[yellow]Origine e destinazione coincidono: scegli due account diversi.[/]");
            return;
        }

        using var sourceClient = new ImapClient();
        using var destinationClient = new ImapClient();
        try
        {
            AnsiConsole.Status().Start(
                $"Connessione a [green]{source.Host.EscapeMarkup()}[/] e [green]{destination.Host.EscapeMarkup()}[/]...",
                _ =>
                {
                    ImapConnectionFactory.Connect(sourceClient, source);
                    ImapConnectionFactory.Connect(destinationClient, destination);
                });
        }
        catch (Exception ex)
        {
            Ui.ShowError("Connessione non riuscita.", ex);
            return;
        }

        try
        {
            RunSync(sourceClient, destinationClient, source, destination);
        }
        catch (Exception ex)
        {
            Ui.ShowError("Confronto interrotto da un errore imprevisto.", ex);
        }
        finally
        {
            sourceClient.Disconnect(true);
            destinationClient.Disconnect(true);
        }
    }

    private static void RunSync(ImapClient sourceClient, ImapClient destinationClient, Account source, Account destination)
    {
        var sourceFolders = new List<IMailFolder>();
        foreach (var folder in sourceClient.GetFolders(sourceClient.PersonalNamespaces[0], false))
        {
            if (!folder.Attributes.HasFlag(FolderAttributes.NoSelect))
                sourceFolders.Add(folder);
        }

        if (sourceFolders.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Nessuna cartella selezionabile sull'account di origine.[/]");
            return;
        }

        char sourceDelimiter = sourceFolders.FirstOrDefault()?.DirectorySeparator is { } c ? c : '/';

        var destFolderCache = new Dictionary<string, IMailFolder>(StringComparer.OrdinalIgnoreCase);
        char destDelimiter = '/';
        AnsiConsole.Status().Start("Lettura delle cartelle di destinazione...", _ =>
        {
            foreach (var folder in destinationClient.GetFolders(destinationClient.PersonalNamespaces[0], false))
            {
                destFolderCache[folder.FullName] = folder;
                destDelimiter = folder.DirectorySeparator;
            }
            destFolderCache["INBOX"] = destinationClient.Inbox;
        });

        var sourceCounts = new Dictionary<IMailFolder, int>();
        AnsiConsole.Status().Start("Conteggio dei messaggi per cartella...", ctx =>
        {
            foreach (var folder in sourceFolders)
            {
                ctx.Status($"Apertura [green]{folder.FullName.EscapeMarkup()}[/]");
                try
                {
                    folder.Open(FolderAccess.ReadOnly);
                    sourceCounts[folder] = folder.Count;
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine(
                        $"[yellow]Cartella '{folder.FullName.EscapeMarkup()}' non leggibile: {ex.Message.EscapeMarkup()}[/]");
                }
            }
        });

        long totalMessages = sourceCounts.Values.Sum(count => (long)count);
        AnsiConsole.MarkupLine(
            $"Origine: [green]{sourceCounts.Count}[/] cartelle per [green]{totalMessages}[/] messaggi. " +
            "Saranno copiate le mail mancanti e segnalate quelle in più. " +
            "I dettagli sono salvati in [grey]mailmover-sync.log[/].");

        if (totalMessages == 0)
            return;

        var reportRows = new List<FolderReport>();
        var extras = new List<ExtraWork>();
        long copied = 0;
        var errors = new List<string>();
        bool stopRun = false;

        using var log = new StreamWriter(
            File.Open("mailmover-sync.log", FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
        };
        Log(log, $"SESSIONE inizio build {Program.BuildStamp}: origine={source.Username}@{source.Host} destinazione={destination.Username}@{destination.Host}");

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

                foreach (var (folder, count) in sourceCounts)
                {
                    if (stopRun)
                        break;

                    // maxValue mai a zero: con massimo 0 Spectre.Console calcola
                    // una percentuale NaN, resa a schermo come -2147483648%.
                    var folderTask = ctx.AddTask(
                        $"[grey]{Ui.Truncate(folder.FullName.EscapeMarkup(), 40)}[/]",
                        maxValue: Math.Max(1, count));

                    try
                    {
                        var row = SyncFolder(source, destination, sourceClient, destinationClient, folder,
                            destFolderCache, sourceDelimiter, destDelimiter,
                            folderTask, totalTask, ref copied, errors, extras, log);
                        reportRows.Add(row);
                    }
                    catch (SyncAbortedException ex)
                    {
                        errors.Add(ex.Message);
                        Log(log, $"SINCRONIZZAZIONE INTERROTTA: {ex.Message}");
                        stopRun = true;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Cartella {folder.FullName}: {ex.Message}");
                        Log(log, $"ERRORE CARTELLA '{folder.FullName}': {ex.Message}");
                        totalTask.Increment(count);
                    }

                    if (count == 0)
                        folderTask.Increment(1);
                }
            });

        Log(log, $"SESSIONE fine: copiate={copied} errori={errors.Count}");
        PrintComparisonTable(reportRows);
        Ui.PrintReport("Confronto e sincronizzazione completati.", copied, 0, errors);

        if (errors.Count > 0)
            AnsiConsole.MarkupLine(
                "[yellow]La sessione ha avuto errori: rilancia la sincronizzazione per completare la copia " +
                "(riprende da dove si è fermata e non duplica nulla).[/]");

        OfferRemoval(destinationClient, destination, extras, log, errors.Count);
    }

    private static FolderReport SyncFolder(
        Account sourceAccount, Account destinationAccount,
        ImapClient sourceClient, ImapClient destinationClient, IMailFolder folder,
        Dictionary<string, IMailFolder> destFolderCache, char sourceDelimiter, char destDelimiter,
        ProgressTask folderTask, ProgressTask totalTask, ref long copied, List<string> errors,
        List<ExtraWork> extras, StreamWriter log)
    {
        IMailFolder sourceFolder = folder;
        IMailFolder target = null!;
        string targetName = string.Empty;
        IList<IMessageSummary> summaries = Array.Empty<IMessageSummary>();
        int sourceCount = 0;
        int recoveries = 0;

        // Indici della destinazione usati per riconoscere i duplicati.
        var destMessageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var destHashToUid = new Dictionary<string, UniqueId>(StringComparer.OrdinalIgnoreCase);
        var knownDestUids = new HashSet<UniqueId>();
        var destUidsById = new Dictionary<string, List<UniqueId>>(StringComparer.OrdinalIgnoreCase);
        var destHashByUid = new Dictionary<UniqueId, string>();

        // Candidati alla rimozione: fotografia della destinazione congelata
        // prima di iniziare a copiare.
        var initialWithId = new List<(UniqueId Uid, string Id)>();
        var initialNoId = new List<(UniqueId Uid, string Hash)>();

        // Riconnessione con ripresa: ricostruisce lo stato della destinazione
        // (buste per i Message-ID, hash solo per le mail senza id mai viste).
        bool Recover(Exception ex)
        {
            if (!MailKitUtils.IsConnectionLoss(ex) || recoveries >= MaxRecoveriesPerFolder)
                return false;

            recoveries++;
            AnsiConsole.MarkupLine(
                $"[yellow]Connessione persa su '{sourceFolder.FullName.EscapeMarkup()}': riconnessione e ripresa automatica...[/]");
            Log(log, $"RICONNESSIONE #{recoveries} su '{sourceFolder.FullName}': {ex.Message.Split('\n')[0]}");

            try
            {
                if (!sourceClient.IsConnected)
                {
                    ImapConnectionFactory.Connect(sourceClient, sourceAccount);
                    sourceFolder = sourceClient.GetFolder(folder.FullName);
                    sourceFolder.Open(FolderAccess.ReadOnly);
                }

                if (!destinationClient.IsConnected)
                {
                    ImapConnectionFactory.Connect(destinationClient, destinationAccount);

                    // Gli oggetti cartella del vecchio collegamento non sono
                    // più validi: si riparte dalla risoluzione dal vivo.
                    destFolderCache.Clear();

                    if (targetName.Length > 0)
                    {
                        target = destinationClient.GetFolder(targetName);
                        target.Open(FolderAccess.ReadWrite);

                        int scanned = target.Count;
                        if (scanned > 0)
                        {
                            foreach (var item in target.Fetch(0, scanned - 1,
                                         MessageSummaryItems.Envelope | MessageSummaryItems.UniqueId))
                            {
                                if (!knownDestUids.Add(item.UniqueId))
                                    continue;

                                string? id = item.Envelope?.MessageId;
                                if (string.IsNullOrEmpty(id))
                                    destHashToUid.TryAdd(MailKitUtils.ContentHash(target, item.UniqueId), item.UniqueId);
                                else
                                    destMessageIds.Add(id);
                            }
                        }
                    }
                }

                return true;
            }
            catch (Exception reconnectError)
            {
                Log(log, $"RICONNESSIONE #{recoveries} fallita su '{sourceFolder.FullName}': {reconnectError.Message}");
                return false;
            }
        }

        // Fase 1: apertura cartelle e fotografia della destinazione, con
        // riconnessione automatica se la connessione cade durante la scansione.
        while (true)
        {
            try
            {
                sourceFolder.Open(FolderAccess.ReadOnly);
                sourceCount = sourceFolder.Count;
                summaries = sourceCount == 0
                    ? Array.Empty<IMessageSummary>()
                    : sourceFolder.Fetch(0, sourceCount - 1,
                        MessageSummaryItems.Envelope | MessageSummaryItems.Flags |
                        MessageSummaryItems.InternalDate | MessageSummaryItems.UniqueId);

                string[] segments = folder.FullName.Split(sourceDelimiter, StringSplitOptions.RemoveEmptyEntries);
                target = MailKitUtils.GetOrCreateFolder(destinationClient, destFolderCache, segments, destDelimiter);
                targetName = target.FullName;
                target.Open(FolderAccess.ReadWrite);

                string workingDescription = folderTask.Description;
                folderTask.Description = $"[grey]scansione {Ui.Truncate(folder.FullName.EscapeMarkup(), 32)}…[/]";

                destMessageIds.Clear();
                destHashToUid.Clear();
                knownDestUids.Clear();
                destUidsById.Clear();
                destHashByUid.Clear();
                initialWithId.Clear();
                initialNoId.Clear();

                int scannedDest = target.Count;
                if (scannedDest > 0)
                {
                    foreach (var item in target.Fetch(0, scannedDest - 1,
                                 MessageSummaryItems.Envelope | MessageSummaryItems.UniqueId))
                    {
                        knownDestUids.Add(item.UniqueId);
                        string? id = item.Envelope?.MessageId;
                        if (string.IsNullOrEmpty(id))
                        {
                            string hash = MailKitUtils.ContentHash(target, item.UniqueId);
                            initialNoId.Add((item.UniqueId, hash));
                            destHashToUid.TryAdd(hash, item.UniqueId);
                        }
                        else
                        {
                            initialWithId.Add((item.UniqueId, id));
                            destMessageIds.Add(id);
                            if (!destUidsById.TryGetValue(id, out var uidsOfId))
                                destUidsById[id] = uidsOfId = new List<UniqueId>();
                            uidsOfId.Add(item.UniqueId);
                        }
                    }
                }

                folderTask.Description = workingDescription;
                break;
            }
            catch (Exception ex)
            {
                if (MailKitUtils.IsConnectionLoss(ex) && Recover(ex))
                    continue;

                if (MailKitUtils.IsConnectionLoss(ex))
                    throw new SyncAbortedException(
                        $"Connessione persa su '{folder.FullName}' e riconnessione non riuscita: sincronizzazione interrotta", ex);

                throw;
            }
        }

        int destInitial = initialWithId.Count + initialNoId.Count;
        var sourceFirstUid = new Dictionary<string, UniqueId>(StringComparer.OrdinalIgnoreCase);
        var sourceHashesById = new Dictionary<string, Dictionary<string, UniqueId>>(StringComparer.OrdinalIgnoreCase);
        var sourceNoIdHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matchedSourceHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ensuredIdHashes = new HashSet<(string Id, string Hash)>();
        int distinctIdCopies = 0;
        int copiedInFolder = 0;
        int failedInFolder = 0;
        int withoutMessageId = 0;
        int sourceDuplicates = 0;
        bool completed = true;

        // Impronta di una mail della destinazione (scaricata una sola volta).
        string? GetDestHash(UniqueId uid)
        {
            if (destHashByUid.TryGetValue(uid, out string? cached))
                return cached;
            try
            {
                string hash = MailKitUtils.ContentHash(target, uid);
                destHashByUid[uid] = hash;
                return hash;
            }
            catch (Exception ex)
            {
                Log(log, $"HASH destinazione fallito uid={uid.Id}: {ex.Message}");
                return null;
            }
        }

        HashSet<string> GetDestHashesForId(string id)
        {
            var hashes = new HashSet<string>(StringComparer.Ordinal);
            if (!destUidsById.TryGetValue(id, out var uids))
                return hashes;
            foreach (var uid in uids)
            {
                string? hash = GetDestHash(uid);
                if (hash is not null)
                    hashes.Add(hash);
            }
            return hashes;
        }

        // Regola dello stesso Message-ID: prima di parlare di doppione si
        // confrontano i campi (oggetto, mittente, data, corpi testo/HTML). Se il
        // contenuto differisce sono due mail distinte: entrambe le versioni
        // devono esistere sulla destinazione. Ritorna -1 per un doppione vero,
        // altrimenti il numero di copie eseguite.
        int HandleIdCollision(string id, IMessageSummary current)
        {
            if (!sourceHashesById.TryGetValue(id, out var byHash))
            {
                byHash = new Dictionary<string, UniqueId>(StringComparer.Ordinal);
                sourceHashesById[id] = byHash;

                // L'impronta della prima occorrenza si calcola solo adesso,
                // quando l'id si ripete.
                var firstUid = sourceFirstUid[id];
                try
                {
                    byHash[MailKitUtils.ContentHash(sourceFolder, firstUid)] = firstUid;
                }
                catch (Exception ex)
                {
                    Log(log, $"CONFRONTO impossibile per id={id} (prima occorrenza uid={firstUid.Id}): {ex.Message}");
                }
            }

            string currentHash;
            try
            {
                currentHash = MailKitUtils.ContentHash(sourceFolder, current.UniqueId);
            }
            catch (Exception ex)
            {
                // Senza impronta il doppione non è dimostrabile: la mail resta
                // distinta, la copia fallirà o riuscirà da sola.
                errors.Add($"{folder.FullName}: confronto impossibile per id={id} uid={current.UniqueId.Id}: {ex.Message}");
                Log(log, $"CONFRONTO impossibile per id={id} uid={current.UniqueId.Id}: {ex.Message}");
                return 0;
            }

            if (byHash.TryGetValue(currentHash, out var existingRep))
            {
                if (existingRep != current.UniqueId)
                    return -1; // doppione vero: stesso id e stessi campi

                // Ripresa dopo una riconnessione: questa versione era già stata
                // riconosciuta distinta, va solo assicurata a destinazione.
            }
            else
            {
                byHash[currentHash] = current.UniqueId;
                distinctIdCopies++;
                Log(log, $"DISTINTA id={id} '{folder.FullName}' uid={current.UniqueId.Id}: stesso Message-ID ma contenuto diverso");
            }

            int appends = 0;
            var destHashes = GetDestHashesForId(id);
            foreach (var (hash, repUid) in byHash)
            {
                if (destHashes.Contains(hash) || ensuredIdHashes.Contains((id, hash)))
                    continue;

                try
                {
                    if (repUid == current.UniqueId)
                    {
                        CopyMessage(sourceFolder, current, target);
                    }
                    else
                    {
                        var rep = sourceFolder
                            .Fetch(new List<UniqueId> { repUid },
                                MessageSummaryItems.Envelope | MessageSummaryItems.Flags |
                                MessageSummaryItems.InternalDate | MessageSummaryItems.UniqueId)
                            .FirstOrDefault();
                        if (rep is null)
                        {
                            Log(log, $"VERSIONE non trovata id={id} uid={repUid.Id}");
                            continue;
                        }
                        CopyMessage(sourceFolder, rep, target);
                    }

                    ensuredIdHashes.Add((id, hash));
                    appends++;
                    Log(log, $"COPIA (versione distinta) '{folder.FullName}' id={id} uid={repUid.Id}");
                }
                catch (Exception ex) when (MailKitUtils.IsConnectionLoss(ex))
                {
                    throw; // gestita dal chiamante: riconnessione e ripresa
                }
                catch (Exception ex)
                {
                    ensuredIdHashes.Add((id, hash));
                    errors.Add($"{folder.FullName} (versione distinta id={id}): {ex.Message}");
                    Log(log, $"ERRORE versione distinta '{folder.FullName}' id={id} uid={repUid.Id}: {ex.Message}");
                }
            }

            return appends;
        }

        for (int index = 0; index < summaries.Count; index++)
        {
            var summary = summaries[index];
            string? messageId = summary.Envelope?.MessageId;
            bool retrySameMessage = false;

            try
            {
                if (string.IsNullOrEmpty(messageId))
                {
                    withoutMessageId++;
                    string hash = MailKitUtils.ContentHash(sourceFolder, summary.UniqueId);
                    if (!sourceNoIdHashes.Add(hash))
                    {
                        sourceDuplicates++;
                    }
                    else if (destHashToUid.ContainsKey(hash))
                    {
                        matchedSourceHashes.Add(hash);
                    }
                    else
                    {
                        CopyMessage(sourceFolder, summary, target);
                        Log(log, $"COPIA (senza id) '{folder.FullName}' uid={summary.UniqueId.Id}");
                        copiedInFolder++;
                        copied++;
                    }
                }
                else if (sourceFirstUid.TryAdd(messageId, summary.UniqueId))
                {
                    if (!destMessageIds.Contains(messageId))
                    {
                        CopyMessage(sourceFolder, summary, target);
                        destMessageIds.Add(messageId);
                        Log(log, $"COPIA '{folder.FullName}' uid={summary.UniqueId.Id} id={messageId}");
                        copiedInFolder++;
                        copied++;
                    }
                }
                else
                {
                    int appends = HandleIdCollision(messageId, summary);
                    if (appends < 0)
                    {
                        sourceDuplicates++;
                    }
                    else
                    {
                        copiedInFolder += appends;
                        copied += appends;
                    }
                }
            }
            catch (Exception ex)
            {
                if (MailKitUtils.IsConnectionLoss(ex))
                {
                    if (Recover(ex))
                    {
                        // Connessione ricostruita e indice della destinazione
                        // rifatto: si riprova esattamente questa mail, senza
                        // rischiare di copiarla due volte.
                        retrySameMessage = true;
                    }
                    else
                    {
                        completed = false;
                        throw new SyncAbortedException(
                            $"Connessione persa su '{folder.FullName}' e riconnessione non riuscita: sincronizzazione interrotta", ex);
                    }
                }
                else
                {
                    failedInFolder++;
                    errors.Add($"{folder.FullName}: {ex.Message}");
                    Log(log, $"ERRORE '{folder.FullName}' uid={summary.UniqueId.Id} id={messageId ?? "-"}: {ex.Message}");
                }
            }

            if (retrySameMessage)
            {
                index--;
                continue;
            }

            folderTask.Increment(1);
            totalTask.Increment(1);
        }

        // In più per il 1:1: doppioni (stessa mail ripetuta), mail senza
        // corrispettivo nell'origine e copie multiple senza Message-ID.
        // Se la cartella è stata interrotta, niente pulizia: il confronto
        // non è completo e l'elenco sarebbe inaffidabile.
        var keptIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keptIdHashes = new HashSet<(string Id, string Hash)>();
        var keptNoIdHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var removableUids = new List<UniqueId>();
        if (completed)
        {
            foreach (var (uid, id) in initialWithId)
            {
                if (sourceHashesById.TryGetValue(id, out var byHash))
                {
                    // Id riusato da più mail distinte: si conserva una copia
                    // per ogni versione presente nell'origine.
                    string? hash = GetDestHash(uid);
                    if (hash is null)
                        continue; // non verificabile: si conserva
                    if (!byHash.ContainsKey(hash))
                        removableUids.Add(uid);
                    else if (!keptIdHashes.Add((id, hash)))
                        removableUids.Add(uid);
                }
                else if (sourceFirstUid.ContainsKey(id))
                {
                    if (!keptIds.Add(id))
                        removableUids.Add(uid);
                }
                else
                {
                    removableUids.Add(uid);
                }
            }

            foreach (var (uid, hash) in initialNoId)
            {
                if (!matchedSourceHashes.Contains(hash) || !keptNoIdHashes.Add(hash))
                    removableUids.Add(uid);
            }

            if (removableUids.Count > 0)
                extras.Add(new ExtraWork(target.FullName, target.UidValidity, removableUids));
        }

        int destCount = destInitial + copiedInFolder;
        int removableCount = completed ? removableUids.Count : 0;

        Log(log,
            $"CARTELLA '{folder.FullName}': origine={sourceCount} dest={destCount} " +
            $"copiate={copiedInFolder} errori={failedInFolder} senzaId={withoutMessageId} " +
            $"doppieOrigine={sourceDuplicates} versioniDistinte={distinctIdCopies} rimovibili={removableCount}" +
            (completed ? string.Empty : " (CARTELLA INTERROTTA: pulizia non proposta)"));

        return new FolderReport(
            folder.FullName, sourceCount, destCount, copiedInFolder, failedInFolder,
            sourceDuplicates, removableCount);
    }

    private static void OfferRemoval(
        ImapClient client, Account account, List<ExtraWork> extras, StreamWriter log, int sessionErrors)
    {
        long totalExtras = extras.Sum(work => (long)work.Uids.Count);
        if (totalExtras == 0)
        {
            AnsiConsole.MarkupLine(sessionErrors == 0
                ? "[green]Il nuovo account è ora una copia 1:1 del vecchio: nessuna mail in più.[/]"
                : $"[yellow]Nessuna mail in più da rimuovere, ma la sessione ha avuto {sessionErrors} errori: " +
                  "rilancia la sincronizzazione per completare la copia.[/]");
            return;
        }

        if (!AnsiConsole.Confirm(
                $"Il nuovo account contiene [red]{totalExtras}[/] mail in più rispetto al vecchio " +
                "(doppioni o mail senza corrispondenza). [red]Eliminarle definitivamente?[/]",
                defaultValue: false))
        {
            AnsiConsole.MarkupLine("[grey]Nessuna mail rimossa: le 'in più' restano al loro posto.[/]");
            return;
        }

        if (!client.IsConnected)
        {
            try
            {
                AnsiConsole.Status().Start("Riconnessione per la pulizia...",
                    _ => ImapConnectionFactory.Connect(client, account));
            }
            catch (Exception ex)
            {
                Ui.ShowError("Connessione non riuscita: pulizia saltata, rilancia la sincronizzazione.", ex);
                return;
            }
        }

        long removed = 0;
        var errors = new List<string>();

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
                var totalTask = ctx.AddTask("[bold]Eliminazione mail in più[/]", maxValue: Math.Max(1, totalExtras));

                foreach (var work in extras)
                {
                    var task = ctx.AddTask(
                        $"[grey]{Ui.Truncate(work.FolderName.EscapeMarkup(), 40)}[/]",
                        maxValue: Math.Max(1, work.Uids.Count));

                    try
                    {
                        var folder = client.GetFolder(work.FolderName);
                        folder.Open(FolderAccess.ReadWrite);

                        if (folder.UidValidity != work.UidValidity)
                        {
                            errors.Add($"{work.FolderName}: la cartella è cambiata (validità diversa), pulizia saltata: rilancia la sincronizzazione");
                            task.Increment(work.Uids.Count);
                            totalTask.Increment(work.Uids.Count);
                            continue;
                        }

                        foreach (var chunk in work.Uids.Chunk(500))
                        {
                            var uids = chunk.ToList();
                            folder.AddFlags(uids, MessageFlags.Deleted, silent: true);
                            folder.Expunge();

                            removed += chunk.Length;
                            task.Increment(chunk.Length);
                            totalTask.Increment(chunk.Length);
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{work.FolderName}: {ex.Message}");
                        Log(log, $"ERRORE RIMOZIONE '{work.FolderName}': {ex.Message}");
                        totalTask.Increment(work.Uids.Count);
                    }
                }
            });

        Log(log, $"RIMOZIONE fine: eliminate={removed} errori={errors.Count}");
        AnsiConsole.MarkupLine($"[green]✓ Eliminate {removed} mail in più: il nuovo account ora rispecchia il vecchio.[/]");

        if (errors.Count > 0)
            Ui.PrintReport("Eliminazione completata con errori.", 0, 0, errors);
    }

    private static void CopyMessage(IMailFolder source, IMessageSummary summary, IMailFolder target)
    {
        var flags = (summary.Flags ?? MessageFlags.None) & ~MessageFlags.Recent;
        var internalDate = summary.InternalDate ?? DateTimeOffset.Now;
        IEnumerable<string> keywords = (IEnumerable<string>?)summary.Keywords ?? Array.Empty<string>();

        MimeMessage message;
        using (var messageStream = source.GetStream(summary.UniqueId))
            message = MimeMessage.Load(messageStream);

        target.Append(FormatOptions.Default, new AppendRequest(message, flags, keywords, internalDate));
    }

    // Il log non deve mai compromettere la sincronizzazione.
    private static void Log(StreamWriter log, string message)
    {
        try
        {
            log.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}");
        }
        catch
        {
        }
    }

    private static void PrintComparisonTable(List<FolderReport> rows)
    {
        var table = new Table
        {
            Border = TableBorder.Rounded,
            Title = new TableTitle($"[bold]Confronto per cartella[/] [grey](build {Program.BuildStamp})[/]"),
        };
        table.AddColumn("Cartella");
        table.AddColumn(new TableColumn("Origine") { Alignment = Justify.Right });
        table.AddColumn(new TableColumn("Destinazione") { Alignment = Justify.Right });
        table.AddColumn(new TableColumn("Copiate ora") { Alignment = Justify.Right });
        table.AddColumn(new TableColumn("Non copiate (errori)") { Alignment = Justify.Right });
        table.AddColumn(new TableColumn("Doppioni in origine") { Alignment = Justify.Right });
        table.AddColumn(new TableColumn("In più (rimovibili)") { Alignment = Justify.Right });

        foreach (var row in rows)
        {
            table.AddRow(
                Ui.Truncate(row.Folder.EscapeMarkup(), 45),
                row.SourceCount.ToString(),
                row.DestCount.ToString(),
                row.Copied == 0 ? "[grey]0[/]" : $"[green]{row.Copied}[/]",
                row.Failed == 0 ? "[grey]0[/]" : $"[red]{row.Failed}[/]",
                row.SourceDuplicates == 0 ? "[grey]0[/]" : $"[yellow]{row.SourceDuplicates}[/]",
                row.Removable == 0 ? "[grey]0[/]" : $"[yellow]{row.Removable}[/]");
        }

        table.AddRow(
            "[bold]Totale[/]",
            $"[bold]{rows.Sum(r => r.SourceCount)}[/]",
            $"[bold]{rows.Sum(r => r.DestCount)}[/]",
            $"[bold]{rows.Sum(r => r.Copied)}[/]",
            $"[bold]{rows.Sum(r => r.Failed)}[/]",
            $"[bold]{rows.Sum(r => r.SourceDuplicates)}[/]",
            $"[bold]{rows.Sum(r => r.Removable)}[/]");

        AnsiConsole.Write(table);
    }
}
