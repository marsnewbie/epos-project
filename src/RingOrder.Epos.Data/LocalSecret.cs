using System.Security.Cryptography;
using System.Text;

namespace RingOrder.Epos.Data;

/// <summary>
/// Secrets that have to live on the merchant's machine, encrypted at rest with
/// Windows DPAPI.
/// <para>
/// The exposure this closes is not someone sitting at the till — it is the
/// database leaving the shop. <c>data.sqlite</c> is copied nightly into
/// <c>backups/</c>, and a copy goes to us whenever anyone investigates
/// anything. A website password and a paid lookup key in the clear travel with
/// every one of those.
/// </para>
/// </summary>
public static class LocalSecret
{
    /// <summary>
    /// Marks a value as encrypted. Self-describing on purpose: a settings row
    /// written before this existed is read as it always was, so nothing needs a
    /// migration and nothing breaks if a shop downgrades.
    /// </summary>
    private const string Prefix = "dpapi:";

    /// <summary>Encrypts for storage. Empty stays empty — there is nothing to hide.</summary>
    public static string Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        if (plain.StartsWith(Prefix, StringComparison.Ordinal)) return plain;   // already done
        if (!OperatingSystem.IsWindows()) return plain;

        try
        {
            return Prefix + Convert.ToBase64String(Encrypt(Encoding.UTF8.GetBytes(plain)));
        }
        catch (CryptographicException)
        {
            // Storing it readable beats losing a merchant's credentials. The
            // caller cannot do anything useful with a failure here, and a till
            // that would not save its settings is the worse outcome.
            return plain;
        }
    }

    /// <summary>
    /// Machine scope, not user scope.
    /// <para>
    /// The till's whole data model is machine-wide — a shop signing into a
    /// second Windows account must not find an empty till, and the print worker
    /// may not be running as the person who typed the key. Per-user protection
    /// would encrypt a value that the next account cannot read, turning a
    /// security measure into a support call.
    /// </para>
    /// <para>
    /// The trade is that any account on that PC can decrypt it. That is the
    /// right trade here: the till is a single-purpose machine behind a counter,
    /// and the threat being closed is the copy that leaves the building.
    /// </para>
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static byte[] Encrypt(byte[] plain) =>
        ProtectedData.Protect(plain, null, DataProtectionScope.LocalMachine);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static byte[] Decrypt(byte[] cipher) =>
        ProtectedData.Unprotect(cipher, null, DataProtectionScope.LocalMachine);

    /// <summary>
    /// Decrypts for use, and passes through anything written before this
    /// existed.
    /// </summary>
    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored;

        if (!OperatingSystem.IsWindows()) return "";

        var payload = stored[Prefix.Length..];
        try
        {
            return Encoding.UTF8.GetString(Decrypt(Convert.FromBase64String(payload)));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // DPAPI is bound to the machine, so a database restored onto
            // different hardware cannot read these back. Returning empty is
            // correct and recoverable: the shop retypes the key. Returning the
            // ciphertext would send it to the website as a password.
            return "";
        }
    }

    /// <summary>True when a stored value is already encrypted.</summary>
    public static bool IsProtected(string? stored) =>
        stored is not null && stored.StartsWith(Prefix, StringComparison.Ordinal);
}
