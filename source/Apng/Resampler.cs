/*
ImageGlass APNG Codec Plugin
Copyright (C) 2026 DUONG DIEU PHAP
MIT License
*/
using System.Numerics;
using System.Runtime.InteropServices;

namespace ApngCodec.Apng;

/// <summary>
/// Downscales straight-alpha BGRA8 with a separable Mitchell-Netravali filter.
/// </summary>
internal static unsafe class Resampler
{
    /// <summary>
    /// Filter taps for one axis: output pixel i reads Count[i] source pixels from Start[i].
    /// </summary>
    private sealed class Taps
    {
        public required int[] Start { get; init; }
        public required int[] Count { get; init; }
        public required float[] Weights { get; init; }
        public required int Stride { get; init; }
        public required int MaxCount { get; init; }
    }


    /// <summary>
    /// Returns a new NativeMemory buffer the caller frees; <paramref name="source"/> stays owned by the caller.
    /// </summary>
    public static byte* Downscale(byte* source, int srcWidth, int srcHeight, int dstWidth, int dstHeight)
    {
        var xTaps = CreateTaps(srcWidth, dstWidth);
        var yTaps = CreateTaps(srcHeight, dstHeight);

        // The vertical window only moves forward, so a ring as tall as the widest window filters each source row once.
        var ringRows = yTaps.MaxCount;
        var ring = new Vector4[ringRows * dstWidth];
        var ringSource = new int[ringRows];
        Array.Fill(ringSource, -1);

        var accum = new Vector4[dstWidth];
        var dest = (byte*)NativeMemory.Alloc((nuint)dstWidth * (nuint)dstHeight * 4);

        for (var y = 0; y < dstHeight; y++)
        {
            var start = yTaps.Start[y];
            var count = yTaps.Count[y];
            Array.Clear(accum);

            for (var k = 0; k < count; k++)
            {
                var row = start + k;
                var slot = row % ringRows;
                var filtered = ring.AsSpan(slot * dstWidth, dstWidth);
                if (ringSource[slot] != row)
                {
                    FilterRow(source + ((long)row * srcWidth * 4), filtered, xTaps);
                    ringSource[slot] = row;
                }

                var weight = yTaps.Weights[(y * yTaps.Stride) + k];
                for (var x = 0; x < dstWidth; x++)
                {
                    accum[x] += filtered[x] * weight;
                }
            }

            var outRow = dest + ((long)y * dstWidth * 4);
            for (var x = 0; x < dstWidth; x++)
            {
                Store(accum[x], outRow + (x * 4));
            }
        }

        return dest;
    }


    /// <summary>
    /// Horizontally filters one source row into premultiplied BGRA in the 0..255 range.
    /// </summary>
    private static void FilterRow(byte* src, Span<Vector4> dst, Taps taps)
    {
        for (var x = 0; x < dst.Length; x++)
        {
            var sum = Vector4.Zero;
            var start = taps.Start[x];
            var count = taps.Count[x];
            var weights = x * taps.Stride;

            for (var k = 0; k < count; k++)
            {
                var p = src + ((start + k) * 4);
                int alpha = p[3];
                if (alpha == 0) continue;

                var factor = alpha / 255f * taps.Weights[weights + k];
                sum += new Vector4(p[0], p[1], p[2], 255f) * factor;
            }

            dst[x] = sum;
        }
    }


    /// <summary>
    /// Unpremultiplies and clamps an accumulated pixel, absorbing the filter's small overshoots.
    /// </summary>
    private static void Store(Vector4 v, byte* p)
    {
        var alpha = Math.Min(v.W, 255f);
        if (alpha < 0.5f)
        {
            *(uint*)p = 0;
            return;
        }

        var unpremul = 255f / v.W;
        p[0] = ToByte(v.X * unpremul);
        p[1] = ToByte(v.Y * unpremul);
        p[2] = ToByte(v.Z * unpremul);
        p[3] = ToByte(alpha);
    }


    private static byte ToByte(float v) => (byte)Math.Clamp((int)(v + 0.5f), 0, 255);


    /// <summary>
    /// Builds normalized taps; the kernel widens with the shrink factor so every source pixel contributes.
    /// </summary>
    private static Taps CreateTaps(int srcSize, int dstSize)
    {
        var scale = (double)srcSize / dstSize;
        var filterScale = Math.Max(scale, 1.0);
        var support = 2.0 * filterScale;
        var stride = (int)Math.Ceiling(support * 2) + 2;

        var starts = new int[dstSize];
        var counts = new int[dstSize];
        var weights = new float[dstSize * stride];
        var maxCount = 1;

        for (var i = 0; i < dstSize; i++)
        {
            var center = (i + 0.5) * scale;
            var left = Math.Max(0, (int)Math.Floor(center - support));
            var right = Math.Min(srcSize, (int)Math.Ceiling(center + support));
            var row = i * stride;

            var sum = 0.0;
            for (var j = left; j < right; j++)
            {
                var w = Mitchell((j + 0.5 - center) / filterScale);
                weights[row + j - left] = (float)w;
                sum += w;
            }

            if (sum != 0)
            {
                for (var j = 0; j < right - left; j++)
                {
                    weights[row + j] = (float)(weights[row + j] / sum);
                }
            }

            starts[i] = left;
            counts[i] = right - left;
            maxCount = Math.Max(maxCount, right - left);
        }

        return new Taps
        {
            Start = starts,
            Count = counts,
            Weights = weights,
            Stride = stride,
            MaxCount = maxCount,
        };
    }


    /// <summary>
    /// Mitchell-Netravali kernel with B = C = 1/3: smooth, with only faint ringing.
    /// </summary>
    private static double Mitchell(double x)
    {
        x = Math.Abs(x);
        if (x < 1) return ((7 * x * x * x) - (12 * x * x) + (16.0 / 3)) / 6;
        if (x < 2) return ((-7.0 / 3 * x * x * x) + (12 * x * x) - (20 * x) + (32.0 / 3)) / 6;
        return 0;
    }
}
