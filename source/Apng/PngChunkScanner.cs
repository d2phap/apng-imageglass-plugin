/*
ImageGlass APNG Codec Plugin
Copyright (C) 2026 DUONG DIEU PHAP
MIT License
*/
using System.IO.Compression;
using ImageGlass.SDK.Plugins;

namespace ApngCodec.Apng;

/// <summary>
/// Colour and transparency hints read straight from the PNG chunk stream.
/// </summary>
internal readonly struct PngColorInfo
{
    /// <summary>
    /// Raw ICC profile bytes from the <c>iCCP</c> chunk, or <c>null</c> when absent.
    /// </summary>
    public byte[]? IccProfile { get; init; }

    /// <summary>
    /// Colour-space hint used when no ICC profile is embedded.
    /// </summary>
    public IGColorSpace ColorSpace { get; init; }

    /// <summary>
    /// Whether a <c>tRNS</c> chunk makes an otherwise opaque colour type transparent.
    /// </summary>
    public bool HasTransparencyChunk { get; init; }
}


/// <summary>
/// Minimal forward scan over the PNG chunk stream for the ancillary chunks LibAPNG
/// does not surface (<c>iCCP</c>, <c>sRGB</c>, <c>tRNS</c>). Stops at the first
/// <c>IDAT</c>, which the spec requires all three to precede.
/// </summary>
internal static class PngChunkScanner
{
    /// <summary>
    /// Reads the colour hints from a PNG/APNG byte stream. Never throws: a malformed
    /// chunk table simply ends the scan with whatever was found so far.
    /// </summary>
    public static PngColorInfo Scan(byte[] fileBytes)
    {
        byte[]? icc = null;
        var colorSpace = IGColorSpace.Unknown;
        var hasTrns = false;

        // PNG signature is 8 bytes; every chunk is length(4) + type(4) + data + crc(4).
        var offset = 8;
        while (offset + 8 <= fileBytes.Length)
        {
            var length = ReadBigEndianInt32(fileBytes, offset);
            if (length < 0 || offset + 12L + length > fileBytes.Length) break;

            var type = ReadChunkType(fileBytes, offset + 4);
            var dataStart = offset + 8;

            if (type == "IDAT" || type == "IEND") break;

            switch (type)
            {
                case "iCCP":
                    icc = TryReadIccProfile(fileBytes, dataStart, length);
                    break;

                case "sRGB":
                    colorSpace = IGColorSpace.Srgb;
                    break;

                case "tRNS":
                    hasTrns = true;
                    break;
            }

            offset = dataStart + length + 4;
        }

        return new PngColorInfo
        {
            IccProfile = icc,
            ColorSpace = icc is null ? colorSpace : IGColorSpace.Unknown,
            HasTransparencyChunk = hasTrns,
        };
    }


    /// <summary>
    /// Inflates the zlib payload of an <c>iCCP</c> chunk. Returns <c>null</c> when the
    /// chunk is malformed or uses an unknown compression method.
    /// </summary>
    private static byte[]? TryReadIccProfile(byte[] bytes, int dataStart, int length)
    {
        // Layout: profile name (1..79 bytes, NUL-terminated), compression method, zlib stream.
        var nameEnd = -1;
        var limit = Math.Min(dataStart + 80, dataStart + length);
        for (var i = dataStart; i < limit; i++)
        {
            if (bytes[i] == 0) { nameEnd = i; break; }
        }
        if (nameEnd < 0) return null;

        var methodIndex = nameEnd + 1;
        if (methodIndex >= dataStart + length || bytes[methodIndex] != 0) return null;

        var payloadStart = methodIndex + 1;
        var payloadLength = dataStart + length - payloadStart;
        if (payloadLength <= 0) return null;

        try
        {
            using var source = new MemoryStream(bytes, payloadStart, payloadLength, writable: false);
            using var inflater = new ZLibStream(source, CompressionMode.Decompress);
            using var output = new MemoryStream();
            inflater.CopyTo(output);
            return output.Length > 0 ? output.ToArray() : null;
        }
        catch
        {
            return null;
        }
    }


    private static int ReadBigEndianInt32(byte[] bytes, int offset)
    {
        return (bytes[offset] << 24) | (bytes[offset + 1] << 16)
            | (bytes[offset + 2] << 8) | bytes[offset + 3];
    }


    private static string ReadChunkType(byte[] bytes, int offset)
    {
        return string.Create(4, (bytes, offset), static (span, state) =>
        {
            for (var i = 0; i < 4; i++) span[i] = (char)state.bytes[state.offset + i];
        });
    }
}
