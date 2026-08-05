using System;
using System.IO;
using System.IO.Compression;

namespace ProjectMQaMcp.Editor
{
    /// <summary>
    /// Writes a PNG without touching Unity.
    ///
    /// <c>Texture2D.EncodeToPNG</c> is a Unity API, so it cannot run on the watcher thread - and the
    /// watcher thread is the only one running while a modal dialog holds the main thread. Without this,
    /// the off-thread capture could read a window's pixels and then have nowhere to put them.
    ///
    /// PNG is a short format when the goal is only "store these pixels": one IHDR, one IDAT holding a
    /// zlib stream of the filtered rows, one IEND. Every row is written with filter byte 0 (None), so
    /// the file is larger than what an optimising encoder produces but is decoded identically by
    /// everything. .NET supplies the deflate; the zlib header and the two checksums are written here.
    /// </summary>
    internal static class ThreadSafePng
    {
        /// <summary>
        /// Encodes a top-down 32bpp BGRA buffer - the layout GetDIBits hands back - and writes it out.
        /// </summary>
        internal static void WriteBgra(string path, byte[] bgra, int width, int height)
        {
            if (bgra == null) throw new ArgumentNullException("bgra");
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException("width");
            if (bgra.Length < width * height * 4)
            {
                throw new ArgumentException("The pixel buffer is smaller than width * height * 4.");
            }

            // Filtered scanlines: one leading filter byte per row, then RGB from the source's BGR.
            var raw = new byte[height * (1 + width * 4)];
            var rawIndex = 0;
            for (var y = 0; y < height; y++)
            {
                raw[rawIndex++] = 0; // filter: None
                var srcIndex = y * width * 4;
                for (var x = 0; x < width; x++)
                {
                    raw[rawIndex++] = bgra[srcIndex + 2]; // R
                    raw[rawIndex++] = bgra[srcIndex + 1]; // G
                    raw[rawIndex++] = bgra[srcIndex];     // B
                    // PrintWindow leaves alpha at 0 on some drivers, which would make the whole image
                    // transparent. The capture is a screenshot, so it is written fully opaque.
                    raw[rawIndex++] = 255;
                    srcIndex += 4;
                }
            }

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                file.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);

                var ihdr = new byte[13];
                WriteBigEndian(ihdr, 0, width);
                WriteBigEndian(ihdr, 4, height);
                ihdr[8] = 8;  // bit depth
                ihdr[9] = 6;  // colour type: truecolour with alpha
                ihdr[10] = 0; // compression: deflate
                ihdr[11] = 0; // filter method
                ihdr[12] = 0; // interlace: none
                WriteChunk(file, "IHDR", ihdr);

                WriteChunk(file, "IDAT", Zlib(raw));
                WriteChunk(file, "IEND", new byte[0]);
            }
        }

        /// <summary>Wraps raw deflate output in the zlib container PNG requires.</summary>
        private static byte[] Zlib(byte[] raw)
        {
            byte[] deflated;
            using (var buffer = new MemoryStream())
            {
                // Leave the stream open so the trailing bytes are flushed before the buffer is read.
                using (var deflate = new DeflateStream(buffer, CompressionMode.Compress, true))
                {
                    deflate.Write(raw, 0, raw.Length);
                }

                deflated = buffer.ToArray();
            }

            var result = new byte[2 + deflated.Length + 4];
            result[0] = 0x78; // zlib: deflate, 32k window
            result[1] = 0x01; // no preset dictionary, fastest compression level
            Buffer.BlockCopy(deflated, 0, result, 2, deflated.Length);
            WriteBigEndian(result, 2 + deflated.Length, unchecked((int)Adler32(raw)));
            return result;
        }

        private static void WriteChunk(Stream stream, string type, byte[] data)
        {
            var length = new byte[4];
            WriteBigEndian(length, 0, data.Length);
            stream.Write(length, 0, 4);

            var typeAndData = new byte[4 + data.Length];
            for (var i = 0; i < 4; i++)
            {
                typeAndData[i] = (byte)type[i];
            }
            Buffer.BlockCopy(data, 0, typeAndData, 4, data.Length);
            stream.Write(typeAndData, 0, typeAndData.Length);

            var crc = new byte[4];
            WriteBigEndian(crc, 0, unchecked((int)Crc32(typeAndData)));
            stream.Write(crc, 0, 4);
        }

        private static void WriteBigEndian(byte[] target, int offset, int value)
        {
            target[offset] = (byte)((value >> 24) & 0xFF);
            target[offset + 1] = (byte)((value >> 16) & 0xFF);
            target[offset + 2] = (byte)((value >> 8) & 0xFF);
            target[offset + 3] = (byte)(value & 0xFF);
        }

        private static uint Adler32(byte[] data)
        {
            uint a = 1, b = 0;
            foreach (var value in data)
            {
                a = (a + value) % 65521;
                b = (b + a) % 65521;
            }

            return (b << 16) | a;
        }

        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }

                table[n] = c;
            }

            return table;
        }

        private static uint Crc32(byte[] data)
        {
            var c = 0xFFFFFFFFu;
            foreach (var value in data)
            {
                c = CrcTable[(c ^ value) & 0xFF] ^ (c >> 8);
            }

            return c ^ 0xFFFFFFFFu;
        }
    }
}
