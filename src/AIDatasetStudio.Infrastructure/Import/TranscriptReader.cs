using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AIDatasetStudio.Application.Import;

namespace AIDatasetStudio.Infrastructure.Import;

/// <summary>
/// Reads conversation logs (SDD §12.3, §17.2, M18): WhatsApp chat exports (Android and iOS, in
/// any date format) and transcripts with the speaker before each message ("Müşteri: …",
/// "**Agent:** …"). Lines without a speaker continue the message above them. A WhatsApp chat is
/// split into conversations where it pauses for <see cref="ConversationGap"/>; a transcript at
/// separator lines and headings, or at blank lines when it has neither.
/// </summary>
internal static partial class TranscriptReader
{
    /// <summary>
    /// A pause this long in a WhatsApp chat starts a new conversation.
    /// </summary>
    public static readonly TimeSpan ConversationGap = TimeSpan.FromHours(6);

    // A log names a few people; more distinct "speakers" are labels of some other text.
    private const int MaxSpeakers = 12;

    private const int MaxNameLength = 40;

    // WhatsApp marks direction and placeholders with invisible characters.
    private static readonly char[] Invisible = ['‎', '‏', '‪', '‫', '‬', '‭', '‮', '﻿'];

    private static readonly string[] Deleted =
    [
        "This message was deleted", "You deleted this message", "This message was deleted.", "You deleted this message.",
        "Bu mesaj silindi", "Bu mesajı sildiniz", "Bu mesaj silindi.", "Bu mesajı sildiniz.", "null",
    ];

    private static readonly string[] EditedSuffixes = ["<This message was edited>", "<Bu mesaj düzenlendi>", "<Bu mesaj düzenlenmiştir>"];

    /// <summary>
    /// A conversation and the line its first message is on.
    /// </summary>
    public sealed record Conversation(long Line, IReadOnlyList<TranscriptMessage> Messages);

    /// <summary>
    /// The conversations of a log, its speakers (most messages first) and how many conversations each opens.
    /// </summary>
    public sealed record Log(IReadOnlyList<Conversation> Conversations, IReadOnlyList<string> Speakers, IReadOnlyDictionary<string, int> Openers);

    /// <summary>
    /// Whether text looks like a conversation log: most of its lines start a message (WhatsApp
    /// or "Speaker: text"), by two to <see cref="MaxSpeakers"/> speakers who write at least twice.
    /// </summary>
    public static bool Looks(IReadOnlyList<string> lines)
    {
        var nonEmpty = lines.Select(Clean).Where(l => l.Length > 0).ToList();
        if (nonEmpty.Count < 4)
        {
            return false;
        }

        var whatsApp = nonEmpty.Count(l => WhatsAppLine(l) is not null);
        if (whatsApp * 2 >= nonEmpty.Count && whatsApp >= 3)
        {
            return true;
        }

        var speakers = nonEmpty.Select(SpeakerLine).OfType<(string Name, string Text)>().GroupBy(m => m.Name).ToList();
        var labelled = speakers.Sum(g => g.Count());
        var top = speakers.Select(g => g.Count()).OrderDescending().Take(2).Sum();
        return labelled * 2 >= nonEmpty.Count
            && labelled >= 4
            && speakers.Count is >= 2 and <= MaxSpeakers
            && speakers.Count(g => g.Count() >= 2) >= 2
            && top * 10 >= labelled * 6;
    }

    public static Log Read(IEnumerable<string> lines)
    {
        var all = lines.Select(Clean).ToList();
        var nonEmpty = all.Where(l => l.Length > 0).ToList();
        var isWhatsApp = nonEmpty.Count(l => WhatsAppLine(l) is not null) * 2 >= Math.Max(1, nonEmpty.Count);
        var conversations = isWhatsApp ? WhatsApp(all) : Transcript(all);
        conversations = [.. conversations.Where(c => c.Messages.Count > 0)];

        var counts = conversations.SelectMany(c => c.Messages).GroupBy(m => m.Speaker, StringComparer.Ordinal)
            .Select(g => (Speaker: g.Key, Count: g.Count(), First: g.Min(m => m.Line)))
            .OrderByDescending(s => s.Count).ThenBy(s => s.First)
            .ToList();
        var openers = conversations.GroupBy(c => c.Messages[0].Speaker, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return new Log(conversations, [.. counts.Select(s => s.Speaker)], openers);
    }

    private static List<Conversation> WhatsApp(List<string> lines)
    {
        var entries = new List<(long Line, string Date, string Time, string? Speaker, StringBuilder Text)>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (WhatsAppLine(lines[i]) is { } entry)
            {
                var (speaker, text) = entry.Body.IndexOf(": ", StringComparison.Ordinal) is var colon and > 0 and <= MaxNameLength + 20
                    ? (entry.Body[..colon].Trim(), entry.Body[(colon + 2)..])
                    : ((string?)null, entry.Body);
                entries.Add((i + 1, entry.Date, entry.Time, speaker, new StringBuilder(text)));
            }
            else if (entries.Count > 0)
            {
                entries[^1].Text.Append('\n').Append(lines[i]);
            }
        }

        // Day or month first: a number above 12 tells; dots mean day first, as in Turkish and German.
        var dates = entries.Select(e => e.Date.Split(['.', '/', '-'])).Where(p => p.Length == 3).ToList();
        var yearFirst = dates.Count > 0 && dates.All(p => p[0].Length == 4);
        var dayFirst = dates.Any(p => !yearFirst && int.Parse(p[0], CultureInfo.InvariantCulture) > 12)
            || (!dates.Any(p => !yearFirst && int.Parse(p[1], CultureInfo.InvariantCulture) > 12) && entries.Any(e => e.Date.Contains('.', StringComparison.Ordinal)));

        var conversations = new List<Conversation>();
        var current = new List<TranscriptMessage>();
        DateTime? last = null;
        foreach (var (line, date, time, speaker, text) in entries)
        {
            var at = Timestamp(date, time, dayFirst, yearFirst);
            if (current.Count > 0 && at is { } now && last is { } before && now - before >= ConversationGap)
            {
                conversations.Add(new Conversation(current[0].Line, current));
                current = [];
            }

            last = at ?? last;
            if (speaker is not null && MessageText(text.ToString()) is { } message)
            {
                current.Add(new TranscriptMessage(speaker, message, line));
            }
        }

        if (current.Count > 0)
        {
            conversations.Add(new Conversation(current[0].Line, current));
        }

        return conversations;
    }

    private static List<Conversation> Transcript(List<string> lines)
    {
        var hasSeparators = lines.Any(IsSeparator);
        var conversations = new List<Conversation>();
        var current = new List<(string Speaker, StringBuilder Text, long Line)>();

        void Close()
        {
            var messages = current.Select(m => MessageText(m.Text.ToString()) is { } text ? new TranscriptMessage(m.Speaker, text, m.Line) : null)
                .OfType<TranscriptMessage>()
                .ToList();
            if (messages.Count > 0)
            {
                conversations.Add(new Conversation(messages[0].Line, messages));
            }

            current.Clear();
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (hasSeparators ? IsSeparator(line) : line.Length == 0)
            {
                Close();
                continue;
            }

            if (SpeakerLine(line) is { } message)
            {
                current.Add((message.Name, new StringBuilder(message.Text), i + 1));
            }
            else if (current.Count > 0)
            {
                // Blank lines inside a message are kept when conversations are split at separators.
                current[^1].Text.Append('\n').Append(line);
            }
        }

        Close();
        return conversations;
    }

    /// <summary>
    /// The message as it should be kept: placeholders for media and deleted messages are dropped
    /// and the "edited" note is cut.
    /// </summary>
    private static string? MessageText(string text)
    {
        // iOS marks attachments, deleted messages and its own notices with a left-to-right mark.
        if (text.TrimStart().StartsWith('\u200E'))
        {
            return null;
        }

        // Android names an attached file on a line of its own; a caption may follow it.
        text = string.Join('\n', text.Split('\n').Where(line => !AttachmentRegex().IsMatch(line.Trim(Invisible).Trim())));
        text = text.Trim().Trim(Invisible).Trim();
        foreach (var suffix in EditedSuffixes)
        {
            if (text.EndsWith(suffix, StringComparison.Ordinal))
            {
                text = text[..^suffix.Length].TrimEnd();
            }
        }

        if (text.Length == 0
            || Deleted.Contains(text, StringComparer.Ordinal)
            || PlaceholderRegex().IsMatch(text)
            || (text.Length <= 40 && (text.EndsWith(" omitted", StringComparison.Ordinal) || text.EndsWith(" dahil edilmedi", StringComparison.Ordinal))))
        {
            return null;
        }

        return text;
    }

    private static DateTime? Timestamp(string date, string time, bool dayFirst, bool yearFirst)
    {
        var parts = date.Split(['.', '/', '-']);
        if (parts.Length != 3)
        {
            return null;
        }

        var numbers = parts.Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        var (year, month, day) = yearFirst ? (numbers[0], numbers[1], numbers[2])
            : dayFirst ? (numbers[2], numbers[1], numbers[0])
            : (numbers[2], numbers[0], numbers[1]);
        year = year < 100 ? 2000 + year : year;

        var clock = TimeRegex().Match(time);
        if (!clock.Success)
        {
            return null;
        }

        var hour = int.Parse(clock.Groups[1].Value, CultureInfo.InvariantCulture);
        var minute = int.Parse(clock.Groups[2].Value, CultureInfo.InvariantCulture);
        var meridiem = clock.Groups[3].Value.ToUpperInvariant();
        hour = meridiem.StartsWith('P') && hour < 12 ? hour + 12 : meridiem.StartsWith('A') && hour == 12 ? 0 : hour;
        try
        {
            return new DateTime(year, month, day, hour % 24, minute % 60, 0, DateTimeKind.Unspecified);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static (string Date, string Time, string Body)? WhatsAppLine(string line)
    {
        var match = AndroidRegex().Match(line);
        if (!match.Success)
        {
            match = IosRegex().Match(line);
        }

        return match.Success ? (match.Groups["date"].Value, match.Groups["time"].Value, match.Groups["rest"].Value) : null;
    }

    private static (string Name, string Text)? SpeakerLine(string line)
    {
        var match = SpeakerRegex().Match(line);
        if (!match.Success)
        {
            return null;
        }

        var name = match.Groups["name"].Value.Trim();
        return name.Length is > 0 and <= MaxNameLength && name.Count(c => c == ' ') <= 3 ? (name, match.Groups["text"].Value) : null;
    }

    private static bool IsSeparator(string line) => SeparatorRegex().IsMatch(line);

    private static string Clean(string line) => line.Trim(Invisible).TrimEnd();

    // "08.10.2026 14:32 - Ayşe: …", "10/8/26, 2:32 PM - Ayşe: …"
    [GeneratedRegex(@"^(?<date>\d{1,4}[./-]\d{1,2}[./-]\d{1,4}),?\s+(?<time>\d{1,2}[:.]\d{2}(?:[:.]\d{2})?(?:\s?[AaPp]\.?\s?[Mm]\.?)?)\s+[-–]\s+(?<rest>.*)$")]
    private static partial Regex AndroidRegex();

    // "[08.10.2026 14:32:05] Ayşe: …"
    [GeneratedRegex(@"^\[(?<date>\d{1,4}[./-]\d{1,2}[./-]\d{1,4}),?\s+(?<time>\d{1,2}[:.]\d{2}(?:[:.]\d{2})?(?:\s?[AaPp]\.?\s?[Mm]\.?)?)\]\s+(?<rest>.*)$")]
    private static partial Regex IosRegex();

    [GeneratedRegex(@"^(\d{1,2})[:.](\d{2})(?:[:.]\d{2})?\s?([AaPp])?")]
    private static partial Regex TimeRegex();

    // "Müşteri: …", "**Temsilci:** …", "- Agent: …", "[10:32] Customer: …"
    [GeneratedRegex(@"^\s*(?:\[[^\]]{1,25}\]\s*)?(?:[-*>]\s+)?(?:\*\*|__)?(?<name>\p{L}[\p{L}\p{N} ._'()-]{0,39}?)\s*(?:\*\*|__)?:(?:\*\*|__)?\s+(?<text>\S.*)$")]
    private static partial Regex SpeakerRegex();

    // "---", "***", "===", "#####", or a Markdown heading.
    [GeneratedRegex(@"^\s*(?:([-=*_#~])\1{2,}\s*|#{1,6}\s+\S.*)$")]
    private static partial Regex SeparatorRegex();

    // "<Media omitted>", "<Medya dahil edilmedi>", "<attached: 00000012-PHOTO-….jpg>".
    [GeneratedRegex(@"^<[^<>]{3,150}>$")]
    private static partial Regex PlaceholderRegex();

    // "IMG-20261008-WA0001.jpg (file attached)", "Fatura.pdf (dosya ekli)".
    [GeneratedRegex(@"^.{1,150}\.\w{2,5} \((?:file attached|dosya ekli)\)$")]
    private static partial Regex AttachmentRegex();
}
