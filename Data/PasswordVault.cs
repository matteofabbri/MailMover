using System.Text;

namespace MailMover.Data;

public static class PasswordVault
{
    // Offuscamento reversibile, NON cifratura: serve solo a non salvare
    // la password in chiaro dentro il file sqlite.
    private static readonly byte[] Key = "mailmover-obfuscation-key-v1"u8.ToArray();

    public static string Protect(string plainText)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(plainText);
        Xor(bytes);
        return Convert.ToBase64String(bytes);
    }

    public static string Unprotect(string encodedText)
    {
        byte[] bytes = Convert.FromBase64String(encodedText);
        Xor(bytes);
        return Encoding.UTF8.GetString(bytes);
    }

    private static void Xor(byte[] bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] ^= Key[i % Key.Length];
    }
}
