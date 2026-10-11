using System.Text;
using QuietRead.Core;

internal static class StateBudgetTests
{
    internal static void Register(List<(string Name, Action Run)> tests, string root)
    {
        tests.Add(("Settings reject excess histories before creating their object graph", () =>
        {
            string directory = Path.Combine(root, "state-budget"); Directory.CreateDirectory(directory);
            var warm = new StateStore(directory); warm.Save(StateStore.Snapshot(new AppState())); warm.Load();
            byte[] bytes = Encoding.UTF8.GetBytes("{\"Schema\":2,\"Books\":[" + string.Join(',', Enumerable.Repeat("{}", 200_000)) + "]}");
            string path = Path.Combine(directory, "state.json"); File.WriteAllBytes(path, bytes);
            var store = new StateStore(directory); long before = GC.GetAllocatedBytesForCurrentThread(); AppState state = store.Load();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Check.True(store.LoadFailed); Check.Equal(0, state.Books.Count); Check.True(allocated < 2 * 1024 * 1024, "Settings objects were allocated before enforcing the collection budget.");
            Check.True(File.ReadAllBytes(path).SequenceEqual(bytes));
            ReauditTests.Measurements.Add(new { name = "Reject oversized settings collection", inputBytes = bytes.Length, allocatedBytes = allocated });
        }
        ));
        tests.Add(("Settings reject duplicate known properties and excess bookmarks while preserving the original file", () =>
        {
            string directory = Path.Combine(root, "state-duplicates"); Directory.CreateDirectory(directory); string path = Path.Combine(directory, "state.json");
            foreach (string input in new[]
            {
                "{\"Schema\":2,\"Preferences\":{},\"Preferences\":{\"Theme\":\"Dark\"}}",
                "{\"Schema\":2,\"InterfaceLanguage\":\"en\",\"InterfaceLanguage\":\"zh-CN\"}",
                "{\"Schema\":2,\"Books\":[{\"Bookmarks\":[" + string.Join(',', Enumerable.Repeat("{}", 65)) + "]}]}"
            })
            {
                File.WriteAllText(path, input); var store = new StateStore(directory); store.Load(); Check.True(store.LoadFailed); Check.Equal(input, File.ReadAllText(path));
            }
        }
        ));
        tests.Add(("Bounded settings loading preserves schema migration and normalization", () =>
        {
            string directory = Path.Combine(root, "state-legacy-budget"); Directory.CreateDirectory(directory); string path = Path.Combine(directory, "state.json");
            File.WriteAllText(path, "{\"Schema\":1,\"Preferences\":null,\"Books\":[null],\"InterfaceLanguage\":\"fr-CA\"}");
            var store = new StateStore(directory); AppState state = store.Load(); Check.False(store.LoadFailed); Check.Equal(2, state.Schema); Check.Equal("zh-CN", state.InterfaceLanguage);
            Check.Equal(0, state.Books.Count); Check.Equal(22d, state.Preferences.FontSize);
            File.WriteAllBytes(path, Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("{\"Schema\":1,\"InterfaceLanguage\":\"en\"}")).ToArray());
            var bom = new StateStore(directory); Check.Equal("en", bom.Load().InterfaceLanguage); Check.False(bom.LoadFailed);
        }
        ));
    }
}
