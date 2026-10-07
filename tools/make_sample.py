"""Create the original bilingual sample EPUB. Requires only Python's standard library."""
from pathlib import Path
from zipfile import ZipFile, ZIP_DEFLATED, ZIP_STORED
from html import escape
import struct
import sys
import zlib

chapters = [
    ("欢迎使用静读", """<h1 id="welcome">欢迎使用静读</h1>
<p>一本书，一段安静的时间。QuietRead 用简洁的 Windows 原生界面，让文字回到阅读的中心。</p>
<p>点击右上角的 <strong>Aa</strong>，可以调整字号、行距和阅读宽度。纸白适合明亮的房间，暖色更柔和，夜间模式适合低光环境。</p>
<blockquote><p>让阅读适合你的节奏。</p></blockquote>
<h2 id="navigation">从目录开始</h2><p>左侧目录可以直接跳转。正文里的内链也能带你去书内的相关位置。例如，<a href="c1.xhtml#navigation">查看翻页与查找说明</a>。</p>
<p>阅读位置自动保存在本机。下次从「最近」打开同一个文件，便可继续阅读。</p>"""),
    ("轻松翻页与查找", """<h1 id="navigation">轻松翻页与查找</h1>
<p>用滚轮滚动，或点击底部「下一页」阅读下一屏。在长章节段尾，点击翻页会继续下一段或下一章。</p>
<ul><li>「打开」：选择本地 EPUB。</li><li>「查找」：输入文字，再点击查找按钮。</li><li>「＋书签」：保存当前位置。</li><li>「全屏」：放大阅读区域，点击「退出全屏」恢复。</li></ul>
<p>试着在左侧「查找」输入「阅读」，再点击「查找」。搜索结果可以点击，匹配文字会显示背景高亮。</p>
<p>点击目录可以切换章节。<a href="c0.xhtml#welcome">返回欢迎页</a>。</p>
<h2>基本表格与代码</h2><table><tr><th>操作</th><th>位置</th></tr><tr><td>调整外观</td><td>右上角 Aa</td></tr><tr><td>继续阅读</td><td>最近阅读列表</td></tr></table><pre>QuietRead.exe "C:\\Books\\example.epub"</pre>"""),
    ("A quiet reading moment", """<h1>A quiet reading moment</h1>
<p>The window is open. The desk is clear. A book waits beside a warm cup of tea.</p>
<p>There is no rush. You can read a paragraph, look away, and return when you are ready. The place will still be there.</p>
<p><em>Choose a comfortable size. Let the lines breathe. Read at your own pace.</em></p>
<p>This short chapter also lets you try the Georgia font for English text. Select <strong>Aa → Georgia</strong> and compare the reading experience.</p>"""),
    ("长章节体验", "<h1>长章节体验</h1><p>这章有许多简短段落，用于体验分段显示。滚动到底后，点击「下一页」继续。</p>" + "".join(
        f"<p>第 {i} 段。书页上的文字安静地排列着。窗外的光慢慢变化，阅读的节奏由你决定。每一段都可以停留，也可以继续。当前位置会自动保存。</p>" for i in range(1, 321))),
]

def main():
    root = Path(__file__).resolve().parents[1]
    destination = Path(sys.argv[1]) if len(sys.argv) > 1 else root / "samples" / "QuietRead-Guide.epub"
    destination.parent.mkdir(parents=True, exist_ok=True)
    manifest = "".join(f'<item id="c{i}" href="c{i}.xhtml" media-type="application/xhtml+xml"/>' for i in range(len(chapters)))
    spine = "".join(f'<itemref idref="c{i}"/>' for i in range(len(chapters)))
    nav = "".join(f'<li><a href="c{i}.xhtml">{escape(title)}</a></li>' for i, (title, _) in enumerate(chapters))
    with ZipFile(destination, "w") as book:
        book.writestr("mimetype", "application/epub+zip", compress_type=ZIP_STORED)
        book.writestr("META-INF/container.xml", '<container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0"><rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>', compress_type=ZIP_DEFLATED)
        book.writestr("OEBPS/content.opf", f'''<package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="uid"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>静读 · 阅读指南</dc:title><dc:creator>QuietRead</dc:creator><dc:language>zh-CN</dc:language><dc:identifier id="uid">urn:uuid:173c4ff5-12d8-48a4-b930-bc68db4e0c49</dc:identifier><meta property="dcterms:modified">2026-10-07T10:00:00Z</meta></metadata><manifest>{manifest}<item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/><item id="cover" href="cover.png" media-type="image/png" properties="cover-image"/></manifest><spine>{spine}</spine></package>''', compress_type=ZIP_DEFLATED)
        book.writestr("OEBPS/nav.xhtml", f'<html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>目录</title></head><body><nav epub:type="toc"><ol>{nav}</ol></nav></body></html>', compress_type=ZIP_DEFLATED)
        for i, (title, body) in enumerate(chapters):
            if i == 0:
                body = '<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" viewBox="0 0 640 280"><image xlink:href="cover.png" width="640" height="280"/></svg>' + body
            book.writestr(f"OEBPS/c{i}.xhtml", f'<html xmlns="http://www.w3.org/1999/xhtml" lang="zh-CN"><head><title>{escape(title)}</title></head><body>{body}</body></html>', compress_type=ZIP_DEFLATED)
        width, height = 640, 280
        rows = bytearray()
        for y in range(height):
            rows.append(0)
            for x in range(width):
                color = (232, 240, 232)
                if 235 <= x < 405 and 44 <= y < 236:
                    color = (40, 102, 75)
                if 252 <= x < 388 and 63 <= y < 216:
                    color = (253, 252, 249)
                if 316 <= x < 323 and 63 <= y < 218:
                    color = (40, 102, 75)
                if (270 <= x < 302 or 340 <= x < 371) and any(t <= y < t + 4 for t in (93, 119, 145, 171)):
                    color = (139, 166, 142)
                rows.extend(color)
        def chunk(kind, data):
            return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)
        png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(bytes(rows), 9)) + chunk(b"IEND", b"")
        book.writestr("OEBPS/cover.png", png, compress_type=ZIP_DEFLATED)
    print(destination)

if __name__ == "__main__":
    main()
