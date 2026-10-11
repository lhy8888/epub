using System.Globalization;
using System.Text;
using System.Text.Json;
using QuietRead.Core;

internal static class LanguageTests
{
    internal static void Register(List<(string Name, Action Run)> tests, string root)
    {
        tests.Add(("Interface language persists globally and older or invalid settings keep Chinese", () =>
        {
            var store = new StateStore(Path.Combine(root, "language"));
            var state = new AppState { InterfaceLanguage = "en" };
            store.Save(StateStore.Snapshot(state));
            Check.Equal("en", store.Load().InterfaceLanguage);
            AppState older = JsonSerializer.Deserialize<AppState>("{\"Schema\":1}")!; older.Normalize();
            Check.Equal("zh-CN", older.InterfaceLanguage);
            foreach (string? unsupported in new string?[] { null, "", "en-US", "de", new string('x', 10000) })
            { state.InterfaceLanguage = unsupported!; Check.Equal("zh-CN", JsonSerializer.Deserialize<AppState>(StateStore.Snapshot(state))!.InterfaceLanguage); }
        }
        ));
        tests.Add(("Every English message preserves its format arguments and supports both languages", () =>
        {
            Check.True(InterfaceText.Translations.Count > 300);
            foreach ((string source, string english) in InterfaceText.Translations)
            {
                int count = CompositeFormat.Parse(source).MinimumArgumentCount;
                Check.Equal(count, CompositeFormat.Parse(english).MinimumArgumentCount);
                object?[] arguments = Enumerable.Range(0, count).Select(i => (object?)($"ARG{i}")).ToArray();
                string translated = InterfaceText.Get("en", source, arguments);
                Check.True(translated.Length > 0);
                for (int i = 0; i < count; i++) Check.True(translated.Contains($"ARG{i}", StringComparison.Ordinal));
                Check.Equal(string.Format(InterfaceText.Culture("zh-CN"), source, arguments), InterfaceText.Get("zh-CN", source, arguments));
            }
        }
        ));
        tests.Add(("Concurrent interface formatting keeps book text and thread culture untouched", () =>
        {
            string culture = CultureInfo.CurrentCulture.Name;
            Parallel.For(0, 100, i =>
            {
                Check.Equal(i % 2 == 0 ? "Open" : "打开", InterfaceText.Get(i % 2 == 0 ? "en" : "zh-CN", "打开"));
                Check.Equal("• 打开: 2 note conflicts", InterfaceText.Get("en", "• {0}：笔记冲突 {1}{2}{3}{4}", "打开", 2, "", "", ""));
            });
            Check.Equal(culture, CultureInfo.CurrentCulture.Name);
            Check.Equal("用户笔记 user note", InterfaceText.Get("en", "用户笔记 user note"));
        }
        ));
        tests.Add(("Localized validation errors retain file paths, property names and runtime details", () =>
        {
            Check.Equal("Choose a .json file.", InterfaceText.Error("en", "请选择 .json 文件。"));
            Check.Equal("Duplicate data property: Annotations", InterfaceText.Error("en", "数据包含重复属性：Annotations"));
            Check.Equal("Missing book resource: OEBPS/章节.xhtml", InterfaceText.Error("en", "书籍缺少资源：OEBPS/章节.xhtml"));
            Check.Equal("所选文件内容不同，不能关联到这本书。", InterfaceText.Error("zh-CN", "所选文件内容不同，不能关联到这本书。"));
            Check.Equal("OS diagnostic {detail}", InterfaceText.Error("en", "OS diagnostic {detail}"));
        }
        ));
        tests.Add(("Markdown headings follow interface language while book data and original notes stay unchanged", () =>
        {
            var store = new LibraryStore(Path.Combine(root, "language-markdown"));
            var data = store.OpenBook(1.ToString("X64"), 2.ToString("X64"), "book.epub", "打开", "中文作者");
            var chapter = new ParsedChapter([new BookBlock(BlockKind.Paragraph, [new BookInline("中文原文 English text")])], new Dictionary<string, int>(), false, 19);
            var range = TextRangeLocation.Capture(chapter, 0, 0, 0, 4);
            data.Annotations.Add(new BookAnnotation(Guid.NewGuid(), 0, "chapter.xhtml", range, "Yellow", "用户笔记 user note", DateTime.UnixEpoch, DateTime.UnixEpoch));
            data.History.Bookmarks.Add(new Bookmark { Label = "书库" });
            store.SaveBook(BookDataRules.Snapshot(data));
            byte[] before = BookDataRules.Snapshot(store.LoadBook(data.BookId));
            string english = store.ExportMarkdown(data.BookId, "en"), chinese = store.ExportMarkdown(data.BookId);
            Check.True(english.StartsWith("# 打开", StringComparison.Ordinal) && english.Contains("Author: 中文作者", StringComparison.Ordinal));
            Check.True(english.Contains("## Chapter 1", StringComparison.Ordinal) && english.Contains("- Bookmark: 书库", StringComparison.Ordinal));
            Check.True(english.Contains("用户笔记 user note", StringComparison.Ordinal) && chinese.Contains("## 第 1 章", StringComparison.Ordinal));
            Check.True(before.SequenceEqual(BookDataRules.Snapshot(store.LoadBook(data.BookId))));
        }
        ));
    }
}
