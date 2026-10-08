using AIDatasetStudio.Domain.Enums;
using Avalonia.Data.Converters;
using FluentIcons.Common;

namespace AIDatasetStudio.Desktop.Converters;

/// <summary>
/// One place that decides which icon stands for which concept.
/// </summary>
public static class IconConverters
{
    public static readonly IValueConverter DatasetType = new FuncValueConverter<DatasetType, Symbol>(type => type switch
    {
        Domain.Enums.DatasetType.Instruction => Symbol.TextBulletListSquare,
        Domain.Enums.DatasetType.Chat => Symbol.ChatMultiple,
        Domain.Enums.DatasetType.Preference => Symbol.ThumbLike,
        _ => Symbol.DocumentText,
    });

    public static readonly IValueConverter ChatRole = new FuncValueConverter<ChatRole, Symbol>(role => role switch
    {
        Domain.Enums.ChatRole.User => Symbol.Person,
        Domain.Enums.ChatRole.Assistant => Symbol.BotSparkle,
        _ => Symbol.Settings,
    });
}
