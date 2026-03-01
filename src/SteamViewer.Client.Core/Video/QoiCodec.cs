namespace SteamViewer.Client.Core.Video;

/// <summary>
/// QOI (Quite OK Image) lossless codec for BGRA pixel data.
/// Used for lossless settle frames — pixel-perfect text after screen stops changing.
/// Spec: https://qoiformat.org/qoi-specification.pdf
///
/// BGRA input (DXGI native) is handled by swapping R/B channel reads.
/// ~20-50x faster than PNG encode, PNG-level compression.
/// </summary>
public static class QoiCodec
{
    private const uint QOI_MAGIC = 0x716F6966; // "qoif"
    private const byte QOI_OP_INDEX = 0x00; // 00xxxxxx
    private const byte QOI_OP_DIFF  = 0x40; // 01xxxxxx
    private const byte QOI_OP_LUMA  = 0x80; // 10xxxxxx
    private const byte QOI_OP_RUN   = 0xC0; // 11xxxxxx
    private const byte QOI_OP_RGB   = 0xFE;
    private const byte QOI_OP_RGBA  = 0xFF;
    private const byte QOI_MASK_2   = 0xC0;

    private static readonly byte[] EndMarker = { 0, 0, 0, 0, 0, 0, 0, 1 };

    private static int ColorHash(byte r, byte g, byte b, byte a)
        => (r * 3 + g * 5 + b * 7 + a * 11) % 64;

    /// <summary>
    /// Encode BGRA pixel data to QOI format.
    /// Input is DXGI BGRA layout: [B, G, R, A] per pixel.
    /// </summary>
    public static byte[] Encode(byte[] bgra, int width, int height, int stride)
    {
        var pixelCount = width * height;
        // Worst case: header(14) + all RGBA ops(5 per pixel) + end(8)
        var maxSize = 14 + pixelCount * 5 + EndMarker.Length;
        var output = new byte[maxSize];
        var p = 0;

        // Header: magic(4) + width(4) + height(4) + channels(1) + colorspace(1)
        WriteBE32(output, ref p, QOI_MAGIC);
        WriteBE32(output, ref p, (uint)width);
        WriteBE32(output, ref p, (uint)height);
        output[p++] = 4; // channels = RGBA
        output[p++] = 0; // colorspace = sRGB

        // Running pixel array (64 entries, RGBA)
        var index = new byte[64 * 4]; // flat: [r,g,b,a, r,g,b,a, ...]

        byte prevR = 0, prevG = 0, prevB = 0, prevA = 255;
        var run = 0;

        for (var y = 0; y < height; y++)
        {
            var rowOffset = y * stride;
            for (var x = 0; x < width; x++)
            {
                var px = rowOffset + x * 4;
                // BGRA layout: [0]=B, [1]=G, [2]=R, [3]=A
                var b = bgra[px];
                var g = bgra[px + 1];
                var r = bgra[px + 2];
                var a = bgra[px + 3];

                if (r == prevR && g == prevG && b == prevB && a == prevA)
                {
                    run++;
                    if (run == 62 || (y == height - 1 && x == width - 1))
                    {
                        output[p++] = (byte)(QOI_OP_RUN | (run - 1));
                        run = 0;
                    }
                    continue;
                }

                if (run > 0)
                {
                    output[p++] = (byte)(QOI_OP_RUN | (run - 1));
                    run = 0;
                }

                var hash = ColorHash(r, g, b, a);
                var idx = hash * 4;

                if (index[idx] == r && index[idx + 1] == g && index[idx + 2] == b && index[idx + 3] == a)
                {
                    output[p++] = (byte)(QOI_OP_INDEX | hash);
                }
                else
                {
                    index[idx] = r;
                    index[idx + 1] = g;
                    index[idx + 2] = b;
                    index[idx + 3] = a;

                    if (a == prevA)
                    {
                        var dr = r - prevR;
                        var dg = g - prevG;
                        var db = b - prevB;

                        var drDg = dr - dg;
                        var dbDg = db - dg;

                        if (dr >= -2 && dr <= 1 && dg >= -2 && dg <= 1 && db >= -2 && db <= 1)
                        {
                            output[p++] = (byte)(QOI_OP_DIFF | ((dr + 2) << 4) | ((dg + 2) << 2) | (db + 2));
                        }
                        else if (dg >= -32 && dg <= 31 && drDg >= -8 && drDg <= 7 && dbDg >= -8 && dbDg <= 7)
                        {
                            output[p++] = (byte)(QOI_OP_LUMA | (dg + 32));
                            output[p++] = (byte)(((drDg + 8) << 4) | (dbDg + 8));
                        }
                        else
                        {
                            output[p++] = QOI_OP_RGB;
                            output[p++] = r;
                            output[p++] = g;
                            output[p++] = b;
                        }
                    }
                    else
                    {
                        output[p++] = QOI_OP_RGBA;
                        output[p++] = r;
                        output[p++] = g;
                        output[p++] = b;
                        output[p++] = a;
                    }
                }

                prevR = r; prevG = g; prevB = b; prevA = a;
            }
        }

        // End marker
        Buffer.BlockCopy(EndMarker, 0, output, p, EndMarker.Length);
        p += EndMarker.Length;

        // Trim to actual size
        var result = new byte[p];
        Buffer.BlockCopy(output, 0, result, 0, p);
        return result;
    }

    /// <summary>
    /// Decode QOI data to BGRA pixel data (DXGI-native layout).
    /// </summary>
    public static byte[] Decode(byte[] qoiData, out int width, out int height)
    {
        var p = 0;

        // Header
        var magic = ReadBE32(qoiData, ref p);
        if (magic != QOI_MAGIC)
            throw new InvalidDataException($"Invalid QOI magic: 0x{magic:X8}");

        width = (int)ReadBE32(qoiData, ref p);
        height = (int)ReadBE32(qoiData, ref p);
        var channels = qoiData[p++];
        var colorspace = qoiData[p++];

        var pixelCount = width * height;
        var stride = width * 4;
        var bgra = new byte[pixelCount * 4];

        var index = new byte[64 * 4];
        byte r = 0, g = 0, b = 0, a = 255;
        var run = 0;
        var dataEnd = qoiData.Length - EndMarker.Length;

        for (var px = 0; px < pixelCount; px++)
        {
            if (run > 0)
            {
                run--;
            }
            else if (p < dataEnd)
            {
                var op = qoiData[p++];

                if (op == QOI_OP_RGB)
                {
                    r = qoiData[p++];
                    g = qoiData[p++];
                    b = qoiData[p++];
                }
                else if (op == QOI_OP_RGBA)
                {
                    r = qoiData[p++];
                    g = qoiData[p++];
                    b = qoiData[p++];
                    a = qoiData[p++];
                }
                else if ((op & QOI_MASK_2) == QOI_OP_INDEX)
                {
                    var idx = (op & 0x3F) * 4;
                    r = index[idx];
                    g = index[idx + 1];
                    b = index[idx + 2];
                    a = index[idx + 3];
                }
                else if ((op & QOI_MASK_2) == QOI_OP_DIFF)
                {
                    r += (byte)(((op >> 4) & 0x03) - 2);
                    g += (byte)(((op >> 2) & 0x03) - 2);
                    b += (byte)((op & 0x03) - 2);
                }
                else if ((op & QOI_MASK_2) == QOI_OP_LUMA)
                {
                    var b2 = qoiData[p++];
                    var dg = (op & 0x3F) - 32;
                    r += (byte)(dg + ((b2 >> 4) & 0x0F) - 8);
                    g += (byte)dg;
                    b += (byte)(dg + (b2 & 0x0F) - 8);
                }
                else if ((op & QOI_MASK_2) == QOI_OP_RUN)
                {
                    run = op & 0x3F; // run-1 remaining after this pixel
                }

                var hash = ColorHash(r, g, b, a) * 4;
                index[hash] = r;
                index[hash + 1] = g;
                index[hash + 2] = b;
                index[hash + 3] = a;
            }

            // Output as BGRA (DXGI layout)
            var dst = px * 4;
            bgra[dst] = b;     // B
            bgra[dst + 1] = g; // G
            bgra[dst + 2] = r; // R
            bgra[dst + 3] = a; // A
        }

        return bgra;
    }

    private static void WriteBE32(byte[] buf, ref int pos, uint value)
    {
        buf[pos++] = (byte)(value >> 24);
        buf[pos++] = (byte)(value >> 16);
        buf[pos++] = (byte)(value >> 8);
        buf[pos++] = (byte)value;
    }

    private static uint ReadBE32(byte[] buf, ref int pos)
    {
        var v = (uint)(buf[pos] << 24 | buf[pos + 1] << 16 | buf[pos + 2] << 8 | buf[pos + 3]);
        pos += 4;
        return v;
    }
}
