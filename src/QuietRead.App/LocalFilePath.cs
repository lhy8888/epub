using System.IO;
using QuietRead.Core;

namespace QuietRead;

internal static class LocalFilePath
{
    internal static string Check(string path, string extension, bool mustExist = true)
    {
        path = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase)) throw new EpubException("请选择 " + extension + " 文件。");
        string root = Path.GetPathRoot(path) ?? "";
        if (root.StartsWith("\\\\", StringComparison.Ordinal) || new DriveInfo(root).DriveType == DriveType.Network) throw new EpubException("请选择本地磁盘中的文件。");
        string component = root;
        string[] parts = path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            component = Path.Combine(component, parts[i]);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(component); }
            catch (FileNotFoundException) when (!mustExist && i == parts.Length - 1) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new EpubException("暂不支持符号链接、目录联接或云占位文件。");
        }
        if (mustExist && !File.Exists(path)) throw new EpubException("找不到所选文件。");
        return path;
    }
}
