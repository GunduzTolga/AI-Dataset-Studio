using System.Collections.ObjectModel;
using System.ComponentModel;
using AIDatasetStudio.Application.Import;
using AIDatasetStudio.Desktop.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIDatasetStudio.Desktop.ViewModels.Import;

/// <summary>
/// Reads a file into a dataset in three steps on one screen: check how the file is read (format,
/// CSV settings or workbook sheet, column mapping) against a live preview, import with progress,
/// then review what was skipped (SDD §12).
/// </summary>
public sealed partial class ImportWizardViewModel : ViewModelBase
{
    public const int PreviewRows = 20;

    private readonly IImportService _import;
    private readonly Localizer _localizer;
    private CancellationTokenSource? _work;
    private CancellationTokenSource? _importCancellation;
    private bool _applying;

    public ImportWizardViewModel(IImportService import, Localizer localizer, string filePath, ImportTarget target)
    {
        _import = import;
        _localizer = localizer;
        FilePath = filePath;
        Target = target;
        Delimiters = CsvOptions.Delimiters.Select(d => new DelimiterOption(d, ImportText.Delimiter(localizer, d))).ToList();
        Encodings = CsvOptions.Encodings.Select(e => new EncodingOption(e, ImportText.Encoding(localizer, e))).ToList();
    }

    /// <summary>
    /// Raised when the wizard is finished or cancelled; the window closes.
    /// </summary>
    public event EventHandler? CloseRequested;

    public string FilePath { get; }

    public string FileName => Path.GetFileName(FilePath);

    public ImportTarget Target { get; }

    public string TargetText => _localizer.Format("Import_Target", Target.Name, Labels.For(_localizer, Target.Type));

    public IReadOnlyList<DelimiterOption> Delimiters { get; }

    public IReadOnlyList<EncodingOption> Encodings { get; }

    public ObservableCollection<ColumnMappingViewModel> Mappings { get; } = [];

    public ObservableCollection<ImportRowViewModel> Preview { get; } = [];

    /// <summary>
    /// The skipped rows reported by a finished import.
    /// </summary>
    public ObservableCollection<ImportRowViewModel> SkippedRows { get; } = [];

    /// <summary>
    /// The current analysis or preview; awaited by tests and by the view before closing.
    /// </summary>
    public Task PendingWork { get; private set; } = Task.CompletedTask;

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(HasAnalysis), nameof(IsCsv), nameof(IsExcel), nameof(IsAllSheets), nameof(IsColumnBased), nameof(IsConversation),
        nameof(IsUnsupported), nameof(FormatText), nameof(LayoutText), nameof(RowCountText), nameof(ConversationText),
        nameof(UnsupportedText), nameof(StartText), nameof(IsEmptyFile), nameof(TextLayoutHint), nameof(HasTextLayoutHint))]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    public partial ImportAnalysis? Analysis { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPreviewEmpty))]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    public partial bool IsAnalyzing { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; private set; }

    /// <summary>
    /// Information that is not an error, such as "import cancelled".
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string? Notice { get; private set; }

    [ObservableProperty]
    public partial DelimiterOption? SelectedDelimiter { get; set; }

    [ObservableProperty]
    public partial EncodingOption? SelectedEncoding { get; set; }

    /// <summary>
    /// Whether the first row holds column names, for CSV files and workbooks.
    /// </summary>
    [ObservableProperty]
    public partial bool HasHeader { get; set; } = true;

    /// <summary>
    /// The ways this file can be read; a choice is offered when there is more than one.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFormatChoice))]
    public partial IReadOnlyList<FormatOption> FormatOptions { get; private set; } = [];

    [ObservableProperty]
    public partial FormatOption? SelectedFormat { get; set; }

    /// <summary>
    /// The visible sheets of a workbook, with "all sheets" when there are several.
    /// </summary>
    [ObservableProperty]
    public partial IReadOnlyList<SheetOption> Sheets { get; private set; } = [];

    [ObservableProperty]
    public partial SheetOption? SelectedSheet { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMissingFields))]
    public partial string? MissingFieldsText { get; private set; }

    [ObservableProperty]
    public partial string? PreviewSummary { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPreviewEmpty))]
    public partial bool HasPreview { get; private set; }

    /// <summary>
    /// Reading is finished and there is no row to show.
    /// </summary>
    public bool IsPreviewEmpty => !HasPreview && !IsAnalyzing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSetup))]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    public partial bool IsImporting { get; private set; }

    [ObservableProperty]
    public partial double ProgressValue { get; private set; }

    [ObservableProperty]
    public partial string? ProgressText { get; private set; }

    /// <summary>
    /// The finished import; <c>null</c> until then.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDone), nameof(IsSetup), nameof(ResultText), nameof(SkippedText), nameof(HasSkipped), nameof(MoreSkippedText), nameof(HasMoreSkipped))]
    public partial ImportResult? Result { get; private set; }

    public bool HasAnalysis => Analysis is not null;

    public bool IsCsv => Analysis?.Format == ImportFormat.Csv;

    public bool IsExcel => Analysis?.Format == ImportFormat.Excel;

    /// <summary>
    /// Every sheet of a workbook with several is read; rows show their sheet.
    /// </summary>
    public bool IsAllSheets => Analysis is { Format: ImportFormat.Excel, Sheet.Sheet: null, Sheets.Count: > 1 };

    public bool HasFormatChoice => FormatOptions.Count > 1;

    /// <summary>
    /// How a Markdown or plain-text file is split into records.
    /// </summary>
    public string? TextLayoutHint => Analysis?.Layout switch
    {
        SourceLayout.Sections => _localizer["Import_SectionsHint"],
        SourceLayout.Paragraphs => _localizer["Import_ParagraphsHint"],
        _ => null,
    };

    public bool HasTextLayoutHint => TextLayoutHint is not null;

    public bool IsColumnBased => Analysis is { } a && ImportLayouts.IsColumnBased(a.Layout) && a.SupportsTargetType;

    public bool IsConversation => Analysis is { } a && !ImportLayouts.IsColumnBased(a.Layout) && a.SupportsTargetType;

    public bool IsUnsupported => Analysis is { SupportsTargetType: false };

    public bool IsEmptyFile => Analysis is { RowCount: 0 };

    public bool HasError => ErrorMessage is not null;

    public bool HasNotice => Notice is not null;

    public bool HasMissingFields => MissingFieldsText is not null;

    /// <summary>
    /// Choosing how to read the file (as opposed to importing or reviewing the result).
    /// </summary>
    public bool IsSetup => !IsImporting && !IsDone;

    public bool IsDone => Result is not null;

    public string FormatText => Analysis is { } a ? ImportText.Format(_localizer, a.Format) : string.Empty;

    public string LayoutText => Analysis is { } a ? ImportText.Layout(_localizer, a.Layout) : string.Empty;

    public string RowCountText => _localizer.Format("Import_RowCount", Analysis?.RowCount ?? 0);

    public string ConversationText => _localizer.Format("Import_ConversationLayout", LayoutText);

    public string UnsupportedText => _localizer.Format("Import_UnsupportedLayout", LayoutText, Labels.For(_localizer, Target.Type));

    /// <summary>
    /// Label of the import button, with the number of rows.
    /// </summary>
    public string StartText => _localizer.Format("Import_Start", Analysis?.RowCount ?? 0);

    public string ResultText => Result is { } r ? _localizer.Format("Import_DoneSummary", r.Imported, Target.Name) : string.Empty;

    public bool HasSkipped => Result is { Skipped: > 0 };

    public string SkippedText => _localizer.Format("Import_DoneSkipped", Result?.Skipped ?? 0);

    public bool HasMoreSkipped => Result is { } r && r.Skipped > r.Errors.Count;

    public string MoreSkippedText => Result is { } r ? _localizer.Format("Import_MoreSkipped", r.Skipped - r.Errors.Count) : string.Empty;

    /// <summary>
    /// The plan for the current settings; <c>null</c> before the file is analyzed.
    /// </summary>
    public ImportPlan? Plan => Analysis is { } a
        ? new ImportPlan(
            a.FilePath,
            a.Format,
            a.Csv,
            a.Layout,
            IsColumnBased
                ? Mappings.Where(m => m.SelectedColumn?.Column is not null).ToDictionary(m => m.Field, m => m.SelectedColumn!.Column!)
                : new Dictionary<TargetField, string>(),
            Target.Type,
            a.Sheet)
        : null;

    /// <summary>
    /// Reads the file with detected settings and shows the first rows.
    /// </summary>
    public Task InitializeAsync() => Analyze(overrides: null);

    private bool CanImport() => IsSetup && !IsAnalyzing && Analysis is { SupportsTargetType: true, RowCount: > 0 };

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportAsync()
    {
        if (Plan is not { } plan)
        {
            return;
        }

        var total = Analysis!.RowCount;
        using var cancellation = new CancellationTokenSource();
        _importCancellation = cancellation;
        Notice = null;
        ErrorMessage = null;
        ProgressValue = 0;
        ProgressText = _localizer.Format("Import_Progress", 0, total, 0);
        IsImporting = true;
        var progress = new Progress<ImportProgress>(p =>
        {
            ProgressValue = total == 0 ? 0 : Math.Min(1, (double)p.RowsRead / total);
            ProgressText = _localizer.Format("Import_Progress", p.RowsRead, total, p.Imported);
        });

        try
        {
            var result = await _import.ImportAsync(plan, Target.DatasetId, progress, cancellation.Token);
            SkippedRows.Clear();
            foreach (var error in result.Errors)
            {
                SkippedRows.Add(ImportRowViewModel.FromError(error, _localizer, IsAllSheets));
            }

            Result = result;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Notice = _localizer["Import_Cancelled"];
        }
        catch (Exception ex) when (Describe(ex) is { } message)
        {
            ErrorMessage = message;
        }
        finally
        {
            _importCancellation = null;
            IsImporting = false;
        }
    }

    /// <summary>
    /// Stops a running import (nothing is kept), or closes the wizard.
    /// </summary>
    [RelayCommand]
    private void Cancel()
    {
        if (_importCancellation is { } cancellation)
        {
            cancellation.Cancel();
            return;
        }

        _work?.Cancel();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    partial void OnSelectedDelimiterChanged(DelimiterOption? value) => OnSourceOptionChanged();

    partial void OnSelectedEncodingChanged(EncodingOption? value) => OnSourceOptionChanged();

    partial void OnHasHeaderChanged(bool value) => OnSourceOptionChanged();

    partial void OnSelectedSheetChanged(SheetOption? value) => OnSourceOptionChanged();

    // Another reading starts from detected settings.
    partial void OnSelectedFormatChanged(FormatOption? value)
    {
        if (!_applying && value is not null && value.Value != Analysis?.Format)
        {
            _ = Analyze(new ImportOverrides(value.Value));
        }
    }

    /// <summary>
    /// Reads the file again with the CSV settings or the sheet the user chose.
    /// </summary>
    private void OnSourceOptionChanged()
    {
        if (_applying || Analysis is not { } analysis)
        {
            return;
        }

        if (analysis.Format == ImportFormat.Csv && SelectedDelimiter is { } delimiter && SelectedEncoding is { } encoding)
        {
            _ = Analyze(new ImportOverrides(analysis.Format, Csv: new CsvOptions(delimiter.Value, encoding.Name, HasHeader)));
        }
        else if (analysis.Format == ImportFormat.Excel && SelectedSheet is { } sheet)
        {
            _ = Analyze(new ImportOverrides(analysis.Format, Sheet: new SheetOptions(sheet.Name, HasHeader)));
        }
    }

    private void OnMappingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_applying && e.PropertyName == nameof(ColumnMappingViewModel.SelectedColumn))
        {
            UpdateMissingFields();
            _ = Run(RefreshPreviewAsync);
        }
    }

    private Task Analyze(ImportOverrides? overrides) => Run(async token =>
    {
        IsAnalyzing = true;
        try
        {
            var analysis = await _import.AnalyzeAsync(FilePath, Target.Type, overrides, token);
            token.ThrowIfCancellationRequested();
            Apply(analysis);
            await RefreshPreviewAsync(token);
        }
        catch (Exception ex) when (!token.IsCancellationRequested && Describe(ex) is { } message)
        {
            Analysis = null;
            Mappings.Clear();
            ClearPreview();
            ErrorMessage = message;
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsAnalyzing = false;
            }
        }
    });

    /// <summary>
    /// Cancels the previous analysis or preview and runs <paramref name="work"/> in its place.
    /// </summary>
    private Task Run(Func<CancellationToken, Task> work)
    {
        _work?.Cancel();
        _work = new CancellationTokenSource();
        var token = _work.Token;
        PendingWork = RunCore();
        return PendingWork;

        async Task RunCore()
        {
            try
            {
                await work(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Superseded by newer settings.
            }
        }
    }

    private void Apply(ImportAnalysis analysis)
    {
        _applying = true;
        try
        {
            ErrorMessage = null;
            Analysis = analysis;
            var formats = analysis.ReadableAs.Select(f => new FormatOption(f, ImportText.Format(_localizer, f))).ToList();
            if (!formats.SequenceEqual(FormatOptions))
            {
                FormatOptions = formats;
            }

            SelectedFormat = FormatOptions.FirstOrDefault(f => f.Value == analysis.Format);
            if (analysis.Csv is { } csv)
            {
                SelectedDelimiter = Delimiters.FirstOrDefault(d => d.Value == csv.Delimiter) ?? new DelimiterOption(csv.Delimiter, csv.Delimiter.ToString());
                SelectedEncoding = Encodings.FirstOrDefault(e => string.Equals(e.Name, csv.EncodingName, StringComparison.OrdinalIgnoreCase));
                HasHeader = csv.HasHeader;
            }

            if (analysis.Sheet is { } sheet)
            {
                var sheets = analysis.Sheets.Select(name => new SheetOption(name, name)).ToList();
                if (sheets.Count > 1)
                {
                    sheets.Add(new SheetOption(null, _localizer.Format("Import_AllSheets", sheets.Count)));
                }

                if (!sheets.SequenceEqual(Sheets))
                {
                    Sheets = sheets;
                }

                SelectedSheet = Sheets.FirstOrDefault(s => s.Name == sheet.Sheet);
                HasHeader = sheet.HasHeader;
            }

            foreach (var mapping in Mappings)
            {
                mapping.PropertyChanged -= OnMappingChanged;
            }

            Mappings.Clear();
            var columns = new List<ColumnOption> { new(null, _localizer["Import_NotMapped"]) };
            columns.AddRange(analysis.Columns.Select(c => new ColumnOption(c, ImportText.Column(_localizer, analysis.Format, c))));
            foreach (var field in ImportLayouts.FieldsFor(Target.Type))
            {
                var mapping = new ColumnMappingViewModel(field, _localizer[$"Field_{field.Field}"], columns);
                mapping.SelectedColumn = analysis.SuggestedMapping.TryGetValue(field.Field, out var column)
                    ? columns.First(c => c.Column == column)
                    : columns[0];
                mapping.PropertyChanged += OnMappingChanged;
                Mappings.Add(mapping);
            }

            UpdateMissingFields();
        }
        finally
        {
            _applying = false;
        }
    }

    private async Task RefreshPreviewAsync(CancellationToken token)
    {
        if (Plan is not { } plan || !Analysis!.SupportsTargetType)
        {
            ClearPreview();
            return;
        }

        var preview = await _import.PreviewAsync(plan, PreviewRows, token);
        token.ThrowIfCancellationRequested();
        Preview.Clear();
        foreach (var row in preview.Rows)
        {
            Preview.Add(ImportRowViewModel.From(row, _localizer, IsAllSheets));
        }

        HasPreview = Preview.Count > 0;
        var valid = preview.Rows.Count(r => r.IsValid);
        PreviewSummary = preview.Rows.Count == 0
            ? null
            : _localizer.Format("Import_PreviewSummary", preview.Rows.Count, valid, preview.Rows.Count - valid);
    }

    private void ClearPreview()
    {
        Preview.Clear();
        HasPreview = false;
        PreviewSummary = null;
    }

    private void UpdateMissingFields()
    {
        var missing = IsColumnBased ? Mappings.Where(m => m.IsMissing).Select(m => m.Label).ToList() : [];
        MissingFieldsText = missing.Count == 0 ? null : _localizer.Format("Import_MissingFields", string.Join(", ", missing));
    }

    private string? Describe(Exception exception) => exception switch
    {
        ImportFileException ex => ImportText.FileError(_localizer, ex),
        _ when UiErrors.TryDescribe(exception) is { } error => _localizer.Format(error.Key, error.Args),
        _ => null,
    };
}
