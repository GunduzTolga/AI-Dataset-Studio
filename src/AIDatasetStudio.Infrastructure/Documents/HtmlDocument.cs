using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AIDatasetStudio.Application.Documents;
using AIDatasetStudio.Infrastructure.Import;

namespace AIDatasetStudio.Infrastructure.Documents;

/// <summary>
/// Reads an HTML page as blocks (SDD §12.3, M17): headings, paragraphs, list items with their
/// depth, tables and preformatted text, in reading order. A small tolerant tokenizer is enough
/// for text: unclosed paragraphs and list items, stray end tags and entities are fine. Scripts,
/// styles, navigation, forms and the chrome of Confluence and Notion exports (breadcrumbs, page
/// metadata, attachments, properties) are left out.
/// </summary>
internal static partial class HtmlDocument
{
    // Their content is text up to the matching end tag, not markup.
    private static readonly HashSet<string> RawText = new(StringComparer.Ordinal) { "script", "style", "textarea", "title", "xmp", "noscript", "template" };

    private static readonly HashSet<string> Void = new(StringComparer.Ordinal)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr",
    };

    // Elements whose content is not part of the page's text.
    private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal)
    {
        "head", "script", "style", "noscript", "template", "svg", "math", "nav", "aside", "form", "button", "select",
        "iframe", "object", "canvas", "audio", "video", "map", "textarea",
    };

    // Confluence: breadcrumbs, the footer and the comment, label and like sections.
    private static readonly HashSet<string> SkippedIds = new(StringComparer.Ordinal)
    {
        "breadcrumb-section", "breadcrumbs", "footer", "navigation", "comments-section", "likes-and-labels-container", "labels-section",
    };

    // Confluence: "Created by …, last modified on …" and the attachments; Notion: a database page's properties.
    private static readonly HashSet<string> SkippedClasses = new(StringComparer.Ordinal)
    {
        "page-metadata", "pageSection", "breadcrumbs", "properties", "sr-only", "visually-hidden",
    };

    // What may stand in the head; anything else starts the body, whose start tags may be left out.
    private static readonly HashSet<string> HeadContent = new(StringComparer.Ordinal)
    {
        "base", "link", "meta", "noscript", "script", "style", "template", "title",
    };

    private static readonly HashSet<string> Blocks = new(StringComparer.Ordinal)
    {
        "address", "article", "blockquote", "body", "caption", "center", "dd", "details", "dialog", "div", "dl", "dt",
        "fieldset", "figcaption", "figure", "footer", "header", "hgroup", "hr", "html", "li", "main", "menu", "ol", "p",
        "pre", "section", "summary", "table", "tbody", "td", "tfoot", "th", "thead", "tr", "ul",
        "h1", "h2", "h3", "h4", "h5", "h6",
    };

    /// <summary>
    /// The page's text decoded from its bytes: byte order mark, then the charset its markup
    /// declares, then UTF-8 or windows-1254 as for text files.
    /// </summary>
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        var head = Encoding.Latin1.GetString(bytes[..Math.Min(bytes.Length, 2048)]);
        Encoding encoding;
        if (TextEncodings.Utf8BomLength(bytes) > 0 || bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) || bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            encoding = TextEncodings.Get(TextEncodings.Detect(bytes[..Math.Min(bytes.Length, 4)], isWholeFile: false));
        }
        else if (CharsetRegex().Match(head) is { Success: true } match && TryEncoding(match.Groups[1].Value) is { } declared)
        {
            encoding = declared;
        }
        else
        {
            encoding = TextEncodings.Get(TextEncodings.Detect(bytes, isWholeFile: true));
        }

        var text = encoding.GetString(bytes);
        return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;

        static Encoding? TryEncoding(string name)
        {
            try
            {
                // Pages that say ISO-8859-1 are written in windows-1252, as browsers assume.
                var encoding = TextEncodings.Get(name.Equals("iso-8859-1", StringComparison.OrdinalIgnoreCase) ? "windows-1252" : name);

                // The markup that named it was read as ASCII: a charset that reads ASCII otherwise
                // (UTF-16 without a byte order mark, EBCDIC) is not the page's, as browsers decide.
                return encoding.GetString("<meta charset>"u8) == "<meta charset>" ? encoding : null;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// The blocks of a page and its title (the &lt;title&gt; element, if any). Positions are the
    /// lines of the page source where the blocks start.
    /// </summary>
    public static (IReadOnlyList<DocumentBlock> Blocks, string? Title) Read(string html, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(html);
        var reader = new Reader(html, cancellationToken);
        reader.Run();
        return (reader.Result, reader.Title);
    }

    [GeneratedRegex(@"charset\s*=\s*[""']?\s*([A-Za-z0-9_\-:.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CharsetRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    private sealed class Reader(string html, CancellationToken cancellationToken)
    {
        private readonly List<int> _newlines = [];
        private const int MaxDepth = 512;

        private readonly List<(string Name, bool Skip)> _open = [];
        private bool _inHead;
        private readonly StringBuilder _text = new();
        private readonly Stack<Table> _tables = new();

        // The open lists: numbered ones count their items.
        private readonly Stack<(bool Numbered, int Next)> _lists = new();
        private string _marker = string.Empty;
        private int _skipping;
        private int _pre;
        private int _articles;
        private DocumentBlockKind _kind = DocumentBlockKind.Paragraph;
        private int _level;
        private int _start;
        private bool _confluenceTitle;

        public List<DocumentBlock> Result { get; } = [];

        public string? Title { get; private set; }

        public void Run()
        {
            for (var i = 0; i < html.Length; i++)
            {
                if (html[i] == '\n')
                {
                    _newlines.Add(i);
                }
            }

            var position = 0;
            var steps = 0;
            while (position < html.Length)
            {
                if ((++steps & 0xFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var lt = html.IndexOf('<', position);
                if (lt < 0)
                {
                    Text(position, html.Length);
                    break;
                }

                Text(position, lt);
                position = Markup(lt);
            }

            Flush();
            while (_tables.Count > 0)
            {
                EndTable();
            }
        }

        // One piece of markup at '<'; returns where reading goes on.
        private int Markup(int lt)
        {
            if (string.CompareOrdinal(html, lt, "<!--", 0, 4) == 0)
            {
                var end = html.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                return end < 0 ? html.Length : end + 3;
            }

            if (lt + 1 < html.Length && html[lt + 1] is '!' or '?')
            {
                var end = html.IndexOf('>', lt + 2);
                return end < 0 ? html.Length : end + 1;
            }

            var closing = lt + 1 < html.Length && html[lt + 1] == '/';
            var nameStart = lt + (closing ? 2 : 1);
            var nameEnd = nameStart;
            while (nameEnd < html.Length && (char.IsAsciiLetterOrDigit(html[nameEnd]) || html[nameEnd] is '-' or ':' or '_'))
            {
                nameEnd++;
            }

            // "a < b" is text.
            if (nameEnd == nameStart || !char.IsAsciiLetter(html[nameStart]))
            {
                Text(lt, lt + 1);
                return lt + 1;
            }

            var name = html[nameStart..nameEnd].ToLowerInvariant();
            var (attributes, selfClosing, after) = Attributes(nameEnd);
            if (closing)
            {
                End(name);
                return after;
            }

            if (RawText.Contains(name) && !selfClosing)
            {
                var close = html.IndexOf("</" + name, after, StringComparison.OrdinalIgnoreCase);
                var contentEnd = close < 0 ? html.Length : close;
                if (name == "title" && Title is null)
                {
                    Title = Clean(WebUtility.HtmlDecode(html[after..contentEnd]));
                }

                if (close < 0)
                {
                    return html.Length;
                }

                var end = html.IndexOf('>', close);
                return end < 0 ? html.Length : end + 1;
            }

            Start(name, attributes, lt);
            if (selfClosing && !Void.Contains(name))
            {
                End(name);
            }

            return after;
        }

        // The attributes of a tag up to its '>'.
        private (Dictionary<string, string> Attributes, bool SelfClosing, int After) Attributes(int position)
        {
            var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
            var selfClosing = false;
            while (position < html.Length)
            {
                var c = html[position];
                if (c == '>')
                {
                    return (attributes, selfClosing, position + 1);
                }

                if (char.IsWhiteSpace(c) || c == '/')
                {
                    selfClosing = c == '/';
                    position++;
                    continue;
                }

                selfClosing = false;
                var nameStart = position;
                while (position < html.Length && !char.IsWhiteSpace(html[position]) && html[position] is not ('=' or '>' or '/'))
                {
                    position++;
                }

                var name = html[nameStart..position].ToLowerInvariant();
                while (position < html.Length && char.IsWhiteSpace(html[position]))
                {
                    position++;
                }

                var value = string.Empty;
                if (position < html.Length && html[position] == '=')
                {
                    position++;
                    while (position < html.Length && char.IsWhiteSpace(html[position]))
                    {
                        position++;
                    }

                    if (position < html.Length && html[position] is '"' or '\'')
                    {
                        var quote = html[position];
                        var end = html.IndexOf(quote, position + 1);
                        end = end < 0 ? html.Length : end;
                        value = html[(position + 1)..end];
                        position = Math.Min(html.Length, end + 1);
                    }
                    else
                    {
                        var valueStart = position;
                        while (position < html.Length && !char.IsWhiteSpace(html[position]) && html[position] != '>')
                        {
                            position++;
                        }

                        value = html[valueStart..position];
                    }
                }

                attributes.TryAdd(name, WebUtility.HtmlDecode(value));
            }

            return (attributes, selfClosing, html.Length);
        }

        private void Start(string name, Dictionary<string, string> attributes, int position)
        {
            if (!HeadContent.Contains(name) && name != "head")
            {
                LeaveHead();
            }

            if (name == "br")
            {
                if (_skipping == 0)
                {
                    Append(_pre > 0 ? "\n" : " ");
                }

                return;
            }

            if (Void.Contains(name))
            {
                if (name == "hr" && _skipping == 0 && _tables.Count == 0)
                {
                    Flush();
                }

                return;
            }

            // Pages that never close their tags would nest without end; past a depth no page
            // needs, elements are taken as if they had no content of their own.
            if (_open.Count >= MaxDepth)
            {
                return;
            }

            var skip = IsSkipped(name, attributes);
            _open.Add((name, skip));
            _inHead |= name == "head";
            if (skip)
            {
                _skipping++;
            }

            if (name is "article" or "main")
            {
                _articles++;
            }

            if (_skipping > 0)
            {
                return;
            }

            // Inside a table everything is the cell's text.
            if (_tables.Count > 0 && name is not ("table" or "tr" or "td" or "th"))
            {
                if (Blocks.Contains(name))
                {
                    _tables.Peek().Append(" ");
                }

                return;
            }

            switch (name)
            {
                case "table":
                    if (_tables.Count == 0)
                    {
                        Flush();
                    }

                    _tables.Push(new Table(Line(position)));
                    return;
                case "tr":
                    // A row or cell outside any table is just its text.
                    if (_tables.Count > 0)
                    {
                        _tables.Peek().Row();
                    }

                    return;
                case "td" or "th":
                    if (_tables.Count > 0)
                    {
                        _tables.Peek().Cell();
                    }

                    return;
            }

            if (!Blocks.Contains(name))
            {
                return;
            }

            Flush();
            if (name.Length == 2 && name[0] == 'h' && name[1] is >= '1' and <= '6')
            {
                (_kind, _level) = (DocumentBlockKind.Heading, name[1] - '0');
                _confluenceTitle = attributes.TryGetValue("id", out var id) && id == "title-heading";
            }
            else if (name is "ul" or "ol" or "menu")
            {
                var start = name == "ol" && attributes.TryGetValue("start", out var value) && int.TryParse(value, out var number) ? number : 1;
                _lists.Push((name == "ol", start));
            }
            else if (name == "li")
            {
                (_kind, _level) = (DocumentBlockKind.ListItem, Math.Max(0, _lists.Count - 1));

                // Items are marked as in PDF and Word documents: "- " or "1. ".
                _marker = "- ";
                if (_lists.TryPop(out var list))
                {
                    _marker = list.Numbered ? string.Create(CultureInfo.InvariantCulture, $"{list.Next}. ") : "- ";
                    _lists.Push(list with { Next = list.Next + 1 });
                }
            }
            else if (name == "pre")
            {
                _pre++;
            }
        }

        private void End(string name)
        {
            var index = _open.FindLastIndex(e => e.Name == name);
            if (index < 0)
            {
                return;
            }

            // Elements left open inside it end with it.
            for (var i = _open.Count - 1; i >= index; i--)
            {
                var (open, skip) = _open[i];
                _open.RemoveAt(i);
                Close(open, skip);
            }
        }

        private void Close(string name, bool skip)
        {
            var skipping = _skipping > 0;
            if (skip)
            {
                _skipping--;
            }

            if (name is "article" or "main")
            {
                _articles--;
            }

            if (skipping)
            {
                return;
            }

            if (_tables.Count > 0)
            {
                if (name == "table")
                {
                    EndTable();
                }
                else if (Blocks.Contains(name))
                {
                    _tables.Peek().Append(" ");
                }

                return;
            }

            if (!Blocks.Contains(name))
            {
                return;
            }

            Flush();
            if (name == "pre")
            {
                _pre = Math.Max(0, _pre - 1);
            }
            else if (name is "ul" or "ol" or "menu")
            {
                _lists.TryPop(out _);
            }
        }

        private void EndTable()
        {
            var table = _tables.Pop();
            var text = table.Text();
            if (_tables.Count > 0)
            {
                // A table in a cell is part of the cell's text.
                _tables.Peek().Append(" " + text.Replace('\n', ' ') + " ");
            }
            else if (text.Length > 0)
            {
                // A table of one cell only lays out its text.
                Result.Add(new DocumentBlock(table.IsOneCell ? DocumentBlockKind.Paragraph : DocumentBlockKind.Table, text, table.Line));
            }
        }

        // An open head ends where the body's content begins (minified pages leave out </head> and <body>).
        private void LeaveHead()
        {
            // Looked for once: every tag and every piece of text asks.
            if (!_inHead)
            {
                return;
            }

            _inHead = false;
            if (_open.FindLastIndex(e => e.Name == "head") is var head and >= 0)
            {
                End(_open[head].Name);
            }
        }

        private bool IsSkipped(string name, Dictionary<string, string> attributes)
        {
            if (Skipped.Contains(name)
                || (name is "header" or "footer" && _articles == 0)
                || attributes.ContainsKey("hidden")
                || (attributes.TryGetValue("aria-hidden", out var ariaHidden) && ariaHidden == "true")
                || (attributes.TryGetValue("id", out var id) && SkippedIds.Contains(id)))
            {
                return true;
            }

            if (attributes.TryGetValue("style", out var style)
                && WhitespaceRegex().Replace(style, string.Empty).Contains("display:none", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return attributes.TryGetValue("class", out var classes)
                && classes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(SkippedClasses.Contains);
        }

        private void Text(int start, int end)
        {
            if (end > start && !string.IsNullOrWhiteSpace(html.AsSpan(start, end - start).ToString()))
            {
                LeaveHead();
            }

            if (end <= start || _skipping > 0)
            {
                return;
            }

            var text = WebUtility.HtmlDecode(html[start..end]);
            if (_tables.Count > 0)
            {
                _tables.Peek().Append(text);
                return;
            }

            if (_text.Length == 0 && string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            if (_text.Length == 0)
            {
                var first = start;
                while (first < end && char.IsWhiteSpace(html[first]))
                {
                    first++;
                }

                _start = Line(first);
            }

            _text.Append(text);
        }

        private void Append(string text)
        {
            if (_tables.Count > 0)
            {
                _tables.Peek().Append(text);
            }
            else if (_text.Length > 0)
            {
                _text.Append(text);
            }
        }

        // The text gathered so far becomes a block.
        private void Flush()
        {
            var raw = _text.ToString();
            _text.Clear();
            var text = _pre > 0 ? Preformatted(raw) : Clean(raw);
            if (text.Length > 0)
            {
                if (_kind == DocumentBlockKind.Heading && _confluenceTitle && text.IndexOf(" : ", StringComparison.Ordinal) is var colon and > 0)
                {
                    // Confluence titles read "Space : Page".
                    text = text[(colon + 3)..].Trim();
                }

                text = _kind == DocumentBlockKind.ListItem ? _marker + text : text;
                Result.Add(new DocumentBlock(_kind, text, _start, _kind == DocumentBlockKind.Paragraph ? 0 : _level));
            }

            (_kind, _level, _confluenceTitle) = (DocumentBlockKind.Paragraph, 0, false);
        }

        private int Line(int position)
        {
            var index = _newlines.BinarySearch(position);
            return (index < 0 ? ~index : index) + 1;
        }
    }

    // Whitespace (also no-break spaces) collapsed to single spaces.
    private static string Clean(string text) => WhitespaceRegex().Replace(text.Replace(' ', ' '), " ").Trim();

    // Code and other preformatted text keeps its lines, without blank ones around it.
    private static string Preformatted(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(line => line.TrimEnd()).ToList();
        while (lines.Count > 0 && lines[0].Length == 0)
        {
            lines.RemoveAt(0);
        }

        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return string.Join('\n', lines);
    }

    private sealed class Table(int line)
    {
        private readonly List<List<StringBuilder>> _rows = [];

        public int Line { get; } = line;

        public bool IsOneCell => _rows.SelectMany(row => row).Count(cell => Clean(cell.ToString()).Length > 0) == 1;

        public void Row() => _rows.Add([]);

        public void Cell()
        {
            if (_rows.Count == 0)
            {
                Row();
            }

            _rows[^1].Add(new StringBuilder());
        }

        public void Append(string text)
        {
            if (_rows.Count == 0 || _rows[^1].Count == 0)
            {
                // Text outside cells (a caption, stray text) gets a row of its own.
                Cell();
            }

            _rows[^1][^1].Append(text);
        }

        /// <summary>
        /// One line per row with text, cells separated by " | ".
        /// </summary>
        public string Text() => string.Join(
            '\n',
            _rows.Select(row => row.Select(cell => Clean(cell.ToString())).ToList())
                .Where(cells => cells.Any(cell => cell.Length > 0))
                .Select(cells => string.Join(" | ", cells)));
    }
}
