namespace RingOrder.Epos.Online;

/// <summary>
/// Where the RingOrder cloud lives.
/// <para>
/// Compiled in, because it is the same for every shop and it is not a secret.
/// It used to arrive in each merchant's <c>secrets.json</c>, which meant a file
/// edited by hand per shop for a value that never differs — the sort of
/// configuration that exists only because somebody made it configurable.
/// </para>
/// </summary>
public static class CloudEndpoint
{
    /// <summary>The service every shipped till talks to.</summary>
    public const string Default = "https://epos-project-production.up.railway.app";

    /// <summary>
    /// The address to actually use: the shop's override when it has one, and
    /// the built-in otherwise.
    /// <para>
    /// The override exists for pointing a development till at a staging service.
    /// It is not something a merchant is ever asked to fill in.
    /// </para>
    /// </summary>
    public static string Resolve(string? overrideUrl) =>
        string.IsNullOrWhiteSpace(overrideUrl) ? Default : overrideUrl.Trim().TrimEnd('/');
}
