using System.Collections.ObjectModel;
using System.Text;
using System.Xml.Linq;

namespace QuietRead.Core;

internal sealed class ContentParser(string source, Func<string, bool> isChapter, CancellationToken token)
{
    private readonly List<BookBlock> _blocks = [];
    private readonly Dictionary<string, int> _anchors = new(StringComparer.Ordinal);
    private readonly List<BookInline> _pending = [];
    private BlockKind _kind = BlockKind.Paragraph;
    private int _level, _characters, _inlineCount, _visits, _pendingCharacters;

    public ParsedChapter Parse(XDocument document)
    {
        XElement? body = document.Descendants().FirstOrDefault(x => Name(x) == "body") ?? document.Root;
        if (body == null) throw new EpubException("章节没有正文。");
        bool rtl = string.Equals((string?)body.Attribute("dir") ?? (string?)document.Root?.Attribute("dir"), "rtl", StringComparison.OrdinalIgnoreCase);
        Mark(body);
        if (Name(body) == "svg") Walk(body, TextStyle.None, null);
        else foreach (XNode child in body.Nodes()) Walk(child, TextStyle.None, null);
        Flush();
        if (_blocks.Count == 0)
            Add(new BookBlock(BlockKind.Paragraph, [new BookInline("此章节没有可显示的文字或图片。")]));
        // Empty anchors at the end of a document still resolve to the last visible block.
        foreach (string key in _anchors.Keys.ToArray()) _anchors[key] = Math.Min(_anchors[key], _blocks.Count - 1);
        return new ParsedChapter(_blocks.AsReadOnly(), new ReadOnlyDictionary<string, int>(_anchors), rtl, _characters);
    }

    private void Walk(XNode node, TextStyle style, LocalLink? link)
    {
        if ((++_visits & 127) == 0) token.ThrowIfCancellationRequested();
        if (node is XText text)
        {
            Append(_kind == BlockKind.Code ? text.Value : Collapse(text.Value), style, link);
            return;
        }
        if (node is not XElement element) return;
        string name = Name(element);
        if (name is "head" or "script" or "style" or "iframe" or "object" or "embed" or "form" or
            "input" or "button" or "textarea" or "select" or "audio" or "video" or "source" or "noscript") return;
        if (name is "img" or "image") { Image(element); return; }
        if (name == "svg")
        {
            XElement? raster = element.Descendants().FirstOrDefault(x => Name(x) == "image");
            if (raster != null) Image(raster);
            else
            {
                Flush(); Mark(element);
                // Do not concatenate descendant .Value strings: nested SVG text can
                // multiply a small input into a very large retained fallback label.
                Add(new BookBlock(BlockKind.Image, [], Alt: "此 SVG 图形暂不支持显示。"));
            }
            return;
        }
        if (name == "hr") { Flush(); Mark(element); Add(new BookBlock(BlockKind.Rule, [])); return; }
        if (name == "br") { Mark(element); Append("\n", style, link); return; }

        bool block = name is "p" or "div" or "section" or "article" or "header" or "footer" or "main" or
            "aside" or "blockquote" or "pre" or "li" or "ul" or "ol" or "table" or "tr" or "figure" or
            "figcaption" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "dl" or "dt" or "dd";
        BlockKind oldKind = _kind;
        int oldLevel = _level;
        if (block)
        {
            Flush();
            _kind = name switch
            {
                "blockquote" => BlockKind.Quote,
                "pre" => BlockKind.Code,
                "li" => BlockKind.ListItem,
                "tr" => BlockKind.TableRow,
                "h1" or "h2" or "h3" or "h4" or "h5" or "h6" => BlockKind.Heading,
                _ => oldKind is BlockKind.Quote or BlockKind.ListItem ? oldKind : BlockKind.Paragraph
            };
            _level = _kind == BlockKind.Heading ? name[1] - '0' : Math.Min(6, element.Ancestors().Count(x => Name(x) == "li"));
        }
        Mark(element);
        style |= name switch
        {
            "strong" or "b" or "th" => TextStyle.Bold,
            "em" or "i" => TextStyle.Italic,
            "code" or "kbd" or "samp" or "pre" => TextStyle.Code,
            "sup" => TextStyle.Superscript,
            "sub" => TextStyle.Subscript,
            _ => TextStyle.None
        };
        if (name == "a")
        {
            LocalLink? candidate = ArchivePath.Resolve(source, (string?)element.Attribute("href"));
            link = candidate != null && isChapter(candidate.Path) ? candidate : null;
        }
        if (name is "td" or "th" && _pending.Count != 0) Append("  |  ", TextStyle.None, null);
        foreach (XNode child in element.Nodes()) Walk(child, style, link);
        if (block) { Flush(); _kind = oldKind; _level = oldLevel; }
    }

    private void Image(XElement element)
    {
        Flush(); Mark(element);
        string? href = (string?)element.Attribute("src") ?? element.Attributes().FirstOrDefault(x => x.Name.LocalName == "href")?.Value;
        LocalLink? target = ArchivePath.Resolve(source, href);
        string alt = (string?)element.Attribute("alt") ?? (string?)element.Attribute("title") ?? "书内图片";
        if (alt.Length > 500) alt = alt[..500];
        Add(new BookBlock(BlockKind.Image, [], ImagePath: target?.Path,
            Alt: target == null ? "外部或不安全的图片已跳过。" : alt));
    }

    private void Mark(XElement element)
    {
        string? id = (string?)element.Attribute("id") ?? (string?)element.Attribute(XNamespace.Xml + "id");
        if (string.IsNullOrEmpty(id) && Name(element) == "a") id = (string?)element.Attribute("name");
        if (!string.IsNullOrEmpty(id) && id.Length <= 1024) _anchors.TryAdd(id, _blocks.Count);
    }

    private void Append(string text, TextStyle style, LocalLink? link)
    {
        if (text.Length == 0) return;
        _characters += text.Length;
        if (_characters > ReaderLimits.ChapterCharacters || ++_inlineCount > ReaderLimits.ChapterInlines)
            throw new EpubException("章节文字量或排版元素过多，已停止加载。");
        int at = 0;
        while (at < text.Length)
        {
            if (_pendingCharacters >= ReaderLimits.BlockCharacters || _pending.Count >= ReaderLimits.BlockInlines) Flush();
            int length = Math.Min(text.Length - at, ReaderLimits.BlockCharacters - _pendingCharacters);
            if (at + length < text.Length && length > 0 && char.IsHighSurrogate(text[at + length - 1])) length--;
            if (length == 0) { Flush(); continue; }
            string part = text.Substring(at, length);
            if (_pending.Count > 0 && _pending[^1].Style == style && _pending[^1].Link == link && _pending[^1].Text.Length + length < 1024)
                _pending[^1] = _pending[^1] with { Text = _pending[^1].Text + part };
            else _pending.Add(new BookInline(part, style, link));
            _pendingCharacters += length; at += length;
        }
    }

    private void Flush()
    {
        if (_pending.Count == 0) return;
        if (_kind != BlockKind.Code)
        {
            _pending[0] = _pending[0] with { Text = _pending[0].Text.TrimStart() };
            _pending[^1] = _pending[^1] with { Text = _pending[^1].Text.TrimEnd() };
        }
        if (_pending.Any(x => !string.IsNullOrWhiteSpace(x.Text)))
        {
            if (_kind == BlockKind.ListItem) _pending.Insert(0, new BookInline("•  "));
            Add(new BookBlock(_kind, _pending.ToArray(), _level));
        }
        _pending.Clear(); _pendingCharacters = 0;
    }

    private void Add(BookBlock block)
    {
        if (_blocks.Count >= ReaderLimits.ChapterBlocks) throw new EpubException("章节段落数量过多，已停止加载。");
        _blocks.Add(block);
    }

    private static string Name(XElement element) => element.Name.LocalName.ToLowerInvariant();
    private string Collapse(string value)
    {
        var result = new StringBuilder(Math.Min(value.Length, ReaderLimits.BlockCharacters));
        bool space = false;
        int scanned = 0;
        foreach (char c in value)
        {
            if ((++scanned & 4095) == 0) token.ThrowIfCancellationRequested();
            if (char.IsWhiteSpace(c) && c != '\u00a0')
            {
                if (!space) { result.Append(' '); }
                space = true;
            }
            else { result.Append(c); space = false; }
            if (result.Length > ReaderLimits.ChapterCharacters - _characters)
                throw new EpubException("章节文字量过多，已停止加载。");
        }
        return result.ToString();
    }
}
