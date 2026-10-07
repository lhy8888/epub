namespace QuietRead.Core;

/// <summary>EPUB paths are ZIP names, never operating system paths.</summary>
public static class ArchivePath
{
    public static string ValidateEntry(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 1024 || name[0] == '/' ||
            name.Any(c => c is '\\' or ':' or '\0' || char.IsControl(c)))
            throw new EpubException("书籍包含不安全的资源路径。");
        string value = name.TrimEnd('/');
        if (value.Split('/').Any(x => x is "" or "." or ".."))
            throw new EpubException("书籍包含不安全的资源路径。");
        return name;
    }

    public static LocalLink? Resolve(string source, string? href)
    {
        if (string.IsNullOrWhiteSpace(href) || href.Length > 2048) return null;
        href = href.Trim();
        if (href.Any(c => c is '\\' or '\0' || char.IsControl(c))) return null;
        int hash = href.IndexOf('#');
        string fragment = hash < 0 ? "" : href[(hash + 1)..];
        string path = hash < 0 ? href : href[..hash];
        int question = path.IndexOf('?');
        if (question >= 0) path = path[..question];
        if (!ValidPercent(path) || !ValidPercent(fragment)) return null;
        path = Uri.UnescapeDataString(path);
        fragment = Uri.UnescapeDataString(fragment);
        if (path.StartsWith('/') || path.Any(c => c is '\\' or ':' or '\0' || char.IsControl(c)) ||
            fragment.Any(char.IsControl)) return null;
        if (path.Length == 0) return new LocalLink(source, fragment);
        var segments = source.Split('/').SkipLast(1).ToList();
        foreach (string segment in path.Split('/'))
        {
            if (segment is "" or ".") continue;
            if (segment == "..")
            {
                if (segments.Count == 0) return null;
                segments.RemoveAt(segments.Count - 1);
            }
            else segments.Add(segment);
        }
        string resolved = string.Join('/', segments);
        return resolved.Length == 0 || resolved.Length > 1024 ? null : new LocalLink(resolved, fragment);
    }

    private static bool ValidPercent(string value)
    {
        for (int i = 0; i < value.Length; i++)
            if (value[i] == '%')
            {
                if (i + 2 >= value.Length || !Uri.IsHexDigit(value[i + 1]) || !Uri.IsHexDigit(value[i + 2]))
                    return false;
                i += 2;
            }
        return true;
    }
}
