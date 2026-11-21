/*
ImageGlass APNG Codec Plugin
Copyright (C) 2026 DUONG DIEU PHAP
MIT License
*/
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace ApngCodec.Apng;

/// <summary>
/// Decodes PNG zlib image data to BGRA8; immutable, so one instance serves every frame and thread.
/// </summary>
internal sealed class PngDecoder
{
    private const int ColorGray = 0;
    private const int ColorRgb = 2;
    private const int ColorPalette = 3;
    private const int ColorGrayAlpha = 4;
    private const int ColorRgba = 6;

    // Adam7 pass origins and steps.
    private static ReadOnlySpan<byte> PassX0 => [0, 4, 0, 2, 0, 1, 0];
    private static ReadOnlySpan<byte> PassY0 => [0, 0, 4, 0, 2, 0, 1];
    private static ReadOnlySpan<byte> PassDx => [8, 8, 4, 4, 2, 2, 1];
    private static ReadOnlySpan<byte> PassDy => [8, 8, 8, 4, 4, 2, 2];

    private readonly int _bitDepth;
    private readonly int _colorType;
    private readonly bool _interlaced;
    private readonly int _bitsPerPixel;

    // 256 BGRA entries, so an out-of-range index reads opaque black instead of overrunning.
    private readonly byte[]? _palette;

    // tRNS colour key at the image's bit depth; -1 never matches a sample.
    private readonly int _keyGray = -1;
    private readonly int _keyR = -1;
    private readonly int _keyG = -1;
    private readonly int _keyB = -1;


    private PngDecoder(int bitDepth, int colorType, bool interlaced, int channels,
        byte[]? palette, byte[]? transparency)
    {
        _bitDepth = bitDepth;
        _colorType = colorType;
        _interlaced = interlaced;
        _bitsPerPixel = bitDepth * channels;

        if (colorType == ColorPalette)
        {
            _palette = new byte[256 * 4];
            for (var i = 0; i < 256; i++)
            {
                var o = i * 4;
                if (palette is not null && (i * 3) + 2 < palette.Length)
                {
                    _palette[o] = palette[(i * 3) + 2];
                    _palette[o + 1] = palette[(i * 3) + 1];
                    _palette[o + 2] = palette[i * 3];
                }
                _palette[o + 3] = transparency is not null && i < transparency.Length ? transparency[i] : (byte)255;
            }
        }
        else if (colorType == ColorGray && transparency is { Length: >= 2 })
        {
            _keyGray = ReadUInt16(transparency, 0);
        }
        else if (colorType == ColorRgb && transparency is { Length: >= 6 })
        {
            _keyR = ReadUInt16(transparency, 0);
            _keyG = ReadUInt16(transparency, 2);
            _keyB = ReadUInt16(transparency, 4);
        }
    }


    /// <summary>
    /// Returns <c>null</c> for an <c>IHDR</c> layout the spec forbids, or an indexed image without <c>PLTE</c>.
    /// </summary>
    public static PngDecoder? Create(byte bitDepth, byte colorType, byte compressionMethod,
        byte filterMethod, byte interlaceMethod, byte[]? palette, byte[]? transparency)
    {
        if (compressionMethod != 0 || filterMethod != 0 || interlaceMethod > 1) return null;

        int channels;
        bool depthOk;
        switch (colorType)
        {
            case ColorGray:
                channels = 1;
                depthOk = bitDepth is 1 or 2 or 4 or 8 or 16;
                break;
            case ColorRgb:
                channels = 3;
                depthOk = bitDepth is 8 or 16;
                break;
            case ColorPalette:
                channels = 1;
                depthOk = bitDepth is 1 or 2 or 4 or 8 && palette is { Length: >= 3 };
                break;
            case ColorGrayAlpha:
                channels = 2;
                depthOk = bitDepth is 8 or 16;
                break;
            case ColorRgba:
                channels = 4;
                depthOk = bitDepth is 8 or 16;
                break;
            default:
                return null;
        }

        return depthOk ? new PngDecoder(bitDepth, colorType, interlaceMethod == 1, channels, palette, transparency) : null;
    }


    /// <summary>
    /// Truncated or corrupt data keeps the rows decoded so far; <c>null</c> only when no row decodes.
    /// </summary>
    public byte[]? Decode(byte[] zlibData, int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height * 4 > Array.MaxLength) return null;

        var output = new byte[width * height * 4];
        var rows = 0;

        using var source = new MemoryStream(zlibData, writable: false);
        using var inflater = new ZLibStream(source, CompressionMode.Decompress);

        try
        {
            if (!_interlaced)
            {
                DecodePass(inflater, output, width, 0, 0, 1, 1, width, height, ref rows);
            }
            else
            {
                for (var pass = 0; pass < 7; pass++)
                {
                    int x0 = PassX0[pass], y0 = PassY0[pass], dx = PassDx[pass], dy = PassDy[pass];
                    var passWidth = width > x0 ? (width - x0 + dx - 1) / dx : 0;
                    var passHeight = height > y0 ? (height - y0 + dy - 1) / dy : 0;

                    // An empty pass has no scanlines at all, not even filter bytes.
                    if (passWidth == 0 || passHeight == 0) continue;

                    if (!DecodePass(inflater, output, width, x0, y0, dx, dy, passWidth, passHeight, ref rows)) break;
                }
            }
        }
        catch (InvalidDataException)
        {
            // corrupt deflate stream: keep what was decoded before it
        }

        return rows > 0 ? output : null;
    }


    /// <summary>
    /// Decodes one Adam7 pass (or the whole image) into the grid x0 + i*dx, y0 + j*dy; <c>false</c> on early end or a bad filter.
    /// </summary>
    private bool DecodePass(Stream source, byte[] output, int imageWidth,
        int x0, int y0, int dx, int dy, int passWidth, int passHeight, ref int rows)
    {
        var rowBytes = (((long)passWidth * _bitsPerPixel) + 7) / 8;

        // Sub-byte sample offsets are counted in bits, so keep them inside an int.
        if (rowBytes > int.MaxValue / 8) return false;

        var bpp = Math.Max(1, _bitsPerPixel / 8);
        var line = new byte[rowBytes + 1];
        var prior = new byte[rowBytes + 1];
        var expanded = dx == 1 ? null : new byte[passWidth * 4];
        var output32 = MemoryMarshal.Cast<byte, uint>(output.AsSpan());

        for (var j = 0; j < passHeight; j++)
        {
            if (source.ReadAtLeast(line, line.Length, throwOnEndOfStream: false) < line.Length) return false;

            var data = line.AsSpan(1);
            if (!Unfilter(line[0], data, prior.AsSpan(1), bpp)) return false;

            var y = y0 + (j * dy);
            if (expanded is null)
            {
                ExpandRow(data, output.AsSpan(y * imageWidth * 4, passWidth * 4), passWidth);
            }
            else
            {
                ExpandRow(data, expanded, passWidth);

                var expanded32 = MemoryMarshal.Cast<byte, uint>(expanded.AsSpan());
                var rowStart = (y * imageWidth) + x0;
                for (var i = 0; i < passWidth; i++)
                {
                    output32[rowStart + (i * dx)] = expanded32[i];
                }
            }

            (line, prior) = (prior, line);
            rows++;
        }

        return true;
    }


    /// <summary>
    /// Reverses a scanline filter in place against the previous unfiltered line.
    /// </summary>
    private static unsafe bool Unfilter(byte filter, Span<byte> lineSpan, ReadOnlySpan<byte> priorSpan, int bpp)
    {
        var n = lineSpan.Length;
        fixed (byte* line = lineSpan)
        fixed (byte* prior = priorSpan)
        {
            switch (filter)
            {
                case 0:
                    return true;

                case 1:
                    for (var i = bpp; i < n; i++)
                    {
                        line[i] += line[i - bpp];
                    }
                    return true;

                case 2:
                {
                    var i = 0;
                    for (; i <= n - Vector128<byte>.Count; i += Vector128<byte>.Count)
                    {
                        (Vector128.Load(line + i) + Vector128.Load(prior + i)).Store(line + i);
                    }
                    for (; i < n; i++)
                    {
                        line[i] += prior[i];
                    }
                    return true;
                }

                case 3:
                    if (bpp is 3 or 4)
                    {
                        AveragePixels(line, prior, n, bpp);
                        return true;
                    }
                    for (var i = 0; i < bpp; i++)
                    {
                        line[i] += (byte)(prior[i] >> 1);
                    }
                    for (var i = bpp; i < n; i++)
                    {
                        line[i] += (byte)((line[i - bpp] + prior[i]) >> 1);
                    }
                    return true;

                case 4:
                    if (bpp is 3 or 4)
                    {
                        PaethPixels(line, prior, n, bpp);
                        return true;
                    }
                    for (var i = 0; i < bpp; i++)
                    {
                        line[i] += prior[i];
                    }
                    for (var i = bpp; i < n; i++)
                    {
                        line[i] += Paeth(line[i - bpp], prior[i], prior[i - bpp]);
                    }
                    return true;

                default:
                    return false;
            }
        }
    }


    /// <summary>
    /// Average filter for 3- or 4-byte pixels, one pixel per step in 16-bit SIMD lanes.
    /// </summary>
    private static unsafe void AveragePixels(byte* line, byte* prior, int n, int bpp)
    {
        var a = Vector128<short>.Zero;
        for (var i = 0; i < n; i += bpp)
        {
            var b = LoadPixel(prior + i, bpp);
            a = (LoadPixel(line + i, bpp) + ((a + b) >>> 1)) & Vector128.Create((short)0xFF);
            StorePixel(line + i, a, bpp);
        }
    }


    /// <summary>
    /// Paeth filter for 3- or 4-byte pixels in 16-bit SIMD lanes: branch-free, and zero a/c on the first pixel yields "add above".
    /// </summary>
    private static unsafe void PaethPixels(byte* line, byte* prior, int n, int bpp)
    {
        var a = Vector128<short>.Zero;
        var c = Vector128<short>.Zero;
        for (var i = 0; i < n; i += bpp)
        {
            var b = LoadPixel(prior + i, bpp);

            // p = a + b - c, so |p - a| = |b - c|, |p - b| = |a - c|, |p - c| = |(b - c) + (a - c)|.
            var pa = b - c;
            var pb = a - c;
            var pc = Vector128.Abs(pa + pb);
            pa = Vector128.Abs(pa);
            pb = Vector128.Abs(pb);

            // Ties go to a, then b, as the spec orders them.
            var smallest = Vector128.Min(pc, Vector128.Min(pa, pb));
            var nearest = Vector128.ConditionalSelect(Vector128.Equals(pa, smallest), a,
                Vector128.ConditionalSelect(Vector128.Equals(pb, smallest), b, c));

            a = (LoadPixel(line + i, bpp) + nearest) & Vector128.Create((short)0xFF);
            StorePixel(line + i, a, bpp);
            c = b;
        }
    }


    /// <summary>
    /// Widens one 3- or 4-byte pixel into 16-bit lanes; a 3-byte pixel never reads past its last byte.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe Vector128<short> LoadPixel(byte* p, int bpp)
    {
        var v = bpp == 4 ? Unsafe.ReadUnaligned<uint>(p) : (uint)(p[0] | (p[1] << 8) | (p[2] << 16));
        return Vector128.WidenLower(Vector128.CreateScalarUnsafe(v).AsByte()).AsInt16();
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void StorePixel(byte* p, Vector128<short> v, int bpp)
    {
        var packed = Vector128.Narrow(v.AsUInt16(), v.AsUInt16()).AsUInt32().ToScalar();
        if (bpp == 4)
        {
            Unsafe.WriteUnaligned(p, packed);
            return;
        }
        p[0] = (byte)packed;
        p[1] = (byte)(packed >> 8);
        p[2] = (byte)(packed >> 16);
    }


    /// <summary>
    /// The spec's Paeth predictor in stb_image's equivalent compare-and-select form.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Paeth(int a, int b, int c)
    {
        var thresh = (c * 3) - (a + b);
        var lo = a < b ? a : b;
        var hi = a < b ? b : a;
        var t0 = hi <= thresh ? lo : c;
        return (byte)(thresh <= lo ? hi : t0);
    }


    /// <summary>
    /// Converts <paramref name="count"/> unfiltered pixels to BGRA8.
    /// </summary>
    private void ExpandRow(ReadOnlySpan<byte> src, Span<byte> dst, int count)
    {
        if (ExpandRowFast(src, dst, count)) return;

        for (var i = 0; i < count; i++)
        {
            var d = i * 4;
            switch (_colorType)
            {
                case ColorGray:
                {
                    var gray = Sample(src, i);
                    var v = To8(gray);
                    dst[d] = v;
                    dst[d + 1] = v;
                    dst[d + 2] = v;
                    dst[d + 3] = gray == _keyGray ? (byte)0 : (byte)255;
                    break;
                }

                case ColorRgb:
                {
                    int r = Sample(src, i * 3), g = Sample(src, (i * 3) + 1), b = Sample(src, (i * 3) + 2);
                    dst[d] = To8(b);
                    dst[d + 1] = To8(g);
                    dst[d + 2] = To8(r);
                    dst[d + 3] = r == _keyR && g == _keyG && b == _keyB ? (byte)0 : (byte)255;
                    break;
                }

                case ColorPalette:
                    _palette.AsSpan(Sample(src, i) * 4, 4).CopyTo(dst.Slice(d, 4));
                    break;

                case ColorGrayAlpha:
                {
                    var v = To8(Sample(src, i * 2));
                    dst[d] = v;
                    dst[d + 1] = v;
                    dst[d + 2] = v;
                    dst[d + 3] = To8(Sample(src, (i * 2) + 1));
                    break;
                }

                case ColorRgba:
                    dst[d] = To8(Sample(src, (i * 4) + 2));
                    dst[d + 1] = To8(Sample(src, (i * 4) + 1));
                    dst[d + 2] = To8(Sample(src, i * 4));
                    dst[d + 3] = To8(Sample(src, (i * 4) + 3));
                    break;
            }
        }
    }


    /// <summary>
    /// Pointer loops for 8-bit RGB(A)/indexed and 16-bit RGB(A); <c>false</c> leaves the row to the generic path.
    /// </summary>
    private unsafe bool ExpandRowFast(ReadOnlySpan<byte> srcSpan, Span<byte> dstSpan, int count)
    {
        fixed (byte* src = srcSpan)
        fixed (byte* dstBytes = dstSpan)
        fixed (byte* paletteBytes = _palette)
        {
            // Little-endian: BGRA bytes store as 0xAARRGGBB.
            var dst = (uint*)dstBytes;
            var keyed = _keyR >= 0;

            switch ((_colorType, _bitDepth))
            {
                case (ColorRgba, 8):
                    for (var i = 0; i < count; i++)
                    {
                        var v = Unsafe.ReadUnaligned<uint>(src + (i * 4));
                        dst[i] = (v & 0xFF00FF00u) | ((v & 0xFFu) << 16) | ((v >> 16) & 0xFFu);
                    }
                    return true;

                case (ColorRgb, 8):
                    for (var i = 0; i < count; i++)
                    {
                        var p = src + (i * 3);
                        var rgb = (uint)((p[0] << 16) | (p[1] << 8) | p[2]);
                        var opaque = !keyed || p[0] != _keyR || p[1] != _keyG || p[2] != _keyB;
                        dst[i] = rgb | (opaque ? 0xFF000000u : 0u);
                    }
                    return true;

                case (ColorPalette, 8):
                    var palette = (uint*)paletteBytes;
                    for (var i = 0; i < count; i++)
                    {
                        dst[i] = palette[src[i]];
                    }
                    return true;

                case (ColorRgba, 16):
                    for (var i = 0; i < count; i++)
                    {
                        var p = src + (i * 8);
                        dst[i] = ((uint)Scale16(p + 6) << 24) | ((uint)Scale16(p) << 16) | ((uint)Scale16(p + 2) << 8) | Scale16(p + 4);
                    }
                    return true;

                case (ColorRgb, 16):
                    for (var i = 0; i < count; i++)
                    {
                        var p = src + (i * 6);
                        int r = (p[0] << 8) | p[1], g = (p[2] << 8) | p[3], b = (p[4] << 8) | p[5];
                        var opaque = !keyed || r != _keyR || g != _keyG || b != _keyB;
                        dst[i] = (opaque ? 0xFF000000u : 0u) | ((uint)To8(r) << 16) | ((uint)To8(g) << 8) | To8(b);
                    }
                    return true;

                default:
                    return false;
            }
        }
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe byte Scale16(byte* p) => (byte)(((((p[0] << 8) | p[1]) * 255) + 32895) >> 16);


    /// <summary>
    /// Reads the <paramref name="index"/>-th sample of a row at the image's bit depth.
    /// </summary>
    private int Sample(ReadOnlySpan<byte> row, int index)
    {
        switch (_bitDepth)
        {
            case 8:
                return row[index];

            case 16:
                return (row[index * 2] << 8) | row[(index * 2) + 1];

            default:
                // Sub-byte samples are packed MSB first.
                var bit = index * _bitDepth;
                var shift = 8 - _bitDepth - (bit & 7);
                return (row[bit >> 3] >> shift) & ((1 << _bitDepth) - 1);
        }
    }


    /// <summary>
    /// Scales a sample at the image's bit depth to 0..255.
    /// </summary>
    private byte To8(int sample) => _bitDepth switch
    {
        1 => (byte)(sample * 255),
        2 => (byte)(sample * 85),
        4 => (byte)(sample * 17),
        8 => (byte)sample,
        _ => (byte)(((sample * 255) + 32895) >> 16),
    };


    private static int ReadUInt16(byte[] bytes, int offset) => (bytes[offset] << 8) | bytes[offset + 1];
}
