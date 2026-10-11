using System.Text.Json;

namespace QuietRead.Core;

// Reject large collections before the typed serializer constructs their objects.
internal static class DataJsonBudget
{
    // Only typed properties need tracking. Unknown fields are skipped by the
    // serializer; do not allocate attacker-controlled property-name strings.
    private static readonly string[] Properties = ["Schema", "BookId", "History", "Annotations", "Appearance", "Books", "LegacyHistoryMigrated",
        "Entry", "Data", "DataRevision", "Title", "Author", "FilePath", "Tags", "Series", "ReadingStatus", "LastReadUtc", "BookKey", "Locator",
        "ChapterIndex", "BlockIndex", "Fraction", "Bookmarks", "Label", "CreatedUtc", "Id", "ResourcePath", "Range", "Color", "Note", "UpdatedUtc",
        "Start", "End", "Text", "Paragraph", "Offset", "Quote", "Before", "After", "Theme", "Font", "FontSize", "LineSpacing", "TextWidth",
        "SidebarVisible", "ParagraphSpacing", "FirstLineIndent", "Alignment"];

    internal static void Check(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 20 });
        var arrays = new Dictionary<int, (int Maximum, int Count, int StringMaximum)>();
        Span<ulong> properties = stackalloc ulong[21];
        properties.Clear();
        int arrayMaximum = 0, stringMaximum = 0, elementMaximum = 0;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject) properties[reader.CurrentDepth] = 0;
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                for (int i = 0; i < Properties.Length; i++)
                    if (reader.ValueTextEquals(Properties[i]))
                    {
                        ulong bit = 1UL << i;
                        int depth = reader.CurrentDepth - 1;
                        if ((properties[depth] & bit) != 0) throw new InvalidDataException("数据包含重复属性：" + Properties[i]);
                        properties[depth] |= bit; break;
                    }
                arrayMaximum = reader.ValueTextEquals("Books"u8) ? BookDataRules.MaxBooks : reader.ValueTextEquals("Annotations"u8) ? BookDataRules.MaxAnnotations :
                    reader.ValueTextEquals("Bookmarks"u8) ? 64 : reader.ValueTextEquals("Tags"u8) ? 16 : 0;
                stringMaximum = reader.ValueTextEquals("Note"u8) ? 2000 : reader.ValueTextEquals("Text"u8) ? 4096 :
                    reader.ValueTextEquals("Quote"u8) ? 48 : reader.ValueTextEquals("Before"u8) || reader.ValueTextEquals("After"u8) ? 16 :
                    reader.ValueTextEquals("Title"u8) || reader.ValueTextEquals("Author"u8) ? 256 : reader.ValueTextEquals("FilePath"u8) ? 2048 :
                    reader.ValueTextEquals("ResourcePath"u8) ? 1024 : 0;
                if (reader.ValueTextEquals("Series"u8) || reader.ValueTextEquals("Label"u8)) stringMaximum = 100;
                if (reader.ValueTextEquals("BookId"u8) || reader.ValueTextEquals("BookKey"u8)) stringMaximum = 64;
                if (reader.ValueTextEquals("DataRevision"u8) || reader.ValueTextEquals("Id"u8)) stringMaximum = 36;
                if (reader.ValueTextEquals("Color"u8) || reader.ValueTextEquals("Theme"u8) || reader.ValueTextEquals("Font"u8) || reader.ValueTextEquals("Alignment"u8) || reader.ValueTextEquals("ReadingStatus"u8)) stringMaximum = 16;
                elementMaximum = reader.ValueTextEquals("Tags"u8) ? 40 : 0;
                continue;
            }
            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray or JsonTokenType.String or JsonTokenType.Number or JsonTokenType.True or JsonTokenType.False or JsonTokenType.Null &&
                arrays.TryGetValue(reader.CurrentDepth - 1, out var array))
            {
                if (++array.Count > array.Maximum) throw new InvalidDataException("数据集合超过数量限制。");
                arrays[reader.CurrentDepth - 1] = array;
                if (reader.TokenType == JsonTokenType.String && array.StringMaximum > 0 && reader.ValueSpan.Length > array.StringMaximum * 6)
                    throw new InvalidDataException("数据字符串超过长度限制。");
            }
            if (reader.TokenType == JsonTokenType.String && stringMaximum > 0 && reader.ValueSpan.Length > stringMaximum * 6)
                throw new InvalidDataException("数据字符串超过长度限制。");
            if (reader.TokenType == JsonTokenType.StartArray && arrayMaximum > 0) arrays[reader.CurrentDepth] = (arrayMaximum, 0, elementMaximum);
            if (reader.TokenType == JsonTokenType.EndArray) arrays.Remove(reader.CurrentDepth);
            arrayMaximum = 0; stringMaximum = 0; elementMaximum = 0;
        }
    }
}
