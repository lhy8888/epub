using System.Text.Json;

namespace QuietRead.Core;

// Reject large collections before the typed serializer constructs their objects.
internal static class DataJsonBudget
{
    internal static void Check(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 20 });
        var arrays = new Dictionary<int, (int Maximum, int Count, int StringMaximum)>();
        int arrayMaximum = 0, stringMaximum = 0, elementMaximum = 0;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
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
