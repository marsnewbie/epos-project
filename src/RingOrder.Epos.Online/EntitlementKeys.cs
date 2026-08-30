namespace RingOrder.Epos.Online;

/// <summary>
/// The public keys a shipped till will accept an entitlement from.
/// <para>
/// Compiled in rather than configured. A merchant who could point their till at
/// a key of their own could sign themselves any plan they liked, and this is the
/// one value in the whole design that has to be beyond their reach.
/// </para>
/// </summary>
public static class EntitlementKeys
{
    /// <summary>
    /// Base64 SubjectPublicKeyInfo, current key first.
    /// <para>
    /// <b>Empty on purpose until a production key exists.</b> With no key
    /// nothing verifies, every till falls back to the edition in its bundle, and
    /// that is exactly the documented behaviour for a shop that has never
    /// reached the cloud — so a build that ships before the key is generated
    /// behaves correctly rather than mysteriously.
    /// </para>
    /// <para>
    /// <b>Two entries, not one, once it is populated:</b> the key in use and its
    /// successor. Rotating a signing key is impossible without a period where
    /// both are accepted, and the day that is needed is the day you cannot ship
    /// an update to everyone first.
    /// </para>
    /// <para>
    /// The development key in <c>fixtures/entitlement</c> is deliberately absent
    /// from this list. Its private half is in the repository, so a build that
    /// trusted it would accept a token anybody could mint.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> Production { get; } =
    [
        // Generated 2026-08-31 by tools/keygen.mjs; the private half lives in the
        // service's SIGNING_KEY and in an offline backup. A successor goes below
        // this line when one exists — both are accepted during a rotation.
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEhuhK6mT30bciyPaY/clStLkIdKH52lAnn4YvKtnAHNmroBwZTFedG7S0fPVOvoLDqkTyX08FcKkiU/mIYO2Ysw==",
    ];
}
