using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using MailKit;
using MailKit.Net.Imap;
using MimeKit;

namespace MailMover.Mail;

internal static class MailKitUtils
{
    public static IMailFolder GetOrCreateFolder(
        ImapClient client, Dictionary<string, IMailFolder> cache, string[] segments, char delimiter)
    {
        var path = segments[0];
        if (!cache.TryGetValue(path, out var current))
        {
            if (string.Equals(path, "INBOX", StringComparison.OrdinalIgnoreCase))
            {
                current = client.Inbox;
            }
            else
            {
                var root = client.GetFolder(client.PersonalNamespaces[0]);
                current = GetOrCreateChild(root, path);
            }
            cache[path] = current;
        }

        for (int i = 1; i < segments.Length; i++)
        {
            path = path + delimiter + segments[i];
            if (!cache.TryGetValue(path, out var child))
            {
                child = GetOrCreateChild(current, segments[i]);
                cache[path] = child;
            }
            current = child;
        }

        return current;
    }

    private static IMailFolder GetOrCreateChild(IMailFolder parent, string name)
    {
        try
        {
            return parent.GetSubfolder(name);
        }
        catch (FolderNotFoundException)
        {
            return parent.Create(name, isMessageFolder: true)!;
        }
    }

    public static MessageFlags ParseFlags(string? text)
        => Enum.TryParse(text, out MessageFlags flags) ? flags : MessageFlags.None;

    /// <summary>
    /// Impronta dei campi di una mail, stabile tra server diversi e attraverso
    /// la ricopia: oggetto, indirizzi mittente in minuscolo, data in UTC e
    /// corpi testo+HTML decodificati. Due mail con la stessa impronta sono
    /// uguali campo per campo.
    /// </summary>
    internal static string ContentHash(IMailFolder folder, UniqueId uid)
    {
        MimeMessage message;
        using (var stream = folder.GetStream(uid))
            message = MimeMessage.Load(stream);

        string from = string.Join(",",
            message.From.Mailboxes
                .Select(mailbox => (mailbox.Address ?? string.Empty).ToLowerInvariant())
                .OrderBy(address => address, StringComparer.Ordinal));

        string payload = string.Join('\u0001',
            (message.Subject ?? string.Empty).Trim(),
            from,
            message.Date.UtcDateTime.ToString("u"),
            message.TextBody ?? string.Empty,
            message.HtmlBody ?? string.Empty);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    /// <summary>
    /// Vero quando l'eccezione indica che la connessione col server è caduta
    /// (va ricostruita e l'operazione ripresa), non un errore puntuale del
    /// singolo messaggio: i NO di protocollo come la quota piena restano
    /// errori normali.
    /// </summary>
    internal static bool IsConnectionLoss(Exception? ex)
    {
        if (ex is null)
            return false;

        if (ex is ServiceNotConnectedException
            or ServiceNotAuthenticatedException
            or ImapProtocolException
            or TimeoutException
            || ex is IOException or SocketException)
        {
            return true;
        }

        return ex.InnerException is not null && IsConnectionLoss(ex.InnerException);
    }
}
