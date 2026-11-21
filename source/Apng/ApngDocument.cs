/*
ImageGlass APNG Codec Plugin
Copyright (C) 2026 DUONG DIEU PHAP
MIT License

Parses one APNG file with LibAPNG (APNG.NET) and composes its frames.

The host contract requires every frame handed back to be a fully composed
RGBA canvas: APNG stores each frame as a sub-rectangle plus a blend and a
disposal operation, so the replay below is the plugin's job, not the host's.
*/
using ImageGlass.SDK.Plugins;
using LibAPNG;

namespace ApngCodec.Apng;

/// <summary>
/// A parsed APNG file plus the canvas state used to replay its frames.
/// All composition runs under <see cref="_lock"/> because the host may call the
/// codec entry points concurrently.
/// </summary>
internal sealed unsafe class ApngDocument : IDisposable
{
    /// <summary>
    /// One animation frame: where it lands on the canvas and how it is merged.
    /// </summary>
    private readonly struct ApngFrame
    {
        /// <summary>
        /// The frame's zlib stream: its IDAT or fdAT payloads joined in order.
        /// </summary>
        public required byte[] Data { get; init; }
        public required int X { get; init; }
        public required int Y { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required DisposeOps Dispose { get; init; }
        public required BlendOps Blend { get; init; }
        public required int DurationMs { get; init; }
    }


    private readonly Lock _lock = new();
    private readonly ApngFrame[] _frames;
    private readonly PngDecoder _decoder;
    private readonly bool _usesPreviousDisposal;

    private byte[]? _canvas;
    private byte[]? _restore;
    private int _nextFrame;
    private bool _disposed;


    /// <summary>
    /// Canvas width in pixels (the <c>IHDR</c> width, which every composed frame uses).
    /// </summary>
    public int Width { get; }

    /// <summary>
    /// Canvas height in pixels.
    /// </summary>
    public int Height { get; }

    /// <summary>
    /// Number of animation frames; 1 for a still PNG.
    /// </summary>
    public int FrameCount => _frames.Length;

    /// <summary>
    /// Number of play-throughs from <c>acTL</c>; 0 means infinite.
    /// </summary>
    public int LoopCount { get; }

    /// <summary>
    /// Whether the composed canvas can contain non-opaque pixels.
    /// </summary>
    public bool HasAlpha { get; }

    /// <summary>
    /// Raw bytes of the embedded <c>iCCP</c> profile, or <c>null</c> when absent.
    /// </summary>
    public byte[]? IccProfile { get; }

    /// <summary>
    /// Colour-space hint used when <see cref="IccProfile"/> is <c>null</c>.
    /// </summary>
    public IGColorSpace ColorSpace { get; }

    /// <summary>
    /// Size of the source file in bytes.
    /// </summary>
    public long FileSizeBytes { get; }

    /// <summary>
    /// Byte size of one composed frame (<c>Width * Height * 4</c>).
    /// </summary>
    public int FrameByteSize => Width * Height * 4;


    /// <summary>
    /// Gets the display duration of a frame in milliseconds.
    /// </summary>
    public int GetFrameDurationMs(int index)
    {
        return index >= 0 && index < _frames.Length ? _frames[index].DurationMs : 0;
    }


    /// <summary>
    /// Reads and parses an APNG file. Returns <c>null</c> and a failure status when the
    /// file cannot be read or is not a valid PNG/APNG stream.
    /// </summary>
    public static ApngDocument? Load(string filePath, out IGStatus status)
    {
        byte[] fileBytes;
        try
        {
            fileBytes = File.ReadAllBytes(filePath);
        }
        catch (Exception ex)
        {
            HostChannel.Log(4, $"ApngCodec: cannot read '{filePath}' ({ex.Message}).");
            status = IGStatus.IoError;
            return null;
        }

        APNG apng;
        try
        {
            apng = new APNG(fileBytes);
        }
        catch (Exception ex)
        {
            HostChannel.Log(4, $"ApngCodec: '{Path.GetFileName(filePath)}' is not a valid APNG ({ex.Message}).");
            status = IGStatus.DecodeFailed;
            return null;
        }

        // Header reads, the chunk scan and the frame table are all file-driven, and this runs
        // behind an ABI boundary where an escaping exception is fatal. Keep them guarded.
        try
        {
            var ihdr = apng.IHDRChunk;
            var width = ihdr.Width;
            var height = ihdr.Height;
            if (width <= 0 || height <= 0 || (long)width * height * 4L > int.MaxValue)
            {
                status = IGStatus.DecodeFailed;
                return null;
            }

            var colorInfo = PngChunkScanner.Scan(fileBytes);
            var decoder = PngDecoder.Create(ihdr.BitDepth, ihdr.ColorType, ihdr.CompressionMethod,
                ihdr.FilterMethod, ihdr.InterlaceMethod, colorInfo.Palette, colorInfo.Transparency);
            if (decoder is null)
            {
                HostChannel.Log(4, $"ApngCodec: '{Path.GetFileName(filePath)}' has an invalid pixel layout (color type {ihdr.ColorType}, {ihdr.BitDepth}-bit).");
                status = IGStatus.DecodeFailed;
                return null;
            }

            status = IGStatus.OK;
            return new ApngDocument(apng, colorInfo, decoder, fileBytes.Length, width, height);
        }
        catch (Exception ex)
        {
            HostChannel.Log(4, $"ApngCodec: '{Path.GetFileName(filePath)}' has an unusable frame table ({ex.Message}).");
            status = IGStatus.DecodeFailed;
            return null;
        }
    }


    private ApngDocument(APNG apng, PngColorInfo colorInfo, PngDecoder decoder, long fileSize, int width, int height)
    {
        Width = width;
        Height = height;
        FileSizeBytes = fileSize;
        IccProfile = colorInfo.IccProfile;
        ColorSpace = colorInfo.ColorSpace;
        _decoder = decoder;

        var sourceFrames = apng.Frames;
        if (apng.IsSimplePNG || sourceFrames.Length == 0)
        {
            _frames =
            [
                new ApngFrame
                {
                    Data = JoinImageData(apng.DefaultImage.IDATChunks),
                    X = 0,
                    Y = 0,
                    Width = width,
                    Height = height,
                    Dispose = DisposeOps.APNGDisposeOpNone,
                    Blend = BlendOps.APNGBlendOpSource,
                    DurationMs = 0,
                },
            ];
            LoopCount = 1;
        }
        else
        {
            _frames = new ApngFrame[sourceFrames.Length];
            for (var i = 0; i < sourceFrames.Length; i++)
            {
                var source = sourceFrames[i];
                var fctl = source.fcTLChunk;
                var dispose = fctl.DisposeOp;

                // The spec forbids DisposeOpPrevious on the first frame; fall back to Background.
                if (i == 0 && dispose == DisposeOps.APNGDisposeOpPrevious)
                {
                    dispose = DisposeOps.APNGDisposeOpBackground;
                }
                if (dispose == DisposeOps.APNGDisposeOpPrevious) _usesPreviousDisposal = true;

                _frames[i] = new ApngFrame
                {
                    Data = JoinImageData(source.IDATChunks),
                    X = (int)fctl.XOffset,
                    Y = (int)fctl.YOffset,
                    Width = (int)fctl.Width,
                    Height = (int)fctl.Height,
                    Dispose = dispose,
                    Blend = fctl.BlendOp,
                    DurationMs = ToDurationMs(fctl.DelayNum, fctl.DelayDen),
                };
            }

            LoopCount = apng.acTLChunk is null ? 0 : (int)Math.Min(apng.acTLChunk.NumPlays, int.MaxValue);
        }

        HasAlpha = ComputeHasAlpha(apng.IHDRChunk.ColorType, colorInfo.HasTransparencyChunk);
    }


    /// <summary>
    /// Composes <paramref name="frameIndex"/> and copies the canvas into
    /// <paramref name="dest"/>, which must hold at least <see cref="FrameByteSize"/> bytes.
    /// Frames are replayed from the current canvas state, or from frame 0 when the
    /// request moves backwards.
    /// </summary>
    public IGStatus CopyFrameTo(int frameIndex, byte* dest, void* cancellation)
    {
        if (dest == null) return IGStatus.InvalidArg;
        if (frameIndex < 0 || frameIndex >= _frames.Length) return IGStatus.InvalidArg;

        lock (_lock)
        {
            if (_disposed) return IGStatus.Internal;

            EnsureBuffers();
            if (frameIndex < _nextFrame) ResetCanvas();

            for (var i = _nextFrame; i <= frameIndex; i++)
            {
                if (HostChannel.IsCanceled(cancellation)) return IGStatus.Canceled;

                if (_frames[i].Dispose == DisposeOps.APNGDisposeOpPrevious)
                {
                    Array.Copy(_canvas!, _restore!, _canvas!.Length);
                }

                var status = RenderFrame(i);
                if (status != IGStatus.OK) return status;

                if (i == frameIndex)
                {
                    fixed (byte* src = _canvas)
                    {
                        Buffer.MemoryCopy(src, dest, _canvas!.Length, _canvas.Length);
                    }
                }

                ApplyDisposal(i);
                _nextFrame = i + 1;
            }

            return IGStatus.OK;
        }
    }


    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _canvas = null;
            _restore = null;
        }
    }


    // ------------------------------ Composition ------------------------------

    /// <summary>
    /// Decodes one frame's sub-image and merges it onto the canvas with its blend op.
    /// </summary>
    private IGStatus RenderFrame(int index)
    {
        ref readonly var frame = ref _frames[index];

        // fcTL is file-driven: a frame bigger than the canvas is malformed and would only allocate to be clipped.
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Width > Width || frame.Height > Height)
        {
            HostChannel.Log(4, $"ApngCodec: frame {index} is {frame.Width}x{frame.Height} on a {Width}x{Height} canvas.");
            return IGStatus.DecodeFailed;
        }

        var pixels = _decoder.Decode(frame.Data, frame.Width, frame.Height);
        if (pixels is null)
        {
            HostChannel.Log(4, $"ApngCodec: frame {index} could not be decoded.");
            return IGStatus.DecodeFailed;
        }

        if (frame.Blend == BlendOps.APNGBlendOpSource)
        {
            CopyRect(pixels, frame.Width, frame.Height, frame.X, frame.Y);
        }
        else
        {
            BlendRect(pixels, frame.Width, frame.Height, frame.X, frame.Y);
        }

        return IGStatus.OK;
    }


    /// <summary>
    /// Applies the frame's disposal operation, leaving the canvas ready for the next frame.
    /// </summary>
    private void ApplyDisposal(int index)
    {
        ref readonly var frame = ref _frames[index];

        switch (frame.Dispose)
        {
            case DisposeOps.APNGDisposeOpBackground:
                ClearRect(frame.X, frame.Y, frame.Width, frame.Height);
                break;

            case DisposeOps.APNGDisposeOpPrevious:
                Array.Copy(_restore!, _canvas!, _canvas!.Length);
                break;
        }
    }


    /// <summary>
    /// Overwrites the destination rectangle with the source pixels, alpha included
    /// (<c>APNG_BLEND_OP_SOURCE</c>).
    /// </summary>
    private void CopyRect(byte[] src, int srcWidth, int srcHeight, int destX, int destY)
    {
        for (var row = 0; row < srcHeight; row++)
        {
            if (!TryGetRowSpan(row, srcWidth, destX, destY, out var startX, out var count, out var destOffset))
                continue;

            Array.Copy(src, ((row * srcWidth) + startX) * 4, _canvas!, destOffset, count * 4);
        }
    }


    /// <summary>
    /// Alpha-composites the source over the destination rectangle
    /// (<c>APNG_BLEND_OP_OVER</c>) in straight (unpremultiplied) alpha.
    /// </summary>
    private void BlendRect(byte[] src, int srcWidth, int srcHeight, int destX, int destY)
    {
        var canvas = _canvas!;

        for (var row = 0; row < srcHeight; row++)
        {
            if (!TryGetRowSpan(row, srcWidth, destX, destY, out var startX, out var count, out var destOffset))
                continue;

            var srcOffset = ((row * srcWidth) + startX) * 4;
            for (var i = 0; i < count; i++)
            {
                var s = srcOffset + (i * 4);
                var d = destOffset + (i * 4);

                int srcAlpha = src[s + 3];
                if (srcAlpha == 0) continue;

                if (srcAlpha == 255)
                {
                    canvas[d] = src[s];
                    canvas[d + 1] = src[s + 1];
                    canvas[d + 2] = src[s + 2];
                    canvas[d + 3] = 255;
                    continue;
                }

                int destAlpha = canvas[d + 3];
                var outAlpha = (srcAlpha * 255) + (destAlpha * (255 - srcAlpha));

                for (var c = 0; c < 3; c++)
                {
                    var numerator = (src[s + c] * srcAlpha * 255) + (canvas[d + c] * destAlpha * (255 - srcAlpha));
                    canvas[d + c] = (byte)((numerator + (outAlpha / 2)) / outAlpha);
                }
                canvas[d + 3] = (byte)((outAlpha + 127) / 255);
            }
        }
    }


    /// <summary>
    /// Resets a canvas rectangle to fully transparent black.
    /// </summary>
    private void ClearRect(int x, int y, int width, int height)
    {
        for (var row = 0; row < height; row++)
        {
            if (!TryGetRowSpan(row, width, x, y, out _, out var count, out var destOffset)) continue;

            Array.Clear(_canvas!, destOffset, count * 4);
        }
    }


    /// <summary>
    /// Clips one row of a <paramref name="rectWidth"/>-wide rectangle placed at
    /// (<paramref name="destX"/>, <paramref name="destY"/>) against the canvas. Returns
    /// <c>false</c> when the row falls entirely outside; otherwise yields the first
    /// in-range column, the number of pixels to touch, and the canvas byte offset.
    /// </summary>
    private bool TryGetRowSpan(int row, int rectWidth, int destX, int destY,
        out int startX, out int count, out int destOffset)
    {
        startX = 0;
        count = 0;
        destOffset = 0;

        var y = destY + row;
        if (y < 0 || y >= Height) return false;

        startX = Math.Max(0, -destX);
        var endX = Math.Min(rectWidth, Width - destX);
        if (endX <= startX) return false;

        count = endX - startX;
        destOffset = ((y * Width) + destX + startX) * 4;
        return true;
    }


    private void EnsureBuffers()
    {
        _canvas ??= new byte[FrameByteSize];
        if (_usesPreviousDisposal) _restore ??= new byte[FrameByteSize];
    }


    private void ResetCanvas()
    {
        Array.Clear(_canvas!);
        _nextFrame = 0;
    }


    // ------------------------------ Helpers ------------------------------

    /// <summary>
    /// Joins a frame's data chunks into the single zlib stream they split between them.
    /// </summary>
    private static byte[] JoinImageData(List<IDATChunk> chunks)
    {
        if (chunks.Count == 1) return chunks[0].ChunkData;

        var total = 0L;
        foreach (var chunk in chunks) total += chunk.ChunkData.Length;

        var data = new byte[total];
        var offset = 0;
        foreach (var chunk in chunks)
        {
            chunk.ChunkData.CopyTo(data, offset);
            offset += chunk.ChunkData.Length;
        }
        return data;
    }


    /// <summary>
    /// Converts an APNG delay fraction to milliseconds; a zero denominator means 1/100 s.
    /// </summary>
    private static int ToDurationMs(ushort delayNum, ushort delayDen)
    {
        var den = delayDen == 0 ? 100 : delayDen;
        return (int)Math.Round(delayNum * 1000.0 / den);
    }


    /// <summary>
    /// Decides whether the composed canvas can hold non-opaque pixels. Any frame that
    /// does not cover the whole canvas, or clears itself to background, exposes it.
    /// </summary>
    private bool ComputeHasAlpha(byte colorType, bool hasTransparencyChunk)
    {
        // PNG colour types 4 (gray+alpha) and 6 (RGBA) always carry alpha.
        if (colorType == 4 || colorType == 6 || hasTransparencyChunk) return true;

        foreach (var frame in _frames)
        {
            if (frame.X != 0 || frame.Y != 0 || frame.Width != Width || frame.Height != Height) return true;
            if (frame.Dispose == DisposeOps.APNGDisposeOpBackground) return true;
        }

        return false;
    }
}
