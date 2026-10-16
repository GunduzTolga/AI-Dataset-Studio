using AIDatasetStudio.Desktop.Controls;
using AIDatasetStudio.Desktop.Localization;
using AIDatasetStudio.Desktop.Tests.Support;
using AIDatasetStudio.Desktop.Views;
using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Highlighting;

namespace AIDatasetStudio.Desktop.Tests.Views;

/// <summary>
/// The highlighting text fields of the record editor (M11, SDD §6.1).
/// </summary>
[Collection(LanguageCollection.Name)]
public sealed class CodeEditorTests
{
    private const string Answer = "Şöyle **yapın**:\n\n```python\nimport os  # yorum\nprint(\"merhaba\")\n```\n\n- `os` modülü";

    public CodeEditorTests() => Localizer.Instance.SetCulture(Localizer.English);

    [Fact]
    public Task Fields_colour_markdown_with_fenced_code_and_json_in_the_theme_colours() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetAsync(DatasetType.Instruction, [new InstructionPayload("Ne yapmalıyım?", "{\"adet\": 3, \"not\": null}", Answer)]);
        var window = Open(host);
        await SelectAsync(host, window, 0);
        var fields = EditorFields(window);

        Assert.Equal(["Markdown", "Json", "Markdown"], fields.Select(f => f.SyntaxHighlighting.Name));
        Assert.Equal([false, true, false], fields.Select(f => f.IsJson));
        Assert.Contains("json", fields[1].Classes);

        // Python inside the fence, Markdown around it, all in the light theme's syntax colours.
        Assert.Equal(Token(window, "SyntaxKeywordColor"), ColourAt(fields[2], "import"));
        Assert.Equal(Token(window, "SyntaxCommentColor"), ColourAt(fields[2], "# yorum"));
        Assert.Equal(Token(window, "SyntaxStringColor"), ColourAt(fields[2], "\"merhaba\""));
        Assert.Equal(Token(window, "SyntaxFunctionColor"), ColourAt(fields[2], "print"));
        Assert.Equal(Token(window, "SyntaxCommentColor"), ColourAt(fields[2], "```python"));
        Assert.Equal(Token(window, "SyntaxCodeColor"), ColourAt(fields[2], "`os`"));
        Assert.Equal(FontWeight.Bold, WeightAt(fields[2], "**yapın**"));
        Assert.Equal(Token(window, "SyntaxPropertyColor"), ColourAt(fields[1], "\"adet\""));
        Assert.Equal(Token(window, "SyntaxNumberColor"), ColourAt(fields[1], "3"));
        Assert.Equal(Token(window, "SyntaxLiteralColor"), ColourAt(fields[1], "null"));

        // Dark theme: the same rules, in the dark colours.
        window.RequestedThemeVariant = ThemeVariant.Dark;
        Dispatcher.UIThread.RunJobs();
        Assert.NotEqual(Token(window, "SyntaxKeywordColor"), Token(window, "SyntaxKeywordColor", ThemeVariant.Light));
        Assert.Equal(Token(window, "SyntaxKeywordColor"), ColourAt(fields[2], "import"));
        Assert.Equal(Token(window, "SyntaxPropertyColor"), ColourAt(fields[1], "\"adet\""));
        window.Close();
    });

    [Fact]
    public Task Typing_reaches_the_record_and_a_text_set_by_the_record_replaces_the_document() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetAsync(DatasetType.Instruction,
        [
            new InstructionPayload("Soru 1", "", "Yanıt 1"),
            new InstructionPayload("Soru 2", "", "Yanıt 2"),
        ]);
        var window = Open(host);
        var details = host.Main.Details;
        await SelectAsync(host, window, 0);
        var fields = EditorFields(window);
        Assert.Equal("Empty", fields[1].TextArea.Watermark);

        fields[2].TextArea.Focus();
        fields[2].CaretOffset = fields[2].Text!.Length;
        window.KeyTextInput(" ve {}");
        Assert.Equal("Yanıt 1 ve {}", details.Fields[3].Text);
        Assert.True(details.IsDirty);
        Assert.True(fields[2].Document.UndoStack.CanUndo);

        // Tab goes on to the next field instead of typing a tab.
        fields[0].TextArea.Focus();
        Press(window, Key.Tab, RawInputModifiers.None);
        Assert.Same(fields[1].TextArea, window.FocusManager?.GetFocusedElement());
        Assert.Equal("Soru 1", details.Fields[0].Text);

        // Typing JSON switches the colours at once.
        details.Fields[1].Text = "[{\"a\": 1}]";
        Assert.Equal("[{\"a\": 1}]", fields[1].Document.Text);
        Assert.True(fields[1].IsJson);

        await details.SaveCommand.ExecuteAsync(null);
        await SelectAsync(host, window, 1);
        fields = EditorFields(window);
        Assert.Equal("Yanıt 2", fields[2].Document.Text);
        Assert.False(fields[2].Document.UndoStack.CanUndo); // the previous record's edits are not undoable here
        Assert.False(fields[1].IsJson);
        window.Close();
    });

    [Fact]
    public Task Archived_records_are_read_only_but_can_be_copied() => UiTest.RunAsync(async () =>
    {
        await using var host = new DesktopTestHost();
        await host.CreateDatasetAsync(DatasetType.Instruction, [new InstructionPayload("Soru", "", "Yanıt")]);
        var window = Open(host);
        await SelectAsync(host, window, 0);
        await host.Main.Details.ArchiveCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.All(EditorFields(window), field => Assert.True(field.IsReadOnly));

        // Right-click still copies; cutting and pasting are off.
        var field = EditorFields(window)[2];
        field.Select(0, field.Document.TextLength);
        var menu = Assert.IsType<MenuFlyout>(field.ContextFlyout);
        menu.ShowAt(field);
        Dispatcher.UIThread.RunJobs();
        var items = menu.Items.OfType<MenuItem>().ToList();
        Assert.Equal(["Cut", "Copy", "Paste", "Select all"], items.Select(i => i.Header));
        Assert.Equal([false, true, false, true], items.Select(i => i.IsEnabled));
        menu.Hide();
        window.Close();
    });

    [Theory]
    [InlineData("{\"a\": 1}", true)]
    [InlineData("  [\n  {\"a\": 1}\n]  ", true)]
    [InlineData("[1, 2, 3]", true)]
    [InlineData("[]", true)]
    [InlineData("[EMAIL_1] adresine yazın.", false)]
    [InlineData("[bağlantı](https://ornek.com)", false)]
    [InlineData("{\"a\": 1", false)]
    [InlineData("Merhaba {ad}", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Json_documents_are_told_from_text(string? text, bool expected)
    {
        Assert.Equal(expected, CodeHighlighting.LooksLikeJson(text));
    }

    private static Color Token(Control control, string key, ThemeVariant? theme = null)
    {
        Assert.True(control.TryFindResource(key, theme ?? control.ActualThemeVariant, out var value));
        return Assert.IsType<Color>(value);
    }

    /// <summary>
    /// The foreground of the innermost coloured section that covers <paramref name="text"/>.
    /// </summary>
    private static Color? ColourAt(CodeEditor editor, string text) =>
        Sections(editor, text).Select(s => s.Color.Foreground).OfType<HighlightingBrush>().Select(b => (b.GetBrush(null!) as ISolidColorBrush)?.Color).FirstOrDefault();

    private static FontWeight? WeightAt(CodeEditor editor, string text) =>
        Sections(editor, text).Select(s => s.Color.FontWeight).FirstOrDefault(w => w is not null);

    /// <summary>
    /// The highlighted sections around <paramref name="text"/>, innermost first.
    /// </summary>
    private static List<HighlightedSection> Sections(CodeEditor editor, string text)
    {
        var offset = editor.Document.Text.IndexOf(text, StringComparison.Ordinal);
        Assert.True(offset >= 0, $"'{text}' is not in the field");
        var line = editor.Document.GetLineByOffset(offset);
        using var highlighter = new DocumentHighlighter(editor.Document, editor.SyntaxHighlighting);
        return [.. highlighter.HighlightLine(line.LineNumber).Sections
            .Where(s => s.Offset <= offset && s.Offset + s.Length >= offset + text.Length)
            .Reverse()
            .OrderBy(s => s.Length)];
    }

    private static MainWindow Open(DesktopTestHost host)
    {
        var window = new MainWindow { DataContext = host.Main, Width = 1440, Height = 860 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static async Task SelectAsync(DesktopTestHost host, Window window, int index)
    {
        var row = host.Main.Records.Items[index];
        host.Main.Records.SelectedItem = row;
        await host.WaitForDetailsAsync(row.Id);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static List<CodeEditor> EditorFields(Window window)
    {
        window.UpdateLayout();
        return [.. window.GetVisualDescendants().OfType<CodeEditor>().Where(field => field.IsEffectivelyVisible)];
    }

    private static void Press(Window window, Key key, RawInputModifiers modifiers)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }
}
