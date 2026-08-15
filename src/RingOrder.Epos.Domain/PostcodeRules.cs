using System.Text.RegularExpressions;

namespace RingOrder.Epos.Domain;

/// <summary>How much of the country a delivery rule covers.</summary>
public enum PostcodeRuleLevel
{
    /// <summary>"B" — every postcode in the area.</summary>
    Area = 1,

    /// <summary>"B44" — the whole district.</summary>
    District = 2,

    /// <summary>"B44 0" — one sector of it.</summary>
    Sector = 3,

    /// <summary>"B44 0QN" — a single postcode.</summary>
    Unit = 4,
}

/// <summary>A delivery rule's prefix, parsed into what it actually covers.</summary>
public sealed record PostcodeRule(
    PostcodeRuleLevel Level,
    string Area,
    string? Outward,
    string? Sector,
    string? Unit,
    string Canonical);

/// <summary>
/// Parses the prefix a merchant types against a delivery zone.
/// <para>
/// Matching is on structured postcode components and <b>never on a naive string
/// prefix</b>. B47 must not match a B44 rule — they are different districts on
/// opposite sides of a city — and "B44 3" must not match a "B44 0" rule. The
/// space is significant: "B44 0" is a sector, "B40" is a district, and a
/// spelling that squashes them together turns one into the other.
/// </para>
/// <para>
/// This mirrors the rule engine in the RingOrder website
/// (<c>src/lib/delivery/postcode.ts</c>) deliberately. A shop that takes web
/// orders <i>and</i> phone orders must quote the same delivery charge on both,
/// and the fastest way to break that is for the two products to disagree about
/// what a prefix means.
/// </para>
/// </summary>
public static class PostcodeRules
{
    private const string Outward = "[A-Z]{1,2}[0-9][A-Z0-9]?";

    private static readonly Regex OutwardRe = new($"^{Outward}$", RegexOptions.Compiled);
    private static readonly Regex AreaRe = new("^[A-Z]{1,2}$", RegexOptions.Compiled);
    private static readonly Regex FullRe = new($"^({Outward})([0-9][A-Z]{{2}})$", RegexOptions.Compiled);
    private static readonly Regex SectorNoSpaceRe = new($"^({Outward})([0-9])$", RegexOptions.Compiled);

    /// <summary>Null when the prefix is not something that can be matched against.</summary>
    public static PostcodeRule? Parse(string? input)
    {
        var raw = Regex.Replace((input ?? "").Trim().ToUpperInvariant(), @"\s+", " ");
        if (raw.Length == 0) return null;

        if (raw.Contains(' '))
        {
            var parts = raw.Split(' ');
            var left = parts[0];
            var right = string.Concat(parts.Skip(1));

            if (!OutwardRe.IsMatch(left)) return null;
            var area = AreaOf(left);

            if (right.Length == 0)
                return new PostcodeRule(PostcodeRuleLevel.District, area, left, null, null, left);

            if (right.Length == 1 && char.IsDigit(right[0]))
                return new PostcodeRule(
                    PostcodeRuleLevel.Sector, area, left, $"{left} {right}", null, $"{left} {right}");

            if (Regex.IsMatch(right, "^[0-9][A-Z]{2}$"))
                return new PostcodeRule(
                    PostcodeRuleLevel.Unit, area, left, $"{left} {right[0]}", $"{left} {right}", $"{left} {right}");

            return null;
        }

        if (AreaRe.IsMatch(raw))
            return new PostcodeRule(PostcodeRuleLevel.Area, raw, null, null, null, raw);

        // A whole postcode pasted without its space, e.g. "B440QN".
        var full = FullRe.Match(raw);
        if (full.Success)
        {
            var outward = full.Groups[1].Value;
            var inward = full.Groups[2].Value;
            return new PostcodeRule(
                PostcodeRuleLevel.Unit, AreaOf(outward), outward,
                $"{outward} {inward[0]}", $"{outward} {inward}", $"{outward} {inward}");
        }

        if (OutwardRe.IsMatch(raw))
            return new PostcodeRule(PostcodeRuleLevel.District, AreaOf(raw), raw, null, null, raw);

        // Outward plus a sector digit and no space, e.g. "B440" → sector "B44 0".
        // Reached only after the district test above, so "B40" stays a district.
        var sector = SectorNoSpaceRe.Match(raw);
        if (sector.Success)
        {
            var outward = sector.Groups[1].Value;
            var digit = sector.Groups[2].Value;
            return new PostcodeRule(
                PostcodeRuleLevel.Sector, AreaOf(outward), outward,
                $"{outward} {digit}", null, $"{outward} {digit}");
        }

        return null;
    }

    /// <summary>Does this rule cover that postcode? Exact on the rule's own level.</summary>
    public static bool Covers(PostcodeRule rule, UkPostcode postcode)
    {
        if (!postcode.IsValid) return false;

        return rule.Level switch
        {
            PostcodeRuleLevel.Area => postcode.Area == rule.Area,
            PostcodeRuleLevel.District => postcode.Outward == rule.Outward,
            PostcodeRuleLevel.Sector => postcode.Sector == rule.Sector,
            PostcodeRuleLevel.Unit => postcode.Unit == rule.Unit,
            _ => false,
        };
    }

    /// <summary>Tidied for display, or the cleaned input when it cannot be parsed.</summary>
    public static string Canonical(string? input) =>
        Parse(input)?.Canonical ?? Regex.Replace((input ?? "").Trim().ToUpperInvariant(), @"\s+", " ");

    /// <summary>What the merchant just typed, in words, so a mistake is visible.</summary>
    public static string? Describe(string? input, bool zh = false)
    {
        var rule = Parse(input);
        if (rule is null) return null;

        return rule.Level switch
        {
            PostcodeRuleLevel.Area => zh
                ? $"区域 {rule.Area} — 所有 {rule.Area} 开头的邮编"
                : $"Area {rule.Area} — every postcode starting {rule.Area}",
            PostcodeRuleLevel.District => zh
                ? $"分区 {rule.Outward} — 整个 {rule.Outward}"
                : $"District {rule.Outward} — all of {rule.Outward}",
            PostcodeRuleLevel.Sector => zh
                ? $"仅 {rule.Canonical} 这一段"
                : $"Sector {rule.Canonical} only",
            _ => zh
                ? $"单个邮编 {rule.Canonical}"
                : $"Single postcode {rule.Canonical}",
        };
    }

    private static string AreaOf(string outward) =>
        new(outward.TakeWhile(char.IsLetter).ToArray());
}
