using System.Buffers.Binary;

namespace QuietRead.Core;

/// <summary>Preflight the directory before ZipArchive allocates its entry table.</summary>
internal static class ZipGuard
{
    private static ushort U16(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    private static ulong U64(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..]);

    public static void Validate(FileStream stream, CancellationToken token)
    {
        long length = stream.Length;
        if (length < 22 || length > ReaderLimits.ArchiveBytes)
            throw new EpubException("EPUB 文件无效或超过 512 MB 的大小限制。");
        byte[] tail = new byte[(int)Math.Min(length, 65_557)];
        stream.Position = length - tail.Length;
        stream.ReadExactly(tail);
        int end = -1;
        for (int i = tail.Length - 22; i >= 0; i--)
            if (U32(tail, i) == 0x06054b50 && i + 22 + U16(tail, i + 20) == tail.Length)
            { end = i; break; }
        if (end < 0) throw new EpubException("找不到有效的 EPUB ZIP 目录。");
        if (U16(tail, end + 4) != 0 || U16(tail, end + 6) != 0 || U16(tail, end + 8) != U16(tail, end + 10))
            throw new EpubException("不支持分卷 EPUB 文件。");
        ulong count = U16(tail, end + 10);
        ulong size = U32(tail, end + 12);
        ulong offset = U32(tail, end + 16);
        long eocdPosition = length - tail.Length + end;
        if (count == ushort.MaxValue || size == uint.MaxValue || offset == uint.MaxValue)
        {
            if (eocdPosition < 20) throw new EpubException("ZIP64 目录无效。");
            byte[] locator = new byte[20];
            stream.Position = eocdPosition - 20;
            stream.ReadExactly(locator);
            if (U32(locator, 0) != 0x07064b50 || U32(locator, 4) != 0 || U32(locator, 16) != 1)
                throw new EpubException("ZIP64 目录无效。");
            ulong location = U64(locator, 8);
            if (location > (ulong)(length - 56)) throw new EpubException("ZIP64 目录越界。");
            stream.Position = (long)location;
            byte[] record = new byte[56];
            stream.ReadExactly(record);
            if (U32(record, 0) != 0x06064b50 || U64(record, 4) < 44 ||
                U32(record, 16) != 0 || U32(record, 20) != 0 || U64(record, 24) != U64(record, 32))
                throw new EpubException("ZIP64 目录无效。");
            count = U64(record, 32); size = U64(record, 40); offset = U64(record, 48);
        }
        if (count == 0 || count > ReaderLimits.Entries || size > 32UL * 1024 * 1024 ||
            offset > (ulong)length || size > (ulong)length - offset || offset + size > (ulong)eocdPosition)
            throw new EpubException("书籍资源数量过多或 ZIP 目录无效。");

        stream.Position = (long)offset;
        long directoryEnd = (long)(offset + size);
        byte[] header = new byte[46];
        for (ulong i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (stream.Position > directoryEnd - 46) throw new EpubException("ZIP 目录被截断。");
            stream.ReadExactly(header);
            if (U32(header, 0) != 0x02014b50) throw new EpubException("ZIP 目录格式错误。");
            ushort flags = U16(header, 8), method = U16(header, 10), version = U16(header, 6);
            if ((flags & 0x0041) != 0 || method is not (0 or 8) || version is not (10 or 20 or 45))
                throw new EpubException("不支持加密 EPUB 或这种 ZIP 压缩方式。");
            if (U16(header, 34) != 0) throw new EpubException("不支持分卷 EPUB。");
            int nameLength = U16(header, 28);
            long skip = nameLength + U16(header, 30) + U16(header, 32);
            if (nameLength == 0 || nameLength > 4096 || skip > directoryEnd - stream.Position)
                throw new EpubException("ZIP 资源名称或目录无效。");
            stream.Position += skip;
        }
        if (stream.Position != directoryEnd) throw new EpubException("ZIP 目录包含异常数据。");
        stream.Position = 0;
    }
}
