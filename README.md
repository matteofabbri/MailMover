# MailMover

Applicazione console in C# (.NET 8) che:

- si connette a un server mail tramite **IMAP** (con [MailKit](https://github.com/jstedfast/MailKit));
- fa il **backup completo** di una casella — tutte le cartelle e tutti i messaggi — salvandolo in un database **SQLite** locale (i messaggi sono conservati come blob MIME originali);
- permette di **copiare il backup su un altro server / altro account**, ricreando la gerarchia delle cartelle e preservando flag (letto, risposto, …) e data interna di ogni messaggio;
- usa **Spectre.Console** per menu, barre di avanzamento e report.

## Requisiti

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

## Avvio

```bash
dotnet run
```

Al primo avvio viene creato il database `mailmover.db` nella directory corrente. È anche possibile inizializzare il database senza aprire il menu con `dotnet run -- --init-db`.

## Uso

1. **Gestisci account** → *Aggiungi account*: inserisci nome e indirizzo email; se il dominio è tra quelli riconosciuti (gmail.com, outlook.com/hotmail/live, icloud.com, yahoo.com/it, libero.it, virgilio.it, alice.it, tin.it, tiscali.it, aruba.it, fastwebnet.it, poste.it, zoho.com, matteofabbri.org/OVH) server, porta e connessione SSL vengono proposti automaticamente e modificabili. *Modifica account* permette di correggere qualsiasi campo (nome, server, porta, cifratura, username, password, opzione SSL) senza cancellare e ricreare, con i valori attuali proposti come default. Al termine viene offerto subito il test di connessione. In creazione e modifica si può configurare anche un **tunnel SSH di fallback via PuTTY** (vedi Note). Per i provider Gmail, Outlook, iCloud, Yahoo e Zoho serve una **password per app** (generabile dalle impostazioni di sicurezza dell'account con verifica in due passaggi attiva).
2. **Backup di un account**: scarica tutte le cartelle (vengono scartate solo quelle non selezionabili) e tutti i messaggi. La barra di avanzamento mostra l'avanzamento totale e per singola cartella. Il backup è **incrementale**: rieseguirlo scarica solo i messaggi nuovi (i messaggi già presenti vengono riconosciuti via UID e saltati).
3. **Copia un backup su un altro account**: scegli il backup di partenza e l'account di destinazione. Le cartelle vengono ricreate sulla destinazione (compatendo un eventuale separatore diverso, es. `/` → `.`) e i messaggi vengono caricati con `APPEND` conservando flag, keyword/etichette e data originale. **Nessuna mail viene inserita due volte**: i messaggi già copiati sono tracciati nella tabella `copy_log` e, come ulteriore garanzia, prima di caricare una cartella viene scaricato l'indice dei Message-ID già presenti sul server di destinazione (le mail trovate vengono saltate e registrate). La copia è **riprendibile**: se la interrompi, rilanci e riparte solo dai messaggi mancanti.
4. **Confronta e sincronizza due account (copia 1:1)**: collega due account IMAP insieme e rende la destinazione identica all'origine. Copia le mail mancanti (riconosciute per **Message-ID**, oppure per **hash del contenuto normalizzato** se prive di id: oggetto, mittenti in minuscolo, data UTC e corpi testo+HTML — nessun pre-filtro), con flag, etichette e data originale. Se il server chiude la connessione durante la copia (capita con caselle grandi), il programma **si riconnette da solo e riprende dalla mail interrotta**, ricostruendo l'indice della destinazione per non duplicare nulla; se la riconnessione non basta, la sessione si ferma pulita con un solo errore, invece di generarne uno per ogni mail rimanente. La tabella finale mostra per cartella: messaggi su origine e destinazione, mail copiate, **non copiate per errori**, **doppioni interni all'origine** (lo stesso messaggio presente più volte nel vecchio account) e **mail in più** sulla destinazione. Al termine, su conferma esplicita, le mail in più vengono **eliminate definitivamente** dal nuovo account — con "copia 1:1" dichiarata solo se la sessione si è conclusa senza errori. Barra di avanzamento mail per mail e tracciato dettagliato in `mailmover-sync.log`.
5. **Analizza un account (duplicati e statistiche)**: diagnostica in sola lettura. Per ogni cartella mostra totale messaggi, Message-ID distinti, duplicati interni alla cartella e mail senza Message-ID; segnala anche i Message-ID presenti in più cartelle. Serve a capire se un account contiene duplicati o mail collocate in cartelle diverse da quelle attese, prima di applicare una correzione.
6. **Esplora un account (leggi/elimina mail)**: gestione manuale della posta di un account salvato. Navigazione delle cartelle, elenco paginato delle mail (più recenti prima, ✉ = non letta), lettura del contenuto (intestazioni, corpo testo, elenco allegati), segna letta/non letta, eliminazione singola con conferma, svuotamento di una cartella e **svuotamento completo dell'account** (⚡): elimina tutte le mail e tutte le cartelle, comprese quelle annidate, dalle più profonde alle più esterne. L'unica cartella che sopravvive è INBOX (IMAP non ne permette la rimozione), che viene comunque svuotata. Protetto da doppia conferma con digitazione della parola ELIMINA.
7. **Elimina backup di un account**: libera spazio nel database rimuovendo i messaggi di un backup.

## Schema del database

| Tabella | Contenuto |
|---|---|
| `accounts` | Account IMAP configurati (host, porta, credenziali, tipo di sicurezza) |
| `backup_meta` | Delimitatore cartelle del server di origine e data ultimo backup |
| `messages` | Messaggi di backup: cartella, UID, metadata (Message-ID, oggetto, mittente, data, flag), hash SHA-256 e contenuto MIME completo (`raw` BLOB) |
| `copy_log` | Quali messaggi sono già stati copiati su quale destinazione (per il resume) |

## Struttura del progetto

| Cartella | Contenuto |
|---|---|
| `Program.cs` (radice) | Menù principale e avvio |
| `ConsoleUi/` | Prompt, tabelle e report condivisi (Spectre.Console) |
| `Accounts/` | Creazione/modifica account, preset dei provider, test connessione |
| `Services/` | Operazioni: backup, copia backup, sincronizzazione 1:1, analisi, esplora account |
| `Data/` | Database SQLite e offuscamento delle password |
| `Mail/` | Connessioni IMAP con retry, tunnel PuTTY (plink), utilità MailKit |
| `Models/` | Modelli (account, configurazione tunnel) |

## Note e limiti

- **Doppioni e confronto campo per campo**: una mail è dichiarata doppione solo se ha lo **stesso Message-ID E gli stessi campi** (oggetto, mittente, data, corpi testo e HTML — l'impronta del contenuto). Il confronto dei campi viene fatto scaricando la mail solo dove serve davvero: quando lo stesso Message-ID compare più volte. Se l'id è riusato da mail diverse, le versioni sono trattate come **distinte**: nessuna viene scartata, entrambe vengono copiate e garantite sulla destinazione. Mail senza Message-ID: il confronto è direttamente per contenuto. Questo vale sia per la sincronizzazione 1:1 sia per l'analisi account.
- **Password**: vengono salvate nel database solo offuscate (reversibile): non è cifratura reale, chi ha accesso al file `mailmover.db` può recuperarle. Non lasciare il database su supporti non protetti.
- **Retry e tunnel PuTTY**: ogni connessione IMAP viene riprovata fino a **3 volte** (con attesa crescente) prima di dichiarare il fallimento; gli errori di credenziali non vengono riprovati. Se l'account è configurato con un **tunnel SSH** e la connessione diretta fallisce, MailMover apre da solo un tunnel con `plink.exe` (deve essere installato [PuTTY](https://www.putty.org/), cercato nel PATH e in `C:\Program Files\PuTTY\`) e si connette attraverso `127.0.0.1` — utile quando il server IMAP è irraggiungibile direttamente (firewall, ban temporaneo dell'IP, porte chiuse). Prima del primo uso non interattivo la chiave del server SSH deve essere già stata accettata: apri una volta plink (o PuTTY) verso il server e confermala. Se imposti una password SSH, passa sulla riga di comando di plink (visibile nell'elenco processi): per maggiore sicurezza usa l'autenticazione con chiave lasciando vuoto il campo. Con il tunnel attivo la connessione è già cifrata da SSH, quindi il certificato IMAP non viene verificato.
- **Ignora errori SSL**: con l'opzione *Attiva/disattiva ignora errori SSL* (o la domanda in fase di creazione) il programma non verifica più il certificato del server. Comoda per server self-signed, ma disattiva una protezione: attivala solo su server di cui ti fidi. L'opzione è per account ed è visibile nell'elenco account con ⚠.
- **Gmail e provider simili**: serve IMAP abilitato e una *password per app*.
- **Dimensioni**: il database cresce più o meno quanto la mailbox di origine (i messaggi sono salvati byte per byte).
- **Cartelle speciali**: su alcuni server (es. Gmail) cartelle come "Tutti i messaggi" duplicano il contenuto delle altre cartelle: il backup contiene entrambe le copie.
- **Interruzione**: `Ctrl+C` termina il programma; i dati già scritti sono al sicuro (le scritture avvengono in transazioni a lotti) e le operazioni possono essere riprese.
