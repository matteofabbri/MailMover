using MailMover.Models;

namespace MailMover.Accounts;

public sealed record ProviderPreset(string Host, int Port, int Security, bool RequiresAppPassword);

public static class ProviderPresets
{
    private static readonly Dictionary<string, ProviderPreset> Presets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gmail.com"] = new("imap.gmail.com", 993, Account.SecuritySsl, true),
        ["googlemail.com"] = new("imap.gmail.com", 993, Account.SecuritySsl, true),

        ["outlook.com"] = new("outlook.office365.com", 993, Account.SecuritySsl, true),
        ["hotmail.com"] = new("outlook.office365.com", 993, Account.SecuritySsl, true),
        ["hotmail.it"] = new("outlook.office365.com", 993, Account.SecuritySsl, true),
        ["live.com"] = new("outlook.office365.com", 993, Account.SecuritySsl, true),
        ["live.it"] = new("outlook.office365.com", 993, Account.SecuritySsl, true),
        ["msn.com"] = new("outlook.office365.com", 993, Account.SecuritySsl, true),

        ["icloud.com"] = new("imap.mail.me.com", 993, Account.SecuritySsl, true),
        ["me.com"] = new("imap.mail.me.com", 993, Account.SecuritySsl, true),

        ["yahoo.com"] = new("imap.mail.yahoo.com", 993, Account.SecuritySsl, true),
        ["yahoo.it"] = new("imap.mail.yahoo.com", 993, Account.SecuritySsl, true),

        ["libero.it"] = new("imaplibero.libero.it", 993, Account.SecuritySsl, false),
        ["virgilio.it"] = new("in.virgilio.it", 993, Account.SecuritySsl, false),
        ["alice.it"] = new("in.alice.it", 993, Account.SecuritySsl, false),
        ["tin.it"] = new("imap.tin.it", 993, Account.SecuritySsl, false),
        ["tiscali.it"] = new("imap.tiscali.it", 993, Account.SecuritySsl, false),
        ["aruba.it"] = new("imap.aruba.it", 993, Account.SecuritySsl, false),
        ["fastwebnet.it"] = new("imap.fastwebnet.it", 993, Account.SecuritySsl, false),
        ["poste.it"] = new("postemail.poste.it", 993, Account.SecuritySsl, false),

        ["zoho.com"] = new("imap.zoho.com", 993, Account.SecuritySsl, true),

        // Caselle ospitate su OVH (MX mx*.mail.ovh.net), es. domini personali
        ["matteofabbri.org"] = new("ssl0.ovh.net", 993, Account.SecuritySsl, false),
    };

    public static ProviderPreset? Find(string email)
    {
        int at = email.LastIndexOf('@');
        if (at < 0 || at == email.Length - 1)
            return null;

        return Presets.TryGetValue(email[(at + 1)..], out var preset) ? preset : null;
    }
}
