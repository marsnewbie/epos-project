using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RingOrder.Epos.Domain;

namespace RingOrder.Epos.Online;

/// <summary>Why a token did not produce an entitlement. Diagnostic, never a reason to stop.</summary>
public enum TokenProblem
{
    None,

    /// <summary>Nothing stored yet — the ordinary state of a till before its first sync.</summary>
    Missing,

    /// <summary>Not two base64url parts, or the payload is not the JSON we expect.</summary>
    Malformed,

    /// <summary>Parsed, but no configured key verifies it. Forged, corrupt, or signed by a key we retired.</summary>
    BadSignature,

    /// <summary>A payload version this build does not know. See the note on <see cref="EntitlementToken"/>.</summary>
    UnknownVersion,

    /// <summary>No public key is configured, so nothing can be verified at all.</summary>
    NoKeys,
}

/// <param name="Entitlement">The verified payload, or null.</param>
/// <param name="Problem">What went wrong, for the log and the diagnostics export.</param>
public sealed record TokenVerification(Entitlement? Entitlement, TokenProblem Problem)
{
    public bool Ok => Entitlement is not null;
}

/// <summary>
/// The wire format of a signed entitlement, and the only place it is read.
/// <para>
/// <c>base64url(payload) . base64url(signature)</c> — the JWT shape without the
/// header, because a header exists to negotiate an algorithm and there is
/// exactly one algorithm here. Every <c>alg</c> confusion vulnerability ever
/// written up came from that negotiation, and a closed single-vendor system
/// gains nothing by keeping the door.
/// </para>
/// <para>
/// <b>ECDSA P-256 over SHA-256, signature in IEEE P1363 form</b> — the raw
/// 64-byte <c>r||s</c>, which is what .NET produces natively and what Node
/// produces when told <c>dsaEncoding: "ieee-p1363"</c>. Node's default is DER
/// and the two are not interchangeable, so the service must set it explicitly.
/// The fixtures in <c>fixtures/entitlement</c> are what hold both sides to it.
/// </para>
/// <para>
/// <b>Serialisation options are pinned here rather than shared.</b> This is a
/// contract with software already installed in shops; inheriting a repository-wide
/// serialiser would mean somebody tidying a naming policy could silently change
/// what a till in Birmingham is able to read.
/// </para>
/// </summary>
public static class EntitlementToken
{
    /// <summary>
    /// Payload versions this build understands.
    /// <para>
    /// A higher version is refused rather than guessed at, and the till falls
    /// back to its bundle. The version only moves when a field changes meaning,
    /// which is a deliberate break that ships as a new endpoint — an addition
    /// never moves it, because unknown fields are ignored.
    /// </para>
    /// </summary>
    public const int SupportedVersion = 1;

    private static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,

        // Unknown members are skipped, which is what lets the service add a
        // field without breaking every till that has not updated yet. Setting
        // this to Disallow would turn the next addition into an estate-wide
        // outage; it is stated rather than left to the default so that changing
        // it has to be a decision.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    /// <summary>
    /// Verify a stored token against the configured public keys.
    /// <para>
    /// Expiry is deliberately <em>not</em> checked here. Whether an expired
    /// token still governs the till is a policy question, and it is answered in
    /// <see cref="EntitlementPolicy"/> — which answers "yes, and say so".
    /// </para>
    /// </summary>
    /// <param name="token">The stored token, or null before the first sync.</param>
    /// <param name="publicKeys">
    /// Base64 SubjectPublicKeyInfo, current key first. More than one is
    /// supported so a signing key can be rotated without every shop having to
    /// update on the same day.
    /// </param>
    public static TokenVerification Verify(string? token, IReadOnlyList<string> publicKeys)
    {
        if (string.IsNullOrWhiteSpace(token))
            return new TokenVerification(null, TokenProblem.Missing);

        if (publicKeys.Count == 0)
            return new TokenVerification(null, TokenProblem.NoKeys);

        var parts = token.Split('.');
        if (parts.Length != 2)
            return new TokenVerification(null, TokenProblem.Malformed);

        byte[] payloadBytes, signature;
        try
        {
            payloadBytes = FromBase64Url(parts[0]);
            signature = FromBase64Url(parts[1]);
        }
        catch (FormatException)
        {
            return new TokenVerification(null, TokenProblem.Malformed);
        }

        if (!AnyKeyVerifies(payloadBytes, signature, publicKeys))
            return new TokenVerification(null, TokenProblem.BadSignature);

        EntitlementPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<EntitlementPayload>(payloadBytes, Wire);
        }
        catch (JsonException)
        {
            return new TokenVerification(null, TokenProblem.Malformed);
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.DeviceId))
            return new TokenVerification(null, TokenProblem.Malformed);

        if (payload.V > SupportedVersion)
            return new TokenVerification(null, TokenProblem.UnknownVersion);

        return new TokenVerification(
            new Entitlement(
                ShopId: payload.ShopId ?? "",
                DeviceId: payload.DeviceId,
                Edition: payload.Edition ?? ShopEdition.Pos,
                Features: payload.Features ?? [],
                Terminals: payload.Terminals,
                IssuedAt: payload.IssuedAt,
                ExpiresAt: payload.ExpiresAt),
            TokenProblem.None);
    }

    /// <summary>
    /// Sign a payload. Used by the tests and by the tooling that builds the
    /// shared fixtures; the service signs with its own key and never runs this.
    /// </summary>
    public static string Sign(Entitlement entitlement, ECDsa privateKey, int version = SupportedVersion)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new EntitlementPayload
            {
                V = version,
                ShopId = entitlement.ShopId,
                DeviceId = entitlement.DeviceId,
                Edition = entitlement.Edition,
                Features = [.. entitlement.Features],
                Terminals = entitlement.Terminals,
                IssuedAt = entitlement.IssuedAt,
                ExpiresAt = entitlement.ExpiresAt,
            },
            Wire);

        var signature = privateKey.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        return $"{ToBase64Url(payload)}.{ToBase64Url(signature)}";
    }

    private static bool AnyKeyVerifies(byte[] payload, byte[] signature, IReadOnlyList<string> publicKeys)
    {
        foreach (var spki in publicKeys)
        {
            if (string.IsNullOrWhiteSpace(spki)) continue;

            try
            {
                using var key = ECDsa.Create();
                key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(spki.Trim()), out _);

                if (key.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                    return true;
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException)
            {
                // A malformed key in configuration must not stop the others from
                // being tried, and must not throw on a startup path.
            }
        }

        return false;
    }

    internal static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static byte[] FromBase64Url(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '='));
    }

    /// <summary>
    /// The payload exactly as it crosses the wire. Kept separate from
    /// <see cref="Entitlement"/> so the domain type can be reshaped without
    /// changing what shops in the field are able to read.
    /// </summary>
    private sealed class EntitlementPayload
    {
        public int V { get; set; } = SupportedVersion;
        public string? ShopId { get; set; }
        public string? DeviceId { get; set; }
        public string? Edition { get; set; }
        public List<string>? Features { get; set; }
        public int Terminals { get; set; }
        public DateTimeOffset IssuedAt { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
    }
}
