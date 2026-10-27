using System.IO.Compression;

namespace YourLocalLib.Core;

public static class PngUtil
{
    public static byte[] FromModules(bool[,] dark, int scale, int border)
    {
        var modules = dark.GetLength(0);
        var size = (modules + border * 2) * scale;
        var raw = new byte[size * (size * 4 + 1)];
        var o = 0;
        for (var y = 0; y < size; y++)
        {
            raw[o++] = 0;
            var my = y / scale - border;
            for (var x = 0; x < size; x++)
            {
                var mx = x / scale - border;
                var on = mx >= 0 && my >= 0 && mx < modules && my < modules && dark[my, mx];
                raw[o++] = on ? (byte)30 : (byte)251;
                raw[o++] = on ? (byte)26 : (byte)248;
                raw[o++] = on ? (byte)22 : (byte)243;
                raw[o++] = 255;
            }
        }
        return Encode(size, size, raw);
    }

    private static byte[] Encode(int width, int height, byte[] raw)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        WriteChunk(output, "IHDR", Writer =>
        {
            Writer.Write(Int(width));
            Writer.Write(Int(height));
            Writer.Write(new byte[] { 8, 6, 0, 0, 0 });
        });
        using (var compressed = new MemoryStream())
        {
            using (var deflate = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
                deflate.Write(raw);
            WriteChunk(output, "IDAT", w => w.Write(compressed.ToArray()));
        }
        WriteChunk(output, "IEND", _ => { });
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, Action<Stream> write)
    {
        using var data = new MemoryStream();
        write(data);
        var bytes = data.ToArray();
        output.Write(Int(bytes.Length));
        var tag = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(tag);
        output.Write(bytes);
        var crc = Crc(tag, bytes);
        output.Write(Int((int)crc));
    }

    private static byte[] Int(int value) => new[]
    {
        (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value
    };

    private static uint Crc(byte[] tag, byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        void Feed(byte b)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (crc & 1) == 1 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        foreach (var b in tag) Feed(b);
        foreach (var b in data) Feed(b);
        return crc ^ 0xFFFFFFFFu;
    }
}
