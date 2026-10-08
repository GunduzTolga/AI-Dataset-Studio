using System.Xml;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;

namespace AIDatasetStudio.Desktop.Controls;

/// <summary>
/// The syntax definitions of the record editor (Highlighting/*.xshd), coloured with the theme's
/// syntax tokens (SyntaxStringColor …, see Themes/Tokens.axaml). Each theme variant gets its own
/// loaded copy, so switching themes only swaps definitions.
/// </summary>
public static class CodeHighlighting
{
    // Loaded first: the Markdown definition colours fenced code blocks with them.
    private static readonly string[] Languages = ["Json", "Python", "JavaScript", "CSharp", "Sql", "Shell", "Xml"];

    // Colour names used in the definitions → the theme token that colours them. Strong and
    // Emphasis keep the text colour and are only bold or italic.
    private static readonly Dictionary<string, string> Tokens = new(StringComparer.Ordinal)
    {
        ["Comment"] = "SyntaxCommentColor",
        ["Quote"] = "SyntaxCommentColor",
        ["Fence"] = "SyntaxCommentColor",
        ["String"] = "SyntaxStringColor",
        ["Keyword"] = "SyntaxKeywordColor",
        ["Number"] = "SyntaxNumberColor",
        ["Attribute"] = "SyntaxNumberColor",
        ["Literal"] = "SyntaxLiteralColor",
        ["Property"] = "SyntaxPropertyColor",
        ["Tag"] = "SyntaxPropertyColor",
        ["Function"] = "SyntaxFunctionColor",
        ["Variable"] = "SyntaxVariableColor",
        ["Heading"] = "SyntaxHeadingColor",
        ["Code"] = "SyntaxCodeColor",
        ["Link"] = "SyntaxLinkColor",
        ["Marker"] = "SyntaxMarkerColor",
    };

    // Colour names set in the monospace font (MonoFontFamily).
    private static readonly HashSet<string> Monospaced = new(StringComparer.Ordinal) { "Code", "CodeBlock", "Fence" };

    private static readonly Dictionary<ThemeVariant, Resolver> Loaded = [];

    /// <summary>
    /// Markdown with fenced code blocks: the editor's default.
    /// </summary>
    public static IHighlightingDefinition Markdown(ThemeVariant theme) => For(theme).Get("Markdown");

    /// <summary>
    /// A whole JSON document.
    /// </summary>
    public static IHighlightingDefinition Json(ThemeVariant theme) => For(theme).Get("Json");

    /// <summary>
    /// Whether a text is a JSON object or array rather than prose that happens to start with a
    /// bracket ("[EMAIL_1] adresine…", "[bağlantı](…)"): it is enclosed in braces, or in square
    /// brackets around something a JSON value starts with.
    /// </summary>
    public static bool LooksLikeJson(string? text)
    {
        var span = text.AsSpan().Trim();
        if (span.Length < 2)
        {
            return false;
        }

        if (span[0] == '{')
        {
            return span[^1] == '}';
        }

        if (span[0] != '[' || span[^1] != ']')
        {
            return false;
        }

        var inner = span[1..].TrimStart();
        return inner[0] is '{' or '[' or '"' or ']' or '-' || char.IsAsciiDigit(inner[0])
            || inner.StartsWith("true", StringComparison.Ordinal)
            || inner.StartsWith("false", StringComparison.Ordinal)
            || inner.StartsWith("null", StringComparison.Ordinal);
    }

    private static Resolver For(ThemeVariant theme)
    {
        // Only the UI thread loads definitions; a theme that is neither light nor dark is read as light.
        var key = theme == ThemeVariant.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
        if (!Loaded.TryGetValue(key, out var resolver))
        {
            resolver = new Resolver();
            foreach (var name in Languages.Append("Markdown"))
            {
                resolver.Add(name, Load(name, key, resolver));
            }

            Loaded[key] = resolver;
        }

        return resolver;
    }

    private static IHighlightingDefinition Load(string name, ThemeVariant theme, IHighlightingDefinitionReferenceResolver resolver)
    {
        using var stream = typeof(CodeHighlighting).Assembly.GetManifestResourceStream($"Highlighting.{name}.xshd")
            ?? throw new InvalidOperationException($"The syntax definition '{name}' is not embedded.");
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var definition = HighlightingLoader.LoadXshd(reader);
        var mono = Avalonia.Application.Current?.TryGetResource("MonoFontFamily", theme, out var font) == true ? font as FontFamily : null;
        foreach (var color in definition.Elements.OfType<XshdColor>())
        {
            if (Tokens.TryGetValue(color.Name, out var token) && Resolve(token, theme) is { } value)
            {
                color.Foreground = new SimpleHighlightingBrush(value);
            }

            if (mono is not null && Monospaced.Contains(color.Name))
            {
                color.FontFamily = mono;
            }
        }

        return HighlightingLoader.Load(definition, resolver);
    }

    private static Color? Resolve(string token, ThemeVariant theme) =>
        Avalonia.Application.Current?.TryGetResource(token, theme, out var value) == true && value is Color color ? color : null;

    /// <summary>
    /// Finds the definitions of one theme by name, for the rule sets the Markdown fences borrow.
    /// </summary>
    private sealed class Resolver : IHighlightingDefinitionReferenceResolver
    {
        private readonly Dictionary<string, IHighlightingDefinition> _definitions = new(StringComparer.Ordinal);

        public void Add(string name, IHighlightingDefinition definition) => _definitions[name] = definition;

        public IHighlightingDefinition Get(string name) => _definitions[name];

        public IHighlightingDefinition? GetDefinition(string name) => _definitions.GetValueOrDefault(name);
    }
}
