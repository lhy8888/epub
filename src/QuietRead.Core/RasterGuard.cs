using System.Buffers.Binary;

namespace QuietRead.Core;

public static class RasterGuard
{
    public static RasterInfo Inspect(ReadOnlySpan<byte> data)
    {
        int width = 0, height = 0;
        string format;
        if (data.Length >= 33 && data[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
            data.Slice(12, 4).SequenceEqual("IHDR"u8) && BinaryPrimitives.ReadUInt32BigEndian(data[8..]) == 13)
        {
            uint w = BinaryPrimitives.ReadUInt32BigEndian(data[16..]), h = BinaryPrimitives.ReadUInt32BigEndian(data[20..]);
            if (w > int.MaxValue || h > int.MaxValue) throw new EpubException("图片尺寸异常。");
            width = (int)w; height = (int)h; format = "png";
        }
        else if (data.Length >= 13 && (data[..6].SequenceEqual("GIF87a"u8) || data[..6].SequenceEqual("GIF89a"u8)))
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
            height = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]); format = "gif";
            ValidateGif(data, width, height);
        }
        else if (data.Length >= 54 && data[..2].SequenceEqual("BM"u8))
        {
            int dib = BinaryPrimitives.ReadInt32LittleEndian(data[14..]);
            if (dib < 40 || dib > data.Length - 14) throw new EpubException("BMP 图片头无效。");
            width = BinaryPrimitives.ReadInt32LittleEndian(data[18..]);
            int h = BinaryPrimitives.ReadInt32LittleEndian(data[22..]);
            if (h == int.MinValue) throw new EpubException("图片尺寸异常。");
            height = Math.Abs(h); format = "bmp";
        }
        else if (data.Length >= 4 && data[0] == 0xff && data[1] == 0xd8)
        {
            format = "jpeg";
            int at = 2;
            while (at < data.Length - 3)
            {
                if (data[at++] != 0xff) throw new EpubException("JPEG 图片头无效。");
                while (at < data.Length && data[at] == 0xff) at++;
                if (at >= data.Length) break;
                byte marker = data[at++];
                if (marker is 0xd9 or 0xda) break;
                if (marker is 0x01 or >= 0xd0 and <= 0xd8) continue;
                if (at + 2 > data.Length) break;
                int size = BinaryPrimitives.ReadUInt16BigEndian(data[at..]);
                if (size < 2 || size > data.Length - at) throw new EpubException("JPEG 图片头无效。");
                if (marker is >= 0xc0 and <= 0xcf && marker is not (0xc4 or 0xc8 or 0xcc))
                {
                    if (size < 8) throw new EpubException("JPEG 图片头无效。");
                    height = BinaryPrimitives.ReadUInt16BigEndian(data[(at + 3)..]);
                    width = BinaryPrimitives.ReadUInt16BigEndian(data[(at + 5)..]);
                    break;
                }
                at += size;
            }
        }
        else throw new EpubException("图片格式不受支持。");
        if (width <= 0 || height <= 0 || width > 16_384 || height > 16_384 || (long)width * height > 20_000_000)
            throw new EpubException("图片尺寸过大或无效（最多 2,000 万像素）。");
        return new RasterInfo(width, height, format);
    }

    private static void ValidateGif(ReadOnlySpan<byte> data, int width, int height)
    {
        int at = 13;
        if ((data[10] & 0x80) != 0) at += 3 * (1 << ((data[10] & 7) + 1));
        bool hasFrame = false;
        while (at < data.Length)
        {
            byte marker = data[at++];
            if (marker == 0x3b && hasFrame) return;
            if (marker == 0x21)
            {
                if (at >= data.Length) break;
                at++; // extension label; only skip its bounded sub-blocks
            }
            else if (marker == 0x2c)
            {
                if (at > data.Length - 9) break;
                int left = BinaryPrimitives.ReadUInt16LittleEndian(data[at..]);
                int top = BinaryPrimitives.ReadUInt16LittleEndian(data[(at + 2)..]);
                int frameWidth = BinaryPrimitives.ReadUInt16LittleEndian(data[(at + 4)..]);
                int frameHeight = BinaryPrimitives.ReadUInt16LittleEndian(data[(at + 6)..]);
                if (frameWidth <= 0 || frameHeight <= 0 || left + frameWidth > width || top + frameHeight > height)
                    throw new EpubException("GIF 帧尺寸超出图片画布。");
                int packed = data[at + 8];
                at += 9;
                if ((packed & 0x80) != 0) at += 3 * (1 << ((packed & 7) + 1));
                if (at >= data.Length) break;
                int codeSize = data[at++];
                if (codeSize is < 2 or > 8) throw new EpubException("GIF 图片数据无效。");
                hasFrame = true;
            }
            else break;
            while (true)
            {
                if (at >= data.Length) throw new EpubException("GIF 图片被截断。");
                int size = data[at++];
                if (size == 0) break;
                if (size > data.Length - at) throw new EpubException("GIF 图片被截断。");
                at += size;
            }
        }
        throw new EpubException("GIF 图片结构无效或被截断。");
    }
}
