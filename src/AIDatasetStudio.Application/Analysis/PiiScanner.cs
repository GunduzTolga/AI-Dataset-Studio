using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Application.Analysis;

/// <summary>
/// Kinds of personal or secret data the scanner finds (SDD §9.1).
/// </summary>
public enum PiiKind
{
    Email,
    Phone,
    NationalId,
    Iban,
    CreditCard,
    InternalIp,
    InternalUrl,
    ApiKey,
    Secret,
    CustomTerm,
}

/// <summary>
/// A piece of personal or secret data inside a text.
/// </summary>
public readonly record struct PiiMatch(PiiKind Kind, int Start, int Length, string Value);

/// <summary>
/// A payload with its personal data replaced by placeholders such as <c>[EMAIL_1]</c>.
/// </summary>
/// <param name="Counts">Distinct values found per kind.</param>
public sealed record PiiMaskResult(RecordPayload Payload, IReadOnlyDictionary<PiiKind, int> Counts)
{
    public bool HasFindings => Counts.Count > 0;
}

/// <summary>
/// Finds personal and secret data with local rules only; nothing leaves the machine (SDD §9).
/// Numbers are checked with their algorithms (T.C. kimlik checksum, IBAN mod-97, Luhn), so
/// arbitrary digit runs are not reported.
/// </summary>
public sealed partial class PiiScanner
{
    private readonly Regex? _customTerms;

    /// <param name="customTerms">Words to hide as well, e.g. company or project names; case-insensitive.</param>
    public PiiScanner(IEnumerable<string>? customTerms = null)
    {
        var terms = customTerms?.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(t => t.Length).ToList() ?? [];
        CustomTerms = terms;
        if (terms.Count > 0)
        {
            // Turkish casing: "LOJİSTİK", "lojistik" and "LOJISTIK" are the same word to the user.
            var pattern = string.Join('|', terms.Select(TermPattern));
            _customTerms = new Regex($@"(?<![\p{{L}}\p{{N}}])(?:{pattern})(?![\p{{L}}\p{{N}}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
    }

    public IReadOnlyList<string> CustomTerms { get; }

    private static string TermPattern(string term)
    {
        var builder = new StringBuilder();
        foreach (var c in Regex.Escape(term))
        {
            builder.Append(c is 'i' or 'ı' or 'I' or 'İ' ? "[iıIİ]" : c.ToString());
        }

        return builder.ToString();
    }

    /// <summary>
    /// Placeholder base for a kind, e.g. "EMAIL"; numbered per record as "[EMAIL_1]".
    /// </summary>
    public static string PlaceholderName(PiiKind kind) => kind switch
    {
        PiiKind.Email => "EMAIL",
        PiiKind.Phone => "PHONE",
        PiiKind.NationalId => "ID_NUMBER",
        PiiKind.Iban => "IBAN",
        PiiKind.CreditCard => "CREDIT_CARD",
        PiiKind.InternalIp => "INTERNAL_IP",
        PiiKind.InternalUrl => "INTERNAL_URL",
        PiiKind.ApiKey => "API_KEY",
        PiiKind.Secret => "SECRET",
        _ => "REDACTED",
    };

    /// <summary>
    /// Non-overlapping findings in text order. Where two rules match the same text, the more
    /// specific one wins (a secret over an e-mail address inside it, an IBAN over a phone number).
    /// </summary>
    public IReadOnlyList<PiiMatch> Find(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return [];
        }

        var candidates = new List<(PiiMatch Match, int Priority)>();
        void Add(PiiKind kind, int priority, int start, int length) =>
            candidates.Add((new PiiMatch(kind, start, length, text.Substring(start, length)), priority));

        foreach (var (regex, kind) in SecretPatterns)
        {
            foreach (Match m in regex.Matches(text))
            {
                // Patterns with a "value" group hide only the value, e.g. the password in "Password=…".
                var group = m.Groups["value"].Success ? m.Groups["value"] : m.Groups[0];
                Add(kind, 0, group.Index, group.Length);
            }
        }

        if (_customTerms is not null)
        {
            foreach (Match m in _customTerms.Matches(text))
            {
                Add(PiiKind.CustomTerm, 1, m.Index, m.Length);
            }
        }

        foreach (Match m in IbanRegex().Matches(text))
        {
            if (IsValidIban(m.Value))
            {
                Add(PiiKind.Iban, 2, m.Index, m.Length);
            }
        }

        foreach (Match m in CardRegex().Matches(text))
        {
            if (IsValidCard(m.Value))
            {
                Add(PiiKind.CreditCard, 3, m.Index, m.Length);
            }
        }

        foreach (Match m in NationalIdRegex().Matches(text))
        {
            if (IsValidNationalId(m.Value))
            {
                Add(PiiKind.NationalId, 4, m.Index, m.Length);
            }
        }

        foreach (Match m in EmailRegex().Matches(text))
        {
            Add(PiiKind.Email, 5, m.Index, m.Length);
        }

        foreach (Match m in InternalUrlRegex().Matches(text))
        {
            Add(PiiKind.InternalUrl, 6, m.Index, m.Length);
        }

        foreach (Match m in InternalIpRegex().Matches(text))
        {
            if (m.Value.Split('.').All(octet => int.Parse(octet, CultureInfo.InvariantCulture) <= 255))
            {
                Add(PiiKind.InternalIp, 7, m.Index, m.Length);
            }
        }

        foreach (Match m in PhoneRegex().Matches(text))
        {
            var digits = m.Value.Count(char.IsAsciiDigit);
            if (digits is >= 10 and <= 15)
            {
                Add(PiiKind.Phone, 8, m.Index, m.Length);
            }
        }

        // The most specific rule wins where findings overlap (a secret over the e-mail inside it,
        // an IBAN over the card-like digits inside it), then the longer finding.
        var accepted = new List<PiiMatch>();
        foreach (var (match, _) in candidates.OrderBy(c => c.Priority).ThenByDescending(c => c.Match.Length).ThenBy(c => c.Match.Start))
        {
            if (!accepted.Any(other => match.Start < other.Start + other.Length && other.Start < match.Start + match.Length))
            {
                accepted.Add(match);
            }
        }

        return accepted.OrderBy(m => m.Start).ToList();
    }

    /// <summary>
    /// Replaces personal data in every text of the payload. The same value gets the same
    /// placeholder everywhere in the record, so references across fields and turns survive.
    /// </summary>
    public PiiMaskResult Mask(RecordPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var numbering = new Dictionary<(PiiKind, string), string>();
        var counts = new Dictionary<PiiKind, int>();

        string MaskText(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text ?? string.Empty;
            }

            var matches = Find(text);
            if (matches.Count == 0)
            {
                return text;
            }

            var builder = new StringBuilder(text.Length);
            var position = 0;
            foreach (var match in matches)
            {
                builder.Append(text, position, match.Start - position);
                var key = (match.Kind, NormalizeValue(match.Value));
                if (!numbering.TryGetValue(key, out var placeholder))
                {
                    counts[match.Kind] = counts.GetValueOrDefault(match.Kind) + 1;
                    placeholder = string.Create(CultureInfo.InvariantCulture, $"[{PlaceholderName(match.Kind)}_{counts[match.Kind]}]");
                    numbering[key] = placeholder;
                }

                builder.Append(placeholder);
                position = match.Start + match.Length;
            }

            builder.Append(text, position, text.Length - position);
            return builder.ToString();
        }

        RecordPayload masked = payload switch
        {
            InstructionPayload p => new InstructionPayload(MaskText(p.Instruction), MaskText(p.Input), MaskText(p.Output)),
            ChatPayload p => new ChatPayload(
                p.SystemPrompt is null ? null : MaskText(p.SystemPrompt),
                p.Messages.Select(m => new ChatMessage(m.Role, MaskText(m.Content))).ToList()),
            PreferencePayload p => new PreferencePayload(MaskText(p.Prompt), MaskText(p.Chosen), MaskText(p.Rejected)),
            _ => payload,
        };

        return new PiiMaskResult(counts.Count == 0 ? payload : masked, counts);
    }

    /// <summary>
    /// Kinds found anywhere in the payload.
    /// </summary>
    public IReadOnlySet<PiiKind> FindKinds(RecordPayload payload) =>
        payload.GetSearchableTexts().SelectMany(Find).Select(m => m.Kind).ToHashSet();

    // "0532 123 45 67" and "05321234567" are the same number.
    private static string NormalizeValue(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value.Where(c => !char.IsWhiteSpace(c) && c is not '-' and not '.' and not '(' and not ')'))
        {
            builder.Append(char.ToUpperInvariant(c));
        }

        return builder.ToString();
    }

    /// <summary>
    /// T.C. kimlik numarası: 11 digits, no leading zero, two check digits.
    /// </summary>
    public static bool IsValidNationalId(string value)
    {
        if (value.Length != 11 || value[0] == '0' || !value.All(char.IsAsciiDigit))
        {
            return false;
        }

        var d = value.Select(c => c - '0').ToArray();
        var odd = d[0] + d[2] + d[4] + d[6] + d[8];
        var even = d[1] + d[3] + d[5] + d[7];
        var tenth = ((odd * 7) - even) % 10;
        if (tenth < 0)
        {
            tenth += 10;
        }

        return tenth == d[9] && d.Take(10).Sum() % 10 == d[10];
    }

    /// <summary>
    /// ISO 13616 IBAN with its mod-97 check (spaces allowed).
    /// </summary>
    public static bool IsValidIban(string value)
    {
        var iban = new string(value.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
        if (iban.Length is < 15 or > 34 || (iban.StartsWith("TR", StringComparison.Ordinal) && iban.Length != 26))
        {
            return false;
        }

        var remainder = 0;
        foreach (var c in iban[4..] + iban[..4])
        {
            var digits = char.IsAsciiDigit(c) ? (c - '0').ToString(CultureInfo.InvariantCulture)
                : char.IsAsciiLetterUpper(c) ? (c - 'A' + 10).ToString(CultureInfo.InvariantCulture)
                : null;
            if (digits is null)
            {
                return false;
            }

            foreach (var digit in digits)
            {
                remainder = ((remainder * 10) + (digit - '0')) % 97;
            }
        }

        return remainder == 1;
    }

    /// <summary>
    /// 13–19 digit card number of a known network with a valid Luhn check digit.
    /// </summary>
    public static bool IsValidCard(string value)
    {
        var digits = value.Where(char.IsAsciiDigit).Select(c => c - '0').ToArray();
        if (digits.Length is < 13 or > 19 || digits[0] is < 2 or > 6)
        {
            return false;
        }

        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var digit = digits[digits.Length - 1 - i];
            if (i % 2 == 1)
            {
                digit *= 2;
                if (digit > 9)
                {
                    digit -= 9;
                }
            }

            sum += digit;
        }

        return sum % 10 == 0;
    }

    private static readonly (Regex Regex, PiiKind Kind)[] SecretPatterns =
    [
        (PrivateKeyRegex(), PiiKind.Secret),
        (new Regex(@"\bsk-ant-[A-Za-z0-9_-]{20,}", RegexOptions.CultureInvariant), PiiKind.ApiKey),
        (new Regex(@"\bsk-(?:proj-)?[A-Za-z0-9_-]{20,}", RegexOptions.CultureInvariant), PiiKind.ApiKey),
        (new Regex(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b", RegexOptions.CultureInvariant), PiiKind.ApiKey),
        (new Regex(@"\bgh[pousr]_[A-Za-z0-9]{36,}\b", RegexOptions.CultureInvariant), PiiKind.ApiKey),
        (new Regex(@"\bgithub_pat_[A-Za-z0-9_]{22,}\b", RegexOptions.CultureInvariant), PiiKind.ApiKey),
        (new Regex(@"\bxox[abposr]-[A-Za-z0-9-]{10,}", RegexOptions.CultureInvariant), PiiKind.ApiKey),
        (new Regex(@"\bAIza[0-9A-Za-z_-]{35}\b", RegexOptions.CultureInvariant), PiiKind.ApiKey),
        (new Regex(@"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}", RegexOptions.CultureInvariant), PiiKind.Secret),
        (new Regex(@"\b[Bb]earer\s+(?<value>[A-Za-z0-9._~+/-]{16,}=*)", RegexOptions.CultureInvariant), PiiKind.Secret),
        // The value must look like a password (a digit or symbol), so "Şifre: Güçlü bir şifre seçin" stays.
        (new Regex(@"(?i)\b(?:password|pwd|passwd|parola|şifre)\s*[=:]\s*(?<value>(?=[^\s;'""]*[\d!#$%&*+?@^_~])[^\s;'""]{4,})", RegexOptions.CultureInvariant), PiiKind.Secret),
        (new Regex(@"\b[a-z][a-z0-9+.-]*://[^\s:/@]+:(?<value>[^\s@/]+)@", RegexOptions.CultureInvariant), PiiKind.Secret),
    ];

    [GeneratedRegex(@"-----BEGIN (?:[A-Z]+ )*PRIVATE KEY-----[\s\S]+?-----END (?:[A-Z]+ )*PRIVATE KEY-----", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyRegex();

    [GeneratedRegex(@"(?<![\w.+%-])[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,63}(?![\w-])", RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"\b[A-Z]{2}\d{2}(?: ?[A-Z0-9]{4}){2,7}(?: ?[A-Z0-9]{1,4})?\b", RegexOptions.CultureInvariant)]
    private static partial Regex IbanRegex();

    [GeneratedRegex(@"(?<![\d-])(?:\d[ -]?){12,18}\d(?![\d-])", RegexOptions.CultureInvariant)]
    private static partial Regex CardRegex();

    [GeneratedRegex(@"(?<!\d)[1-9]\d{10}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex NationalIdRegex();

    [GeneratedRegex(@"(?<![\d.])(?:10\.\d{1,3}\.\d{1,3}\.\d{1,3}|192\.168\.\d{1,3}\.\d{1,3}|172\.(?:1[6-9]|2\d|3[01])\.\d{1,3}\.\d{1,3})(?![\d.]*\d)", RegexOptions.CultureInvariant)]
    private static partial Regex InternalIpRegex();

    [GeneratedRegex(@"(?i)\b(?:https?://)?(?:[a-z0-9-]+\.)+(?:local|internal|corp|lan|intranet|localdomain)\b(?:[/:?#][^\s""'<>)]*)?", RegexOptions.CultureInvariant)]
    private static partial Regex InternalUrlRegex();

    // International (+90 532 123 45 67) or Turkish (0532 123 45 67, (0212) 123-4567, 05321234567).
    [GeneratedRegex(@"(?<![\w+])(?:\+\d{1,3}[ .-]?(?:\(\d{1,4}\)|\d{1,4})(?:[ .-]?\d{2,4}){2,4}|\(?0?[2-5]\d{2}\)?[ .-]?\d{3}[ .-]?\d{2}[ .-]?\d{2})(?![\w])", RegexOptions.CultureInvariant)]
    private static partial Regex PhoneRegex();
}
