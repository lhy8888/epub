using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;

namespace QuietRead.Core;

// Interface text only. EPUB content, metadata and user notes never pass through
// this catalog. Language is explicit, so background work does not change culture.
public static class InterfaceText
{
    private static readonly IReadOnlyDictionary<string, string> English = Load();
    public static IReadOnlyDictionary<string, string> Translations => English;
    public static string NormalizeLanguage(string? language) => language == "en" ? "en" : "zh-CN";
    public static CultureInfo Culture(string? language) => CultureInfo.GetCultureInfo(NormalizeLanguage(language) == "en" ? "en-US" : "zh-CN");

    public static string Get(string? language, string source, params object?[] arguments)
    {
        string text = NormalizeLanguage(language) == "en" && English.TryGetValue(source, out string? translation) ? translation : source;
        return arguments.Length == 0 ? text : string.Format(Culture(language), text, arguments);
    }

    public static string Error(string? language, string message)
    {
        if (NormalizeLanguage(language) != "en") return message;
        if (English.TryGetValue(message, out string? text)) return text;
        foreach (string prefix in new[] { "数据包含重复属性：", "书籍缺少资源：" })
            if (message.StartsWith(prefix, StringComparison.Ordinal)) return Get(language, prefix + "{0}", message[prefix.Length..]);
        if (message.StartsWith("请选择 ", StringComparison.Ordinal) && message.EndsWith(" 文件。", StringComparison.Ordinal))
            return Get(language, "请选择 {0} 文件。", message[4..^4]);
        // OS/runtime errors retain their diagnostic detail and system language.
        return message;
    }

    private static IReadOnlyDictionary<string, string> Load()
    {
        using Stream stream = typeof(InterfaceText).Assembly.GetManifestResourceStream("QuietRead.Core.Interface.en.json") ??
            throw new InvalidOperationException("The embedded interface text catalog is missing.");
        return new ReadOnlyDictionary<string, string>(JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!);
    }
}
