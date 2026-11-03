using System.Text.RegularExpressions;

namespace Beko.Agents.Application.Agents.Guardrails;

/// <summary>Kinds of personal data the regex/checksum detector finds.</summary>
public static class PiiKinds
{
    public const string TurkishId = "tckn";
    public const string Iban = "iban";
    public const string Phone = "phone";
    public const string Email = "email";
}

/// <summary>A finding. The matched value is deliberately not kept, so findings can be logged and stored safely.</summary>
public sealed record PiiMatch(string Kind, int Start, int Length);

/// <summary>
/// Fast, deterministic PII detection for Turkish text: TC Kimlik No (with checksum), Turkish IBAN (with mod-97 check),
/// Turkish mobile phone numbers and e-mail addresses. Overlapping matches are resolved in that priority order.
/// </summary>
public static partial class PiiDetector
{
    private const RegexOptions Options = RegexOptions.CultureInvariant;

    private const int RegexTimeoutMs = 200;

    /// <summary>Mask used by <see cref="Redact"/>.</summary>
    public static string MaskFor(string kind) => kind switch
    {
        PiiKinds.TurkishId => "[TC KİMLİK NO]",
        PiiKinds.Iban => "[IBAN]",
        PiiKinds.Phone => "[TELEFON]",
        PiiKinds.Email => "[E-POSTA]",
        _ => "[GİZLİ]",
    };

    public static IReadOnlyList<PiiMatch> Detect(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var found = new List<PiiMatch>();
        Collect(found, IbanRegex().Matches(text), PiiKinds.Iban, m => IsValidIban(m.Value));
        Collect(found, EmailRegex().Matches(text), PiiKinds.Email, _ => true);
        Collect(found, TurkishIdRegex().Matches(text), PiiKinds.TurkishId, m => IsValidTurkishId(m.Value));
        Collect(found, PhoneRegex().Matches(text), PiiKinds.Phone, _ => true);
        return [.. found.OrderBy(m => m.Start)];
    }

    /// <summary>Replaces every finding with its mask.</summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var matches = Detect(text);
        if (matches.Count == 0)
        {
            return text;
        }

        var result = new System.Text.StringBuilder(text.Length);
        var position = 0;
        foreach (var match in matches)
        {
            result.Append(text, position, match.Start - position).Append(MaskFor(match.Kind));
            position = match.Start + match.Length;
        }

        return result.Append(text, position, text.Length - position).ToString();
    }

    /// <summary>TC Kimlik No: 11 digits, no leading zero, 10th and 11th digits follow the official checksums.</summary>
    public static bool IsValidTurkishId(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != 11 || value[0] == '0' || !value.All(char.IsAsciiDigit))
        {
            return false;
        }

        var d = value.Select(c => c - '0').ToArray();
        var oddSum = d[0] + d[2] + d[4] + d[6] + d[8];
        var evenSum = d[1] + d[3] + d[5] + d[7];
        var tenth = (((oddSum * 7) - evenSum) % 10 + 10) % 10;
        var eleventh = d.Take(10).Sum() % 10;
        return d[9] == tenth && d[10] == eleventh;
    }

    /// <summary>Turkish IBAN: <c>TR</c> + 24 digits, valid ISO 7064 mod-97 check.</summary>
    public static bool IsValidIban(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var iban = new string([.. value.Where(c => !char.IsWhiteSpace(c))]).ToUpperInvariant();
        if (iban.Length != 26 || !iban.StartsWith("TR", StringComparison.Ordinal) || !iban.Skip(2).All(char.IsAsciiDigit))
        {
            return false;
        }

        var rearranged = string.Concat(iban.AsSpan(4), iban.AsSpan(0, 4));
        var remainder = 0;
        foreach (var c in rearranged)
        {
            var digits = char.IsAsciiDigit(c) ? (c - '0').ToString(System.Globalization.CultureInfo.InvariantCulture) : (c - 'A' + 10).ToString(System.Globalization.CultureInfo.InvariantCulture);
            foreach (var digit in digits)
            {
                remainder = ((remainder * 10) + (digit - '0')) % 97;
            }
        }

        return remainder == 1;
    }

    private static void Collect(List<PiiMatch> found, MatchCollection matches, string kind, Func<Match, bool> accept)
    {
        foreach (Match match in matches)
        {
            var overlaps = found.Any(f => match.Index < f.Start + f.Length && f.Start < match.Index + match.Length);
            if (!overlaps && accept(match))
            {
                found.Add(new PiiMatch(kind, match.Index, match.Length));
            }
        }
    }

    [GeneratedRegex(@"\bTR\d{2}(?:[ ]?\d{4}){5}[ ]?\d{2}\b", Options | RegexOptions.IgnoreCase, RegexTimeoutMs)]
    private static partial Regex IbanRegex();

    [GeneratedRegex(@"(?<![\w.+-])[\w.+-]+@[\w-]+(?:\.[\w-]+)+(?!\w)", Options, RegexTimeoutMs)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"(?<!\d)\d{11}(?!\d)", Options, RegexTimeoutMs)]
    private static partial Regex TurkishIdRegex();

    // Turkish mobile numbers: optional +90 / 0090 / 0 prefix, then 5xx xxx xx xx with optional spaces, dashes or dots.
    [GeneratedRegex(@"(?<![\d+])(?:(?:\+90|0090|0)[ .-]?)?\(?5\d{2}\)?[ .-]?\d{3}[ .-]?\d{2}[ .-]?\d{2}(?!\d)", Options, RegexTimeoutMs)]
    private static partial Regex PhoneRegex();
}
