namespace CentreStage.Core;

/// <summary>
/// Matches blend materials by name family, for the hair opacity levers.
///
/// A hair set names its parts independently: "Hair_Transparency", "BabyHair_Transparency"
/// and "Scalp_Transparency" (the painted cap that hides the skin under the parting). A
/// prefix test drops every part that does not start with "hair", which is exactly the set
/// of parts that cover the crown, so matching has to reach inside the name.
///
/// Matching is on name <em>tokens</em>, not on raw substrings: the name is split on
/// non-alphanumeric characters, on lower-to-upper transitions and on letter-to-digit
/// transitions, and a token must equal a family entry. Substring matching would also hit
/// "Chair_Fabric", "Mohair_Trim" and "Hairpin", and since the boost is on by default that
/// would silently thicken an unrelated material. Under-matching only leaves a material as
/// it was; over-matching alters assets that never asked for it, so this errs towards the
/// former.
/// </summary>
public static class BlendMaterialFamily
{
    /// <summary>
    /// The default family: hair cards and the painted scalp cap. "scalp" has to be
    /// present or a bald crown shows through every strand (measured on Mégane).
    /// </summary>
    public const string Hair = "hair,scalp";

    /// <summary>Splits a family list once, for callers that match many materials.</summary>
    public static string[] Parse(string? families) =>
        string.IsNullOrEmpty(families)
            ? []
            : families.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static bool Matches(string? materialName, string? families) =>
        Matches(materialName, Parse(families));

    public static bool Matches(string? materialName, ReadOnlySpan<string> families)
    {
        if (string.IsNullOrEmpty(materialName) || families.IsEmpty)
        {
            return false;
        }

        var start = -1;
        for (var i = 0; i < materialName.Length; i++)
        {
            var current = materialName[i];
            if (!char.IsLetterOrDigit(current))
            {
                if (start >= 0 && MatchesToken(materialName.AsSpan(start, i - start), families))
                {
                    return true;
                }

                start = -1;
                continue;
            }

            if (start < 0)
            {
                start = i;
                continue;
            }

            if (IsTokenStart(materialName, i))
            {
                if (MatchesToken(materialName.AsSpan(start, i - start), families))
                {
                    return true;
                }

                start = i;
            }
        }

        return start >= 0 && MatchesToken(materialName.AsSpan(start), families);
    }

    /// <summary>
    /// True where a new token starts inside a run of alphanumerics: before an upper case
    /// letter that follows a lower case letter or a digit ("BabyHair", "hair2Cards"), and
    /// before the last capital of an acronym run that is followed by lower case
    /// ("HAIRTransparency", "FPHair_Cards"). Without the acronym rule the whole run stays
    /// one token and a hair part named that way is silently left unboosted.
    /// </summary>
    private static bool IsTokenStart(ReadOnlySpan<char> name, int i)
    {
        var previous = name[i - 1];
        var current = name[i];
        if (char.IsDigit(current))
        {
            return char.IsLetter(previous);
        }

        if (char.IsDigit(previous))
        {
            return true;
        }

        return char.IsUpper(current)
            && (char.IsLower(previous)
                || (char.IsUpper(previous) && i + 1 < name.Length && char.IsLower(name[i + 1])));
    }

    private static bool MatchesToken(ReadOnlySpan<char> token, ReadOnlySpan<string> families)
    {
        foreach (var family in families)
        {
            if (family.Length > 0 && token.Equals(family, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}