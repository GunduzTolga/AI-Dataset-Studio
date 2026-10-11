using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using AIDatasetStudio.Application.Import;
using AIDatasetStudio.Application.Records;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.ValueObjects;
using AIDatasetStudio.Infrastructure.Documents;

namespace AIDatasetStudio.Infrastructure.Import;

internal sealed class ImportService(IRecordCommandService commands, DocumentReader documents) : IImportService
{
    private const int SampleSize = 100;
    private const int ProgressInterval = 500;

    public IReadOnlyList<string> SupportedExtensions { get; } =
        [".jsonl", ".ndjson", ".json", ".parquet", ".csv", ".tsv", ".txt", ".xlsx", ".xlsm", ".md", ".markdown", ".pdf", ".docx", ".docm", ".html", ".htm", ".zip"];

    // File reading is synchronous, so every operation runs off the caller's (usually the UI) thread.
    public Task<ImportAnalysis> AnalyzeAsync(
        string filePath,
        DatasetType targetType,
        ImportOverrides? overrides = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        return ReadFileAsync(filePath, () => Task.FromResult(Analyze(filePath, targetType, overrides, cancellationToken)), cancellationToken);
    }

    public Task<ImportPreview> PreviewAsync(ImportPlan plan, int maxRows = 20, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRows);

        return ReadFileAsync(plan.FilePath, () =>
        {
            var rows = new List<ImportRowResult>(maxRows);
            foreach (var item in Read(plan, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                rows.Add(item.Convert(plan));
                if (rows.Count == maxRows)
                {
                    break;
                }
            }

            return Task.FromResult(new ImportPreview(rows));
        }, cancellationToken);
    }

    public async Task<ImportResult> ImportAsync(
        ImportPlan plan,
        Guid datasetId,
        IProgress<ImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var fileName = Path.GetFileName(plan.FilePath);
        var unit = plan.Format switch
        {
            ImportFormat.JsonLines or ImportFormat.Markdown or ImportFormat.PlainText or ImportFormat.Html or ImportFormat.Transcript => "line",
            ImportFormat.JsonArray => "item",
            ImportFormat.Pdf or ImportFormat.Archive => "page",
            ImportFormat.Word => "paragraph",
            _ => "row",
        };
        var errors = new List<ImportRowError>();
        var skipped = 0;
        long read = 0;
        var converted = 0;

        // Converted lazily while the records are inserted, so the file is never held in memory.
        IEnumerable<NewRecord> Records()
        {
            foreach (var item in Read(plan, cancellationToken))
            {
                read++;
                var result = item.Convert(plan);
                if (result.Payload is { } payload)
                {
                    converted++;
                    var reference = item.Sheet is { } sheet
                        ? string.Create(CultureInfo.InvariantCulture, $"{fileName}#sheet={EscapeSheet(sheet)}&row={item.Number}")
                        : string.Create(CultureInfo.InvariantCulture, $"{fileName}#{unit}={item.Number}");
                    yield return new NewRecord(payload, new RecordLineage(RecordSourceKind.FileImport, reference));
                }
                else
                {
                    skipped++;
                    if (errors.Count < ImportResult.MaxReportedErrors && result.Error is { } error)
                    {
                        errors.Add(error);
                    }
                }

                // Resumed only after the previous record was written, so every converted record is in.
                if (read % ProgressInterval == 0)
                {
                    progress?.Report(new ImportProgress(read, converted));
                }
            }
        }

        var imported = await ReadFileAsync(
            plan.FilePath,
            () => commands.AddRangeAsync(datasetId, Records(), progress: null, cancellationToken),
            cancellationToken);
        progress?.Report(new ImportProgress(read, imported));
        return new ImportResult(imported, skipped, errors);
    }

    /// <summary>
    /// The rows of the file as the plan reads them.
    /// </summary>
    private IEnumerable<SourceItem> Read(ImportPlan plan, CancellationToken cancellationToken)
    {
        if (plan.Format is not (ImportFormat.Pdf or ImportFormat.Word or ImportFormat.Html or ImportFormat.Archive))
        {
            return SourceFile.Read(plan.FilePath, plan.Format, plan.Csv, plan.Sheet);
        }

        var blocks = documents.Read(plan.FilePath, cancellationToken).Blocks;
        return SourceFile.TextItems(plan.Layout == SourceLayout.Sections ? TextDocuments.Sections(blocks) : TextDocuments.Paragraphs(blocks));
    }

    private ImportAnalysis Analyze(string filePath, DatasetType targetType, ImportOverrides? overrides, CancellationToken cancellationToken)
    {
        var (detected, readableAs) = SourceFile.DetectFormat(filePath);
        var format = overrides?.Format is { } chosen && readableAs.Contains(chosen) ? chosen : detected;
        var analysis = format switch
        {
            ImportFormat.Excel => AnalyzeWorkbook(filePath, targetType, overrides?.Sheet, cancellationToken),
            ImportFormat.Markdown or ImportFormat.PlainText => AnalyzeText(filePath, format, targetType, cancellationToken),
            ImportFormat.Transcript => AnalyzeTranscript(filePath, targetType),
            ImportFormat.Pdf or ImportFormat.Word or ImportFormat.Html or ImportFormat.Archive => AnalyzeDocument(filePath, format, targetType, cancellationToken),
            _ => AnalyzeRows(filePath, format, targetType, overrides?.Csv, cancellationToken),
        };

        // A Markdown file without headings has no sections; its paragraphs are read instead.
        if (format == ImportFormat.Markdown && overrides?.Format is null && analysis.RowCount == 0)
        {
            analysis = AnalyzeText(filePath, ImportFormat.PlainText, targetType, cancellationToken);
        }

        return analysis with { ReadableAs = readableAs };
    }

    private static ImportAnalysis AnalyzeRows(string filePath, ImportFormat format, DatasetType targetType, CsvOptions? csvOverride, CancellationToken cancellationToken)
    {
        var csv = format == ImportFormat.Csv ? csvOverride ?? SourceFile.DetectCsvOptions(filePath) : null;
        var csvSource = csv is null ? null : new CsvSource(filePath, csv);
        var items = csvSource?.Read() ?? SourceFile.Read(filePath, format, csv);

        long rowCount = 0;
        var sample = new List<JsonElement>();
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rowCount++;
            if (sample.Count < SampleSize && item.Row?.Json is { } json)
            {
                // The row's document is disposed when reading moves on.
                sample.Add(json.Clone());
            }
        }

        IReadOnlyList<string> columns;
        SourceLayout layout;
        if (csvSource is null)
        {
            columns = ImportLayouts.ColumnsOf(sample);
            layout = ImportLayouts.DetectFromJson(sample);
        }
        else
        {
            columns = csvSource.ColumnNames;
            layout = csv!.HasHeader ? ImportLayouts.DetectFromColumns(columns) : SourceLayout.Custom;
        }

        return ColumnAnalysis(filePath, format, csv, layout, columns, rowCount, targetType);
    }

    private static ImportAnalysis AnalyzeWorkbook(string filePath, DatasetType targetType, SheetOptions? sheetOverride, CancellationToken cancellationToken)
    {
        var (sheets, active) = ExcelSource.ListSheets(filePath);
        var options = sheetOverride ?? new SheetOptions(active, HasHeader: true);
        var source = new ExcelSource(filePath, options);
        var rowCount = Count(source.Read(), cancellationToken);
        var columns = source.ColumnNames;
        var layout = options.HasHeader ? ImportLayouts.DetectFromColumns(columns) : SourceLayout.Custom;
        return ColumnAnalysis(filePath, ImportFormat.Excel, null, layout, columns, rowCount, targetType) with { Sheet = options, Sheets = sheets };
    }

    private static ImportAnalysis AnalyzeText(string filePath, ImportFormat format, DatasetType targetType, CancellationToken cancellationToken)
    {
        var (layout, columns) = format == ImportFormat.Markdown
            ? (SourceLayout.Sections, TextColumns.Markdown)
            : (SourceLayout.Paragraphs, TextColumns.PlainText);
        var rowCount = Count(SourceFile.Read(filePath, format, csv: null), cancellationToken);
        return new ImportAnalysis(
            filePath,
            format,
            null,
            layout,
            columns,
            rowCount,
            ImportLayouts.SuggestTextMapping(layout, targetType),
            ImportLayouts.Supports(layout, targetType));
    }

    /// <summary>
    /// A conversation log: one row per conversation, its speakers in place of columns.
    /// </summary>
    private static ImportAnalysis AnalyzeTranscript(string filePath, DatasetType targetType)
    {
        var log = TranscriptReader.Read(SourceFile.ReadTextLines(filePath));
        return new ImportAnalysis(
            filePath,
            ImportFormat.Transcript,
            null,
            SourceLayout.Transcript,
            log.Speakers,
            log.Conversations.Count,
            ImportLayouts.SuggestSpeakers(log.Speakers, log.Openers),
            ImportLayouts.Supports(SourceLayout.Transcript, targetType));
    }

    /// <summary>
    /// A PDF, Word or HTML document or a ZIP of pages is read as sections at its headings, or as
    /// paragraphs when it has none.
    /// </summary>
    private ImportAnalysis AnalyzeDocument(string filePath, ImportFormat format, DatasetType targetType, CancellationToken cancellationToken)
    {
        var blocks = documents.Read(filePath, cancellationToken).Blocks;
        var sections = TextDocuments.Sections(blocks).LongCount();
        var (layout, columns, rowCount) = sections > 0
            ? (SourceLayout.Sections, TextColumns.Markdown, sections)
            : (SourceLayout.Paragraphs, TextColumns.PlainText, TextDocuments.Paragraphs(blocks).LongCount());
        return new ImportAnalysis(
            filePath,
            format,
            null,
            layout,
            columns,
            rowCount,
            ImportLayouts.SuggestTextMapping(layout, targetType),
            ImportLayouts.Supports(layout, targetType));
    }

    private static ImportAnalysis ColumnAnalysis(
        string filePath, ImportFormat format, CsvOptions? csv, SourceLayout layout, IReadOnlyList<string> columns, long rowCount, DatasetType targetType)
    {
        var mapping = ImportLayouts.IsColumnBased(layout)
            ? ImportLayouts.SuggestMapping(columns, targetType)
            : new Dictionary<TargetField, string>();
        return new ImportAnalysis(filePath, format, csv, layout, columns, rowCount, mapping, ImportLayouts.Supports(layout, targetType));
    }

    private static long Count(IEnumerable<SourceItem> items, CancellationToken cancellationToken)
    {
        long count = 0;
        foreach (var _ in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            count++;
        }

        return count;
    }

    /// <summary>
    /// A sheet name in a lineage reference, with the characters that separate its parts escaped.
    /// </summary>
    private static string EscapeSheet(string sheet)
    {
        var builder = new StringBuilder(sheet.Length);
        foreach (var c in sheet)
        {
            _ = c is '%' or '&' or '#' or '=' ? builder.Append(CultureInfo.InvariantCulture, $"%{(int)c:X2}") : builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Runs <paramref name="operation"/> on the thread pool and reports file system errors as <see cref="ImportFileException"/>.
    /// </summary>
    private static async Task<T> ReadFileAsync<T>(string filePath, Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(operation, cancellationToken);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new ImportFileException(ImportFileError.NotFound, filePath, $"'{filePath}' was not found.", innerException: ex);
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException)
        {
            // Raised while a workbook's parts are read: the archive or its XML is damaged.
            throw XlsxWorkbook.Invalid(filePath, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ImportFileException(ImportFileError.Unreadable, filePath, $"'{filePath}' cannot be read: {ex.Message}", innerException: ex);
        }
    }
}
