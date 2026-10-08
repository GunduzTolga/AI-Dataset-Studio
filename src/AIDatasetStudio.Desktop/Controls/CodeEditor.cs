using AIDatasetStudio.Desktop.Localization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Styling;
using AvaloniaEdit;

namespace AIDatasetStudio.Desktop.Controls;

/// <summary>
/// A text field of the record editor (SDD §6.1): it wraps, grows with its text and colours
/// Markdown, fenced code blocks (```json, ```python …) and whole JSON documents. Unlike the
/// <see cref="TextEditor"/> it is built on, its <see cref="Text"/> binds both ways, Tab moves to
/// the next field and right-click offers cut, copy and paste, as in a text box.
/// </summary>
public sealed class CodeEditor : TextEditor
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<CodeEditor, string?>(nameof(Text), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string?> PlaceholderTextProperty =
        AvaloniaProperty.Register<CodeEditor, string?>(nameof(PlaceholderText));

    private bool _syncing;
    private (bool Json, ThemeVariant Theme)? _highlighting;

    public CodeEditor()
    {
        WordWrap = true;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Options = new TextEditorOptions
        {
            AcceptsTab = false,
            AllowScrollBelowDocument = false,
            EnableHyperlinks = false,
            EnableEmailHyperlinks = false,
            HighlightCurrentLine = false,
            EnableRectangularSelection = false,
        };
        TextChanged += OnDocumentTextChanged;
        ActualThemeVariantChanged += (_, _) => UpdateHighlighting();
        ContextFlyout = CreateContextMenu();
    }

    /// <summary>
    /// The text; set from a binding, it replaces the document and its undo history.
    /// </summary>
    public new string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>
    /// Shown while the text is empty.
    /// </summary>
    public string? PlaceholderText
    {
        get => GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    /// <summary>
    /// Whether the text is coloured as a JSON document rather than Markdown.
    /// </summary>
    public bool IsJson => _highlighting?.Json == true;

    // Styled by the TextEditor theme of AvaloniaEdit.
    protected override Type StyleKeyOverride => typeof(TextEditor);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty && !_syncing)
        {
            var text = change.GetNewValue<string?>() ?? string.Empty;
            if (base.Text != text)
            {
                _syncing = true;
                try
                {
                    base.Text = text;
                }
                finally
                {
                    _syncing = false;
                }
            }

            UpdateHighlighting();
        }
        else if (change.Property == PlaceholderTextProperty)
        {
            TextArea.Watermark = change.GetNewValue<string?>()!;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateHighlighting();
    }

    private void OnDocumentTextChanged(object? sender, EventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        _syncing = true;
        try
        {
            SetCurrentValue(TextProperty, base.Text);
        }
        finally
        {
            _syncing = false;
        }

        UpdateHighlighting();
    }

    private MenuFlyout CreateContextMenu()
    {
        var items = new (string Key, RoutedCommand Command)[]
        {
            ("Editor_Cut", ApplicationCommands.Cut),
            ("Editor_Copy", ApplicationCommands.Copy),
            ("Editor_Paste", ApplicationCommands.Paste),
            ("Editor_SelectAll", ApplicationCommands.SelectAll),
        };
        var menu = new MenuFlyout();
        foreach (var (key, command) in items)
        {
            var item = new MenuItem { Tag = command };
            item.Bind(HeaderedSelectingItemsControl.HeaderProperty, new TrExtension(key).ProvideValue(null!));
            item.Click += (_, _) => command.Execute(null, TextArea);
            menu.Items.Add(item);
        }

        // Read-only text can still be copied; commands that change it are disabled.
        menu.Opening += (_, _) =>
        {
            foreach (var item in menu.Items.OfType<MenuItem>())
            {
                item.IsEnabled = ((RoutedCommand)item.Tag!).CanExecute(null, TextArea);
            }
        };
        return menu;
    }

    private void UpdateHighlighting()
    {
        var current = (CodeHighlighting.LooksLikeJson(base.Text), ActualThemeVariant);
        if (_highlighting == current)
        {
            return;
        }

        _highlighting = current;
        SyntaxHighlighting = current.Item1 ? CodeHighlighting.Json(current.ActualThemeVariant) : CodeHighlighting.Markdown(current.ActualThemeVariant);
        // Styled with the monospace font in Controls.axaml.
        Classes.Set("json", current.Item1);
    }
}
