using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace QuietRead.Core;

internal static partial class SafeXml
{
    [GeneratedRegex(@"&([A-Za-z][A-Za-z0-9]{1,30});", RegexOptions.CultureInvariant, 200)]
    private static partial Regex NamedEntity();

    public static XDocument Parse(byte[] data, CancellationToken token)
    {
        if (data.Length > ReaderLimits.XmlBytes) throw new EpubException("书籍中的单个文档过大（上限 8 MB）。");
        string text;
        try
        {
            using var reader = new StreamReader(new MemoryStream(data, false), new UTF8Encoding(false, true), true);
            text = reader.ReadToEnd();
            // EPUB 2 often uses HTML named entities. Translate only known names to numeric entities.
            // Never turn encoded '<', '&', etc. into markup.
            text = NamedEntity().Replace(text, match =>
            {
                string name = match.Groups[1].Value;
                if (name is "amp" or "lt" or "gt" or "quot" or "apos") return match.Value;
                string decoded = WebUtility.HtmlDecode(match.Value);
                if (decoded == match.Value) return match.Value;
                return string.Concat(decoded.EnumerateRunes().Select(x => "&#" + x.Value + ";"));
            });
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                MaxCharactersInDocument = ReaderLimits.XmlBytes,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true
            };
            int nodes = 0;
            // The first bounded pass prevents deep trees/too many nodes before XDocument is built.
            using (XmlReader guard = XmlReader.Create(new StringReader(text), settings))
                while (guard.Read())
                {
                    if ((++nodes & 127) == 0) token.ThrowIfCancellationRequested();
                    if (nodes > ReaderLimits.XmlNodes || guard.Depth > ReaderLimits.XmlDepth)
                        throw new EpubException("书籍文档结构过于复杂，已停止加载。");
                }
            token.ThrowIfCancellationRequested();
            using XmlReader xml = XmlReader.Create(new StringReader(text), settings);
            return XDocument.Load(xml, LoadOptions.PreserveWhitespace);
        }
        catch (Exception exception) when (exception is XmlException or DecoderFallbackException or RegexMatchTimeoutException)
        { throw new EpubException("书籍 XML/XHTML 无效或包含无法解析的实体。", exception); }
    }
}
