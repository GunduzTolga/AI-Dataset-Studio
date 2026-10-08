using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using AIDatasetStudio.Application.Import;

namespace AIDatasetStudio.Infrastructure.Import;

/// <summary>
/// A worksheet of a workbook: its tab name and the package part that holds its cells.
/// </summary>
internal sealed record XlsxSheet(string Name, string Part, bool IsHidden);

/// <summary>
/// Reads the cell values of Office Open XML workbooks (.xlsx, .xlsm) as text, without formulas,
/// formatting or charts. Rows are streamed from the sheet, so large sheets are never held in memory.
/// Elements are matched by local name, so transitional and strict documents both read.
/// </summary>
internal sealed class XlsxWorkbook : IDisposable
{
    private static readonly byte[] ZipSignature = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] CompoundFileSignature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    private static readonly XmlReaderSettings XmlSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        CloseInput = true,
    };

    private readonly ZipArchive _zip;
    private readonly Dictionary<string, ZipArchiveEntry> _entries;
    private readonly List<string> _sharedStrings = [];
    private readonly List<bool> _dateStyles = [];
    private bool _date1904;

    private XlsxWorkbook(ZipArchive zip)
    {
        _zip = zip;
        _entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            _entries.TryAdd(entry.FullName.Replace('\\', '/').TrimStart('/'), entry);
        }
    }

    /// <summary>
    /// Every worksheet in tab order; chart and dialog sheets are left out.
    /// </summary>
    public IReadOnlyList<XlsxSheet> Sheets { get; private set; } = [];

    /// <summary>
    /// The sheets a user sees, or every sheet when all of them are hidden.
    /// </summary>
    public IReadOnlyList<XlsxSheet> VisibleSheets => Sheets.Any(s => !s.IsHidden) ? [.. Sheets.Where(s => !s.IsHidden)] : Sheets;

    /// <summary>
    /// The visible sheet that was open when the workbook was saved, or the first visible one.
    /// </summary>
    public XlsxSheet? ActiveSheet { get; private set; }

    /// <summary>
    /// Whether the file starts like an Excel 97–2003 workbook. Password-protected .xlsx files
    /// are stored the same way.
    /// </summary>
    public static bool IsCompoundFile(ReadOnlySpan<byte> start) => start.StartsWith(CompoundFileSignature);

    public static bool IsZip(ReadOnlySpan<byte> start) => start.StartsWith(ZipSignature);

    /// <exception cref="ImportFileException">The file is not a workbook this reader understands.</exception>
    public static XlsxWorkbook Open(string path)
    {
        var stream = SourceFile.OpenRead(path);
        try
        {
            Span<byte> start = stackalloc byte[CompoundFileSignature.Length];
            var length = stream.ReadAtLeast(start, start.Length, throwOnEndOfStream: false);
            if (IsCompoundFile(start[..length]))
            {
                throw new ImportFileException(ImportFileError.LegacyWorkbook, path, $"'{path}' is an Excel 97-2003 or password-protected workbook.");
            }

            stream.Position = 0;
            var workbook = new XlsxWorkbook(new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false));
            try
            {
                workbook.Load(path);
                return workbook;
            }
            catch
            {
                workbook.Dispose();
                throw;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException)
        {
            stream.Dispose();
            throw Invalid(path, ex);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public static ImportFileException Invalid(string path, Exception? innerException = null) =>
        new(ImportFileError.InvalidWorkbook, path, $"'{path}' is not a valid Excel workbook.", innerException: innerException);

    public XlsxSheet? Find(string name) =>
        Sheets.FirstOrDefault(s => s.Name == name) ?? Sheets.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The rows of <paramref name="sheet"/> that have a value: the row number Excel shows and the
    /// cell texts by column (A = 0), <c>null</c> for empty cells, up to the last cell with a value.
    /// </summary>
    public IEnumerable<(long Number, string?[] Cells)> ReadRows(XlsxSheet sheet)
    {
        if (!_entries.TryGetValue(sheet.Part, out var entry))
        {
            yield break;
        }

        using var reader = XmlReader.Create(entry.Open(), XmlSettings);
        var cells = new List<string?>();
        long nextRow = 1;
        long row = 0;
        var inRow = false;
        var column = 0;
        var nextColumn = 0;
        string? type = null;
        var isDate = false;
        string? value = null;
        var inCell = false;

        reader.MoveToContent();
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "row":
                        row = ParseNumber(reader.GetAttribute("r")) ?? nextRow;
                        nextRow = row + 1;
                        nextColumn = 0;
                        cells.Clear();
                        if (reader.IsEmptyElement)
                        {
                            reader.Read();
                            continue;
                        }

                        inRow = true;
                        break;

                    case "c" when inRow:
                        column = ColumnIndex(reader.GetAttribute("r")) ?? nextColumn;
                        nextColumn = column + 1;
                        type = reader.GetAttribute("t");
                        isDate = ParseNumber(reader.GetAttribute("s")) is { } style && style < _dateStyles.Count && _dateStyles[(int)style];
                        value = null;
                        if (reader.IsEmptyElement)
                        {
                            reader.Read();
                            continue;
                        }

                        inCell = true;
                        break;

                    case "v" when inCell:
                        value = reader.ReadElementContentAsString();
                        continue;

                    case "is" when inCell:
                        value = ReadText(reader);
                        break;

                    case "f" or "extLst":
                        reader.Skip();
                        continue;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (reader.LocalName == "c" && inCell)
                {
                    inCell = false;
                    if (CellText(type, value, isDate) is { Length: > 0 } text)
                    {
                        while (cells.Count <= column)
                        {
                            cells.Add(null);
                        }

                        cells[column] = text;
                    }
                }
                else if (reader.LocalName == "row" && inRow)
                {
                    inRow = false;
                    if (cells.Count > 0)
                    {
                        yield return (row, cells.ToArray());
                    }
                }
            }

            reader.Read();
        }
    }

    public void Dispose() => _zip.Dispose();

    /// <summary>
    /// "A" → 0, "AB12" → 27; <c>null</c> when the reference is missing or malformed.
    /// </summary>
    public static int? ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return null;
        }

        var index = 0;
        var letters = 0;
        foreach (var c in reference)
        {
            if (!char.IsAsciiLetter(c))
            {
                break;
            }

            index = (index * 26) + (char.ToUpperInvariant(c) - 'A' + 1);
            letters++;
            if (letters > 3)
            {
                return null;
            }
        }

        return letters == 0 ? null : index - 1;
    }

    /// <summary>
    /// 0 → "A", 27 → "AB": the column names Excel shows.
    /// </summary>
    public static string ColumnName(int index)
    {
        var name = string.Empty;
        for (var n = index + 1; n > 0; n = (n - 1) / 26)
        {
            name = (char)('A' + ((n - 1) % 26)) + name;
        }

        return name;
    }

    private static long? ParseNumber(string? text) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;

    private void Load(string path)
    {
        var workbookPart = Relationships(string.Empty, "_rels/.rels").FirstOrDefault(r => r.Type.EndsWith("/officeDocument", StringComparison.Ordinal)).Target
            ?? "xl/workbook.xml";
        if (!_entries.ContainsKey(workbookPart))
        {
            throw Invalid(path);
        }

        var folder = workbookPart.Contains('/', StringComparison.Ordinal) ? workbookPart[..(workbookPart.LastIndexOf('/') + 1)] : string.Empty;
        var relationships = Relationships(folder, $"{folder}_rels/{workbookPart[folder.Length..]}.rels");

        if (relationships.FirstOrDefault(r => r.Type.EndsWith("/sharedStrings", StringComparison.Ordinal)).Target is { } sharedStrings)
        {
            LoadSharedStrings(sharedStrings);
        }

        if (relationships.FirstOrDefault(r => r.Type.EndsWith("/styles", StringComparison.Ordinal)).Target is { } styles)
        {
            LoadStyles(styles);
        }

        var worksheets = relationships
            .Where(r => r.Type.EndsWith("/worksheet", StringComparison.Ordinal))
            .ToDictionary(r => r.Id, r => r.Target, StringComparer.Ordinal);
        var sheets = new List<XlsxSheet>();
        var activeTab = 0;
        var tab = 0;
        using (var reader = Xml(workbookPart))
        {
            while (reader?.Read() == true)
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                switch (reader.LocalName)
                {
                    case "workbookPr":
                        _date1904 = reader.GetAttribute("date1904") is "1" or "true";
                        break;
                    case "workbookView":
                        activeTab = (int)Math.Min(ParseNumber(reader.GetAttribute("activeTab")) ?? 0, int.MaxValue);
                        break;
                    case "sheet":
                        // The relationship id is the "id" attribute in the relationships namespace.
                        var id = RelationshipId(reader);
                        if (id is not null && worksheets.TryGetValue(id, out var part) && reader.GetAttribute("name") is { } name)
                        {
                            var sheet = new XlsxSheet(name, part, reader.GetAttribute("state") is "hidden" or "veryHidden");
                            sheets.Add(sheet);
                            if (tab == activeTab && !sheet.IsHidden)
                            {
                                ActiveSheet = sheet;
                            }
                        }

                        tab++;
                        break;
                }
            }
        }

        Sheets = sheets;
        ActiveSheet ??= VisibleSheets.FirstOrDefault();
    }

    private static string? RelationshipId(XmlReader reader)
    {
        for (var i = 0; i < reader.AttributeCount; i++)
        {
            reader.MoveToAttribute(i);
            if (reader.LocalName == "id" && reader.NamespaceURI.Length > 0)
            {
                var id = reader.Value;
                reader.MoveToElement();
                return id;
            }
        }

        reader.MoveToElement();
        return null;
    }

    private List<(string Id, string Type, string Target)> Relationships(string folder, string part)
    {
        var relationships = new List<(string Id, string Type, string Target)>();
        using var reader = Xml(part);
        while (reader?.Read() == true)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Relationship" && reader.GetAttribute("TargetMode") != "External"
                && reader.GetAttribute("Id") is { } id && reader.GetAttribute("Type") is { } type && reader.GetAttribute("Target") is { } target)
            {
                relationships.Add((id, type, Resolve(folder, target)));
            }
        }

        return relationships;
    }

    /// <summary>
    /// A relationship target as a package path: absolute ("/xl/…") or relative to the source's folder.
    /// </summary>
    internal static string Resolve(string folder, string target)
    {
        var path = target.StartsWith('/') ? target.TrimStart('/') : folder + target;
        var segments = new List<string>();
        foreach (var segment in Uri.UnescapeDataString(path).Replace('\\', '/').Split('/'))
        {
            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }
            }
            else if (segment is not ("." or ""))
            {
                segments.Add(segment);
            }
        }

        return string.Join('/', segments);
    }

    private void LoadSharedStrings(string part)
    {
        using var reader = Xml(part);
        while (reader?.Read() == true)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "si")
            {
                _sharedStrings.Add(ReadText(reader));
            }
        }
    }

    private void LoadStyles(string part)
    {
        var formats = new Dictionary<long, string>();
        using var reader = Xml(part);
        var inCellFormats = false;
        while (reader?.Read() == true)
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "cellXfs")
            {
                inCellFormats = false;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            switch (reader.LocalName)
            {
                case "numFmt" when ParseNumber(reader.GetAttribute("numFmtId")) is { } id:
                    formats[id] = reader.GetAttribute("formatCode") ?? string.Empty;
                    break;
                case "cellXfs":
                    inCellFormats = !reader.IsEmptyElement;
                    break;
                case "xf" when inCellFormats:
                    var formatId = ParseNumber(reader.GetAttribute("numFmtId")) ?? 0;
                    _dateStyles.Add(formats.TryGetValue(formatId, out var code) ? IsDateFormat(code) : IsBuiltInDateFormat(formatId));
                    break;
            }
        }
    }

    // 14–22: dates and times; 27–36 and 50–58: East Asian dates; 45–47: minutes and seconds.
    private static bool IsBuiltInDateFormat(long id) => id is (>= 14 and <= 22) or (>= 27 and <= 36) or (>= 45 and <= 47) or (>= 50 and <= 58);

    /// <summary>
    /// Whether a custom number format shows a date or time: it has a date or time letter outside
    /// quoted text, escaped characters and [colour]/[$currency] sections.
    /// </summary>
    internal static bool IsDateFormat(string code)
    {
        var quoted = false;
        var bracket = false;
        for (var i = 0; i < code.Length; i++)
        {
            var c = code[i];
            if (quoted)
            {
                quoted = c != '"';
            }
            else if (bracket)
            {
                bracket = c != ']';
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == '[')
            {
                // [h], [mm] and [ss] are elapsed times; everything else in brackets is not a date part.
                if (i + 1 < code.Length && char.ToLowerInvariant(code[i + 1]) is 'h' or 'm' or 's')
                {
                    return true;
                }

                bracket = true;
            }
            else if (c is '\\' or '_' or '*')
            {
                i++;
            }
            else if (char.ToLowerInvariant(c) is 'y' or 'm' or 'd' or 'h' or 's')
            {
                return true;
            }
            else if (c == ';')
            {
                // Only the first section (positive numbers) decides.
                return false;
            }
        }

        return false;
    }

    private string? CellText(string? type, string? value, bool isDate)
    {
        switch (type)
        {
            case "s":
                return ParseNumber(value) is { } index && index < _sharedStrings.Count ? _sharedStrings[(int)index] : null;
            case "inlineStr":
                return value;
            case "str" or "e" or "d":
                return value is null ? null : Unescape(value);
            case "b":
                return value is null ? null : value.Trim() is "1" or "true" ? "TRUE" : "FALSE";
            default:
                if (value is null || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                {
                    return value;
                }

                return isDate && DateText(number) is { } date ? date : number.ToString("G15", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// A date serial as ISO text: "2024-03-01", "2024-03-01 14:30" or "14:30:15".
    /// </summary>
    private string? DateText(double serial)
    {
        var isTime = serial is >= 0 and < 1;
        if (_date1904)
        {
            serial += 1462;
        }
        else if (serial < 61)
        {
            // Excel counts a 29 February 1900 that never was; serials before it are one day off.
            serial += 1;
        }

        DateTime date;
        try
        {
            date = DateTime.FromOADate(serial);
        }
        catch (ArgumentException)
        {
            return null;
        }

        date = new DateTime((date.Ticks + (TimeSpan.TicksPerSecond / 2)) / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Unspecified);
        var time = date.TimeOfDay.Seconds == 0 ? "HH:mm" : "HH:mm:ss";
        var format = isTime ? time : date.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd " + time;
        return date.ToString(format, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The text of a shared or inline string, rich text runs joined; phonetic hints are left out.
    /// </summary>
    private static string ReadText(XmlReader reader)
    {
        var builder = new StringBuilder();
        using (var text = reader.ReadSubtree())
        {
            text.Read();
            while (!text.EOF)
            {
                if (text.NodeType == XmlNodeType.Element && text.Depth > 0)
                {
                    if (text.LocalName == "t")
                    {
                        builder.Append(text.ReadElementContentAsString());
                        continue;
                    }

                    if (text.LocalName == "rPh")
                    {
                        text.Skip();
                        continue;
                    }
                }

                text.Read();
            }
        }

        return Unescape(builder.ToString());
    }

    /// <summary>
    /// Decodes the "_xHHHH_" escapes Excel writes for characters XML cannot hold, such as "_x000D_".
    /// </summary>
    internal static string Unescape(string text)
    {
        var start = text.IndexOf("_x", StringComparison.Ordinal);
        if (start < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        var copied = 0;
        for (var i = start; i >= 0 && i + 7 <= text.Length; i = text.IndexOf("_x", i, StringComparison.Ordinal))
        {
            if (text[i + 6] == '_' && ushort.TryParse(text.AsSpan(i + 2, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code))
            {
                builder.Append(text, copied, i - copied).Append((char)code);
                i += 7;
                copied = i;
            }
            else
            {
                i += 2;
            }
        }

        return builder.Append(text, copied, text.Length - copied).ToString();
    }

    private XmlReader? Xml(string part) =>
        _entries.TryGetValue(part, out var entry) ? XmlReader.Create(entry.Open(), XmlSettings) : null;
}
