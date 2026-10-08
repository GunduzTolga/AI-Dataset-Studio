using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using AIDatasetStudio.Application.Documents;
using AIDatasetStudio.Application.Import;
using AIDatasetStudio.Infrastructure.Import;

namespace AIDatasetStudio.Infrastructure.Documents;

/// <summary>
/// Reads the text of Word documents (.docx, .docm) as blocks: headings by their style or outline
/// level, numbered and bulleted list items with their labels, paragraphs and tables (SDD §12.3).
/// The body is streamed. Headers, footers, footnotes, comments, text boxes, deleted (tracked)
/// text and tables of contents are left out. Elements are matched by local name, so
/// transitional and strict documents both read.
/// </summary>
internal static class DocxDocument
{
    private const string MarkupCompatibility = "http://schemas.openxmlformats.org/markup-compatibility/2006";

    private static readonly XmlReaderSettings XmlSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        CloseInput = true,
    };

    /// <exception cref="ImportFileException">The file is a Word 97–2003 or protected document, or not a Word document.</exception>
    public static IReadOnlyList<DocumentBlock> Read(string path, CancellationToken cancellationToken)
    {
        using var stream = SourceFile.OpenRead(path);
        Span<byte> start = stackalloc byte[8];
        var length = stream.ReadAtLeast(start, start.Length, throwOnEndOfStream: false);
        if (XlsxWorkbook.IsCompoundFile(start[..length]))
        {
            throw Legacy(path);
        }

        stream.Position = 0;
        try
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var package = new Package(zip);
            var main = package.Relationships(string.Empty, "_rels/.rels")
                .FirstOrDefault(r => r.Type.EndsWith("/officeDocument", StringComparison.Ordinal)).Target ?? "word/document.xml";
            var folder = main[..(main.LastIndexOf('/') + 1)];
            var parts = package.Relationships(folder, $"{folder}_rels/{main[folder.Length..]}.rels");
            string Part(string type, string fallback) =>
                parts.FirstOrDefault(r => r.Type.EndsWith(type, StringComparison.Ordinal)).Target ?? folder + fallback;

            var styles = StyleSheet.Read(package.Xml(Part("/styles", "styles.xml")));
            var numbering = NumberingDefinitions.Read(package.Xml(Part("/numbering", "numbering.xml")));
            using var body = package.Xml(main) ?? throw Invalid(path);
            return new BodyReader(styles, numbering).Read(body, cancellationToken) ?? throw Invalid(path);
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException)
        {
            throw Invalid(path, ex);
        }
    }

    public static ImportFileException Legacy(string path) =>
        new(ImportFileError.LegacyDocument, path, $"'{path}' is a Word 97-2003 or password-protected document.");

    public static ImportFileException Invalid(string path, Exception? innerException = null) =>
        new(ImportFileError.InvalidDocument, path, $"'{path}' is not a valid Word document.", innerException: innerException);

    /// <summary>
    /// The "val" attribute of a WordprocessingML element, in whichever namespace it is.
    /// </summary>
    private static string? Val(XmlReader reader)
    {
        for (var i = 0; i < reader.AttributeCount; i++)
        {
            reader.MoveToAttribute(i);
            if (reader.LocalName == "val")
            {
                var value = reader.Value;
                reader.MoveToElement();
                return value;
            }
        }

        reader.MoveToElement();
        return null;
    }

    private static int? IntVal(XmlReader reader) =>
        int.TryParse(Val(reader), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>
    /// The parts of the package by path, with their relationships.
    /// </summary>
    private sealed class Package(ZipArchive zip)
    {
        private readonly Dictionary<string, ZipArchiveEntry> _entries = zip.Entries
            .GroupBy(e => e.FullName.Replace('\\', '/').TrimStart('/'), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        public XmlReader? Xml(string part) =>
            _entries.TryGetValue(part, out var entry) ? XmlReader.Create(entry.Open(), XmlSettings) : null;

        public List<(string Type, string? Target)> Relationships(string folder, string part)
        {
            var relationships = new List<(string Type, string? Target)>();
            using var reader = Xml(part);
            while (reader?.Read() == true)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Relationship" && reader.GetAttribute("TargetMode") != "External"
                    && reader.GetAttribute("Type") is { } type && reader.GetAttribute("Target") is { } target)
                {
                    relationships.Add((type, XlsxWorkbook.Resolve(folder, target)));
                }
            }

            return relationships;
        }
    }

    /// <param name="Name">The style's name, e.g. "heading 1" (built-in names are English in every language of Word).</param>
    /// <param name="OutlineLevel">0–8 for headings, 9 for body text; <c>null</c> when the style does not set it.</param>
    private sealed record Style(string? Name, string? BasedOn, int? OutlineLevel, string? NumberingId, int? ListLevel);

    /// <summary>
    /// What a paragraph style makes of its paragraphs, following the styles it is based on.
    /// </summary>
    private sealed class StyleSheet
    {
        private const int MaxDepth = 16;

        private readonly Dictionary<string, Style> _styles = new(StringComparer.Ordinal);

        public string? DefaultStyle { get; private set; }

        public static StyleSheet Read(XmlReader? reader)
        {
            var sheet = new StyleSheet();
            using (reader)
            {
                while (reader?.Read() == true)
                {
                    if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "style"
                        && reader.GetAttribute("type", reader.NamespaceURI) is null or "paragraph"
                        && Attribute(reader, "styleId") is { } id)
                    {
                        if (Attribute(reader, "default") is "1" or "true")
                        {
                            sheet.DefaultStyle = id;
                        }

                        sheet._styles[id] = ReadStyle(reader.ReadSubtree());
                    }
                }
            }

            return sheet;
        }

        /// <summary>
        /// The heading level of the style (0 for the document title), or <c>null</c> for body text.
        /// </summary>
        public int? HeadingLevel(string? id)
        {
            foreach (var style in Chain(id))
            {
                var name = style.Name?.Trim().ToLowerInvariant();
                if (name == "title")
                {
                    return 0;
                }

                if (name is not null && name.StartsWith("heading ", StringComparison.Ordinal)
                    && int.TryParse(name.AsSpan("heading ".Length), NumberStyles.None, CultureInfo.InvariantCulture, out var level) && level is >= 1 and <= 9)
                {
                    return level;
                }

                if (style.OutlineLevel is { } outline)
                {
                    return outline is >= 0 and <= 8 ? outline + 1 : null;
                }
            }

            return null;
        }

        /// <summary>
        /// Whether the style is one of a table of contents ("TOC 1", "TOC Heading"; "Contents 1" in LibreOffice).
        /// </summary>
        public bool IsTableOfContents(string? id) => Chain(id).Take(1).Any(style =>
            style.Name?.Trim() is { } name
            && (name.StartsWith("toc", StringComparison.OrdinalIgnoreCase) || name.StartsWith("contents", StringComparison.OrdinalIgnoreCase)));

        /// <summary>
        /// The list the style puts its paragraphs in, if any.
        /// </summary>
        public (string NumberingId, int Level)? Numbering(string? id)
        {
            foreach (var style in Chain(id))
            {
                if (style.NumberingId is { } numbering)
                {
                    return (numbering, style.ListLevel ?? 0);
                }
            }

            return null;
        }

        private IEnumerable<Style> Chain(string? id)
        {
            for (var depth = 0; id is not null && depth < MaxDepth && _styles.TryGetValue(id, out var style); depth++)
            {
                yield return style;
                id = style.BasedOn;
            }
        }

        private static Style ReadStyle(XmlReader reader)
        {
            using (reader)
            {
                string? name = null, basedOn = null, numbering = null;
                int? outline = null, level = null;
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element)
                    {
                        continue;
                    }

                    switch (reader.LocalName)
                    {
                        case "name":
                            name = Val(reader);
                            break;
                        case "basedOn":
                            basedOn = Val(reader);
                            break;
                        case "outlineLvl":
                            outline = IntVal(reader);
                            break;
                        case "numId":
                            numbering = Val(reader);
                            break;
                        case "ilvl":
                            level = IntVal(reader);
                            break;
                        case "rPr" or "tblPr" or "tcPr" or "trPr" or "tblStylePr":
                            reader.Skip();
                            break;
                    }
                }

                return new Style(name, basedOn, outline, numbering, level);
            }
        }

        private static string? Attribute(XmlReader reader, string localName)
        {
            for (var i = 0; i < reader.AttributeCount; i++)
            {
                reader.MoveToAttribute(i);
                if (reader.LocalName == localName)
                {
                    var value = reader.Value;
                    reader.MoveToElement();
                    return value;
                }
            }

            reader.MoveToElement();
            return null;
        }
    }

    /// <param name="Format">The number format, e.g. "decimal", "lowerLetter" or "bullet".</param>
    /// <param name="Text">The label pattern, e.g. "%1." or "%1.%2".</param>
    private sealed record ListLevel(string Format, string Text, int Start);

    /// <summary>
    /// The lists of the document and the labels of their items ("1.", "a)", "2.1", bullets as "-").
    /// Counters run per list and restart deeper levels when a level moves on.
    /// </summary>
    private sealed class NumberingDefinitions
    {
        private const int Levels = 9;

        private readonly Dictionary<string, ListLevel?[]> _abstract = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (string AbstractId, Dictionary<int, int> StartOverrides)> _lists = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int[]> _counters = new(StringComparer.Ordinal);

        public static NumberingDefinitions Read(XmlReader? reader)
        {
            var numbering = new NumberingDefinitions();
            using (reader)
            {
                while (reader?.Read() == true)
                {
                    if (reader.NodeType != XmlNodeType.Element)
                    {
                        continue;
                    }

                    if (reader.LocalName == "abstractNum" && Attribute(reader, "abstractNumId") is { } abstractId)
                    {
                        numbering._abstract[abstractId] = ReadLevels(reader.ReadSubtree());
                    }
                    else if (reader.LocalName == "num" && Attribute(reader, "numId") is { } id)
                    {
                        numbering._lists[id] = ReadList(reader.ReadSubtree());
                    }
                }
            }

            return numbering;
        }

        /// <summary>
        /// The label of the next item at <paramref name="level"/> of a list: "1.", "b)", "-" for a
        /// bullet, or <c>null</c> when the list is unknown or has no label.
        /// </summary>
        public string? Next(string id, int level)
        {
            if (!_lists.TryGetValue(id, out var list) || !_abstract.TryGetValue(list.AbstractId, out var levels))
            {
                return null;
            }

            level = Math.Clamp(level, 0, Levels - 1);
            if (levels[level] is not { } definition)
            {
                return null;
            }

            if (definition.Format == "bullet")
            {
                return "-";
            }

            if (!_counters.TryGetValue(id, out var counters))
            {
                _counters[id] = counters = new int[Levels];
            }

            counters[level] = counters[level] == 0 ? StartOf(list, levels, level) : counters[level] + 1;
            Array.Clear(counters, level + 1, Levels - level - 1);
            if (definition.Format == "none")
            {
                return null;
            }

            var label = new StringBuilder();
            var text = definition.Text;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '%' && i + 1 < text.Length && text[i + 1] is >= '1' and <= '9')
                {
                    var referenced = text[++i] - '1';
                    var value = counters[referenced] == 0 ? StartOf(list, levels, referenced) : counters[referenced];
                    label.Append(Number(value, levels[referenced]?.Format ?? "decimal"));
                }
                else
                {
                    label.Append(text[i]);
                }
            }

            return label.ToString().Trim() is { Length: > 0 } result ? result : null;
        }

        private static int StartOf((string AbstractId, Dictionary<int, int> StartOverrides) list, ListLevel?[] levels, int level) =>
            list.StartOverrides.TryGetValue(level, out var start) ? start : levels[level]?.Start ?? 1;

        private static string Number(int value, string format) => format switch
        {
            "lowerLetter" => Letters(value).ToLowerInvariant(),
            "upperLetter" => Letters(value),
            "lowerRoman" => Roman(value).ToLowerInvariant(),
            "upperRoman" => Roman(value),
            "decimalZero" => value.ToString("00", CultureInfo.InvariantCulture),
            _ => value.ToString(CultureInfo.InvariantCulture),
        };

        // Word repeats the letter after z: a … z, aa … zz.
        private static string Letters(int value) =>
            value <= 0 ? string.Empty : new string((char)('A' + ((value - 1) % 26)), ((value - 1) / 26) + 1);

        private static string Roman(int value)
        {
            if (value is <= 0 or >= 4000)
            {
                return value.ToString(CultureInfo.InvariantCulture);
            }

            ReadOnlySpan<int> values = [1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1];
            string[] symbols = ["M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I"];
            var roman = new StringBuilder();
            for (var i = 0; i < values.Length; i++)
            {
                for (; value >= values[i]; value -= values[i])
                {
                    roman.Append(symbols[i]);
                }
            }

            return roman.ToString();
        }

        private static ListLevel?[] ReadLevels(XmlReader reader)
        {
            var levels = new ListLevel?[Levels];
            using (reader)
            {
                reader.Read();
                while (reader.Read())
                {
                    if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "lvl"
                        && int.TryParse(Attribute(reader, "ilvl"), NumberStyles.None, CultureInfo.InvariantCulture, out var level) && level < Levels)
                    {
                        levels[level] = ReadLevel(reader.ReadSubtree());
                    }
                }
            }

            return levels;
        }

        private static ListLevel ReadLevel(XmlReader reader)
        {
            using (reader)
            {
                string format = "decimal", text = string.Empty;
                var start = 1;
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element)
                    {
                        continue;
                    }

                    switch (reader.LocalName)
                    {
                        case "start":
                            start = IntVal(reader) ?? 1;
                            break;
                        case "numFmt":
                            format = Val(reader) ?? format;
                            break;
                        case "lvlText":
                            text = Val(reader) ?? text;
                            break;
                        case "pPr" or "rPr" or "legacy":
                            reader.Skip();
                            break;
                    }
                }

                return new ListLevel(format, text, start);
            }
        }

        private static (string AbstractId, Dictionary<int, int> StartOverrides) ReadList(XmlReader reader)
        {
            using (reader)
            {
                var abstractId = string.Empty;
                var overrides = new Dictionary<int, int>();
                int? level = null;
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element)
                    {
                        continue;
                    }

                    switch (reader.LocalName)
                    {
                        case "abstractNumId":
                            abstractId = Val(reader) ?? abstractId;
                            break;
                        case "lvlOverride":
                            level = int.TryParse(Attribute(reader, "ilvl"), NumberStyles.None, CultureInfo.InvariantCulture, out var l) ? l : null;
                            break;
                        case "startOverride" when level is { } overridden:
                            overrides[overridden] = IntVal(reader) ?? 1;
                            break;
                    }
                }

                return (abstractId, overrides);
            }
        }

        private static string? Attribute(XmlReader reader, string localName)
        {
            for (var i = 0; i < reader.AttributeCount; i++)
            {
                reader.MoveToAttribute(i);
                if (reader.LocalName == localName)
                {
                    var value = reader.Value;
                    reader.MoveToElement();
                    return value;
                }
            }

            reader.MoveToElement();
            return null;
        }
    }

    /// <summary>
    /// A table being read: its rows, the row and the cell being read.
    /// </summary>
    private sealed class TableBuilder(int position)
    {
        public int Position { get; } = position;

        public List<List<string>> Rows { get; } = [];

        public List<string>? Row { get; set; }

        public StringBuilder? Cell { get; set; }

        /// <summary>
        /// The rows that have text, as "cell | cell" lines.
        /// </summary>
        public List<string> Lines() => [.. Rows.Where(r => r.Any(c => c.Length > 0)).Select(r => string.Join(" | ", r))];
    }

    /// <summary>
    /// Streams the document body into blocks.
    /// </summary>
    private sealed class BodyReader(StyleSheet styles, NumberingDefinitions numbering)
    {
        private readonly List<DocumentBlock> _blocks = [];
        private readonly Stack<TableBuilder> _tables = new();
        private readonly StringBuilder _text = new();
        private int _paragraph;
        private string? _style;
        private int? _outline;
        private string? _numbering;
        private int? _listLevel;
        private bool _skipNextContent;

        /// <returns>The blocks, or <c>null</c> when the part is not a WordprocessingML document.</returns>
        public List<DocumentBlock>? Read(XmlReader reader, CancellationToken cancellationToken)
        {
            if (reader.MoveToContent() != XmlNodeType.Element || reader.LocalName != "document")
            {
                return null;
            }

            var advance = true;
            while (!reader.EOF)
            {
                if (advance && !reader.Read())
                {
                    break;
                }

                advance = true;
                if (reader.NodeType == XmlNodeType.EndElement)
                {
                    End(reader.LocalName);
                    continue;
                }

                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                if (Skips(reader))
                {
                    reader.Skip();
                    advance = false;
                    continue;
                }

                var empty = reader.IsEmptyElement;
                switch (reader.LocalName)
                {
                    case "p":
                        cancellationToken.ThrowIfCancellationRequested();
                        StartParagraph();
                        break;
                    case "pPr":
                        ReadParagraphProperties(reader.ReadSubtree());
                        continue;
                    case "sdtPr":
                        // A table of contents is a content control marked as one.
                        _skipNextContent = IsTableOfContents(reader.ReadSubtree());
                        continue;
                    case "sdtContent" when _skipNextContent:
                        _skipNextContent = false;
                        reader.Skip();
                        advance = false;
                        continue;
                    case "t":
                        if (!empty)
                        {
                            _text.Append(reader.ReadElementContentAsString());
                            advance = false;
                        }

                        continue;
                    case "tab":
                        _text.Append(' ');
                        break;
                    case "br" or "cr":
                        _text.Append('\n');
                        break;
                    case "noBreakHyphen":
                        _text.Append('-');
                        break;
                    case "tbl":
                        _tables.Push(new TableBuilder(_paragraph + 1));
                        break;
                    case "tr" when _tables.TryPeek(out var table):
                        table.Row = [];
                        break;
                    case "tc" when _tables.TryPeek(out var table):
                        table.Cell = new StringBuilder();
                        break;
                }

                if (empty)
                {
                    End(reader.LocalName);
                }
            }

            return _blocks;
        }

        private static bool Skips(XmlReader reader) =>
            reader.LocalName is "rPr" or "drawing" or "pict" or "object" or "del" or "moveFrom" or "instrText" or "delText" or "sectPr" or "oMath" or "oMathPara"
            || (reader.LocalName == "Fallback" && reader.NamespaceURI == MarkupCompatibility);

        private void StartParagraph()
        {
            _paragraph++;
            _text.Clear();
            _style = styles.DefaultStyle;
            _outline = null;
            _numbering = null;
            _listLevel = null;
        }

        private void End(string name)
        {
            switch (name)
            {
                case "p":
                    EndParagraph();
                    break;
                case "tc" when _tables.TryPeek(out var table) && table.Cell is { } cell:
                    table.Row?.Add(Clean(cell.ToString()).Replace('\n', ' '));
                    table.Cell = null;
                    break;
                case "tr" when _tables.TryPeek(out var table) && table.Row is { } row:
                    table.Rows.Add(row);
                    table.Row = null;
                    break;
                case "tbl" when _tables.Count > 0:
                    EndTable(_tables.Pop());
                    break;
            }
        }

        private void EndTable(TableBuilder table)
        {
            var lines = table.Lines();
            if (_tables.TryPeek(out var outer))
            {
                // A table in a cell becomes part of that cell's text.
                AppendToCell(outer, string.Join(" / ", lines));
                return;
            }

            if (lines.Count == 0)
            {
                return;
            }

            // A one-cell table is a framed paragraph, not a table.
            var kind = table.Rows.Count == 1 && table.Rows[0].Count == 1 ? DocumentBlockKind.Paragraph : DocumentBlockKind.Table;
            _blocks.Add(new DocumentBlock(kind, string.Join('\n', lines), table.Position));
        }

        private void EndParagraph()
        {
            var text = Clean(_text.ToString());
            if (_tables.TryPeek(out var table))
            {
                AppendToCell(table, text);
                return;
            }

            if (styles.IsTableOfContents(_style))
            {
                return;
            }

            var heading = _outline is { } outline ? (outline is >= 0 and <= 8 ? outline + 1 : null) : styles.HeadingLevel(_style);
            (string Id, int Level)? list = _numbering is { } id
                ? (id, _listLevel ?? 0)
                : styles.Numbering(_style) is { } inherited ? (inherited.NumberingId, _listLevel ?? inherited.Level) : null;
            if (text.Length == 0)
            {
                return;
            }

            // List "0" takes a paragraph out of the list its style would put it in.
            var label = list is { Id: not "0" } item ? numbering.Next(item.Id, item.Level) : null;
            if (heading is { } level)
            {
                // A numbered heading ("2.1 Kargo") keeps its number; a bullet is not part of a heading.
                _blocks.Add(new DocumentBlock(DocumentBlockKind.Heading, label is null or "-" ? text : $"{label} {text}", _paragraph, level));
            }
            else if (label is not null)
            {
                _blocks.Add(new DocumentBlock(DocumentBlockKind.ListItem, $"{label} {text}", _paragraph, list!.Value.Level));
            }
            else
            {
                _blocks.Add(new DocumentBlock(DocumentBlockKind.Paragraph, text, _paragraph));
            }
        }

        private static void AppendToCell(TableBuilder table, string text)
        {
            if (table.Cell is { } cell && text.Length > 0)
            {
                cell.Append(cell.Length > 0 ? " " : string.Empty).Append(text);
            }
        }

        private void ReadParagraphProperties(XmlReader reader)
        {
            using (reader)
            {
                reader.Read();
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element)
                    {
                        continue;
                    }

                    switch (reader.LocalName)
                    {
                        case "pStyle":
                            _style = Val(reader);
                            break;
                        case "outlineLvl":
                            _outline = IntVal(reader);
                            break;
                        case "numId":
                            _numbering = Val(reader);
                            break;
                        case "ilvl":
                            _listLevel = IntVal(reader);
                            break;
                        case "pPrChange" or "rPr" or "sectPr" or "tabs":
                            reader.Skip();
                            break;
                    }
                }
            }
        }

        private static bool IsTableOfContents(XmlReader reader)
        {
            using (reader)
            {
                while (reader.Read())
                {
                    if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "docPartGallery"
                        && Val(reader) is { } gallery && gallery.Contains("Table of Contents", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// The paragraph's text with spaces collapsed and line breaks kept.
        /// </summary>
        private static string Clean(string text)
        {
            var lines = text.Replace(' ', ' ').Split('\n')
                .Select(line => string.Join(' ', line.Split(' ', StringSplitOptions.RemoveEmptyEntries)))
                .Where(line => line.Length > 0);
            return string.Join('\n', lines);
        }
    }
}
