/*
ImageGlass APNG Codec Plugin
Copyright (C) 2026 DUONG DIEU PHAP
MIT License

Native codec plugin that adds animated PNG (".apng") support to ImageGlass 10.

Frames are parsed with LibAPNG (APNG.NET) and composited by this plugin: APNG
stores each frame as a sub-rectangle with its own blend and disposal operation,
while the host expects a fully composed canvas per frame. Pixels are emitted as
32bpp straight-alpha BGRA (IGPixelFormat.Bgra8Unorm).
*/
using ApngCodec.Apng;
using ImageGlass.SDK.Plugins;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace ApngCodec;

internal static unsafe class ApngCodecPlugin
{
    // ------------------------------ Static buffers ------------------------------
    // Everything the host receives must outlive the call. The API tables, id
    // strings and the extension table are process-lifetime native blocks that
    // are intentionally never freed.

    private const string PluginIdString = "Plugin_ApngCodec";
    private const string PluginNameString = "APNG Codec";
    private const string VersionString = "1.0.0";
    private const string CodecIdString = "plugin.apng.codec";
    private const string CodecNameString = "Animated PNG";

    private static readonly string[] SupportedExtensions = [".apng"];

    /// <summary>PNG signature shared by every APNG file.</summary>
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static IGPluginApi* _pluginApi;
    private static IGCodecApi* _codecApi;

    // UTF-16 string buffers (process-lifetime).
    private static char* _bufPluginId;
    private static char* _bufPluginName;
    private static char* _bufVersion;
    private static char* _bufCodecId;
    private static char* _bufCodecName;
    private static char** _bufExtensions;
    private static IGStringRef* _extArray;

    // Live allocations handed to the host, so the free callbacks can validate them.
    private static readonly Lock _bufLock = new();
    private static readonly HashSet<nint> _liveBuffers = [];
    private static readonly HashSet<nint> _liveFrameArrays = [];

    // The host reads IGImageInfo.IccProfileData AFTER LoadMetadata returns, so profiles
    // go into a small process-lifetime ring instead of being freed on the way out.
    private const int ICC_RING_SIZE = 4;
    private static readonly Lock _iccLock = new();
    private static readonly nint[] _iccRing = new nint[ICC_RING_SIZE];
    private static int _iccSlot;


    // ------------------------------ Entry point ------------------------------

    [UnmanagedCallersOnly(EntryPoint = IGNativeAbi.ENTRY_POINT_NAME, CallConvs = [typeof(CallConvCdecl)])]
    public static IGPluginApi* GetApi(int hostAbiVersion, IGHostApi* hostApi)
    {
        // Major-version mismatch: refuse to load.
        if (hostAbiVersion / 1_000_000 != IGNativeAbi.IG_PLUGIN_ABI_MAJOR) return null;
        if (hostApi == null) return null;

        if (_pluginApi != null) return _pluginApi;
        HostChannel.Attach(hostApi);

        InitStrings();
        InitCodecApi();
        InitPluginApi();
        return _pluginApi;
    }


    // ------------------------------ Plugin API callbacks ------------------------------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static IGStatus OnInitialize() => IGStatus.OK;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnShutdown() { }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static IGStatus OnGetCodec(int index, IGCodecApi** outCodecApi)
    {
        if (outCodecApi == null) return IGStatus.InvalidArg;
        if (index != 0) { *outCodecApi = null; return IGStatus.InvalidArg; }
        *outCodecApi = _codecApi;
        return IGStatus.OK;
    }


    // ------------------------------ Codec API callbacks ------------------------------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static IGStatus CodecGetCapability(IGCodecCapability* outCap)
    {
        if (outCap == null) return IGStatus.InvalidArg;
        *outCap = BuildCapability();
        return IGStatus.OK;
    }


    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CodecCanHandleExtension(IGStringRef ext)
    {
        if (ext.Data == null || ext.Length <= 0) return 0;

        var s = new ReadOnlySpan<char>(ext.Data, ext.Length);
        foreach (var supported in SupportedExtensions)
        {
            if (s.Equals(supported, StringComparison.OrdinalIgnoreCase)) return 1;
        }
        return 0;
    }


    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CodecCanHandleSignature(byte* signature, int length)
    {
        if (signature == null || length < 8) return 0;
        return new ReadOnlySpan<byte>(signature, 8).SequenceEqual(PngSignature) ? 1 : 0;
    }


    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static IGStatus CodecLoadMetadata(IGStringRef filePath, IGImageInfo* outInfo, void* cancellation)
    {
        if (outInfo == null) return IGStatus.InvalidArg;
        *outInfo = default;

        if (!TryGetPath(filePath, out var path)) return IGStatus.InvalidArg;
        if (HostChannel.IsCanceled(cancellation)) return IGStatus.Canceled;

        var document = ApngDocumentCache.Acquire(path, out var status);
        if (document is null) return status;

        try
        {
            outInfo->Width = document.Width;
            outInfo->Height = document.Height;
            outInfo->PixelFormat = (int)IGPixelFormat.Bgra8Unorm;
            outInfo->HasAlpha = document.HasAlpha ? 1 : 0;
            outInfo->HdrTransferFn = (int)IGHdrTransferFn.None;
            outInfo->ColorSpace = (int)document.ColorSpace;
            outInfo->Orientation = 1;
            outInfo->FrameCount = document.FrameCount;
            outInfo->FileSizeBytes = document.FileSizeBytes;

            if (document.IccProfile is { Length: > 0 } icc)
            {
                var block = PublishIccProfile(icc);
                if (block != null)
                {
                    outInfo->IccProfileData = block;
                    outInfo->IccProfileSize = icc.Length;
                }
            }

            return IGStatus.OK;
        }
        finally
        {
            ApngDocumentCache.Release(document);
        }
    }


    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static IGStatus CodecDecodeStaticRaster(IGStringRef filePath, int frameIndex,
        IGPixelBuffer* outBuf, void* cancellation)
    {
        return DecodeFrame(filePath, frameIndex, outBuf, cancellation);
    }


    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static IGStatus CodecDecodeAnimationFrame(IGStringRef filePath, int frameIndex,
        IGPixelBuffer* outBuf, void* cancellation)
    {
        return DecodeFrame(filePath, frameIndex, outBuf, cancellation);
    }


    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static IGStatus CodecGetAnimationInfo(IGStringRef filePath, IGAnimationInfo* outInfo, void* cancellation)
    {
        if (outInfo == null) return IGStatus.InvalidArg;
        *outInfo = default;

        if (!TryGetPath(filePath, out var path)) return IGStatus.InvalidArg;
        if (HostChannel.IsCanceled(cancellation)) return IGStatus.Canceled;

        var document = ApngDocumentCache.Acquire(path, out var status);
        if (document is null) return status;

        try
        {
            var count = document.FrameCount;
            var frames = (IGAnimationFrameInfo*)NativeMemory.Alloc(
                (nuint)(sizeof(IGAnimationFrameInfo) * count));
            if (frames == null) return IGStatus.OutOfMemory;

            for (var i = 0; i < count; i++)
            {
                frames[i] = new IGAnimationFrameInfo
                {
                    DurationMs = document.GetFrameDurationMs(i),
                    HasAlpha = document.HasAlpha ? 1 : 0,
                };
            }

            outInfo->FrameCount = count;
            outInfo->LoopCount = document.LoopCount;
            outInfo->Frames = frames;

            lock (_bufLock)
            {
                _liveFrameArrays.Add((nint)frames);
            }
            return IGStatus.OK;
        }
        finally
        {
            ApngDocumentCache.Release(document);
        }
    }


    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void CodecFreeAnimationInfo(IGAnimationInfo* info)
    {
        if (info == null || info->Frames == null) return;

        var key = (nint)info->Frames;
        lock (_bufLock)
        {
            if (!_liveFrameArrays.Remove(key)) return;
        }

        NativeMemory.Free((void*)key);
        info->Frames = null;
        info->FrameCount = 0;
    }


    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void CodecFreePixelBuffer(IGPixelBuffer* buf)
    {
        if (buf == null || buf->Data == null) return;

        var key = (nint)buf->Data;
        lock (_bufLock)
        {
            // Remove-first also guards against a double free.
            if (!_liveBuffers.Remove(key)) return;
        }

        NativeMemory.Free((void*)key);
        buf->Data = null;
        buf->ReleaseContext = null;
    }


    // ------------------------------ Decode pipeline ------------------------------

    /// <summary>
    /// Composes one frame into a freshly allocated native buffer. Shared by the
    /// static-raster and animation entry points: both return the same fully composed
    /// canvas, the animation path simply gets called per frame by the host animator.
    /// </summary>
    private static IGStatus DecodeFrame(IGStringRef filePath, int frameIndex,
        IGPixelBuffer* outBuf, void* cancellation)
    {
        if (outBuf == null) return IGStatus.InvalidArg;
        *outBuf = default;

        if (!TryGetPath(filePath, out var path)) return IGStatus.InvalidArg;
        if (HostChannel.IsCanceled(cancellation)) return IGStatus.Canceled;

        var document = ApngDocumentCache.Acquire(path, out var status);
        if (document is null) return status;

        try
        {
            if (frameIndex < 0 || frameIndex >= document.FrameCount) return IGStatus.InvalidArg;

            var size = document.FrameByteSize;
            var pixels = (byte*)NativeMemory.Alloc((nuint)size);
            if (pixels == null) return IGStatus.OutOfMemory;

            IGStatus composeStatus;
            try
            {
                composeStatus = document.CopyFrameTo(frameIndex, pixels, cancellation);
            }
            catch (Exception ex)
            {
                HostChannel.Log(4, $"ApngCodec: frame {frameIndex} of '{Path.GetFileName(path)}' failed ({ex.Message}).");
                composeStatus = IGStatus.Internal;
            }

            // The host only calls FreePixelBuffer for calls that returned OK.
            if (composeStatus != IGStatus.OK)
            {
                NativeMemory.Free(pixels);
                return composeStatus;
            }

            outBuf->Data = pixels;
            outBuf->Width = document.Width;
            outBuf->Height = document.Height;
            outBuf->Stride = document.Width * 4;
            outBuf->PixelFormat = (int)IGPixelFormat.Bgra8Unorm;
            outBuf->ReleaseContext = pixels;

            lock (_bufLock)
            {
                _liveBuffers.Add((nint)pixels);
            }
            return IGStatus.OK;
        }
        finally
        {
            ApngDocumentCache.Release(document);
        }
    }


    // ------------------------------ Helpers ------------------------------

    /// <summary>
    /// Copies ICC bytes into the next ring slot, freeing whatever occupied it before.
    /// Returns <c>null</c> when the allocation fails.
    /// </summary>
    private static byte* PublishIccProfile(byte[] icc)
    {
        lock (_iccLock)
        {
            var slot = _iccSlot;
            _iccSlot = (_iccSlot + 1) % ICC_RING_SIZE;

            if (_iccRing[slot] != 0)
            {
                NativeMemory.Free((void*)_iccRing[slot]);
                _iccRing[slot] = 0;
            }

            var block = (byte*)NativeMemory.Alloc((nuint)icc.Length);
            if (block == null) return null;

            icc.AsSpan().CopyTo(new Span<byte>(block, icc.Length));
            _iccRing[slot] = (nint)block;
            return block;
        }
    }


    private static bool TryGetPath(IGStringRef filePath, out string path)
    {
        if (filePath.Data == null || filePath.Length <= 0)
        {
            path = string.Empty;
            return false;
        }

        path = new string(filePath.Data, 0, filePath.Length);
        return true;
    }


    private static IGCodecCapability BuildCapability()
    {
        return new IGCodecCapability
        {
            CodecId = MakeStringRef(_bufCodecId, CodecIdString.Length),
            CodecName = MakeStringRef(_bufCodecName, CodecNameString.Length),

            // ".apng" is a built-in format, so the host clamps these below the built-in
            // ceiling unless the plugin's trust entry sets AllowOverrideBuiltins.
            MetadataPriority = 200,
            DecodePriority = 200,

            SupportsMetadata = 1,
            SupportsStaticRaster = 1,
            SupportsColorProfiles = 1,
            SupportsAnimation = 1,

            ExtensionCount = SupportedExtensions.Length,
            Extensions = _extArray,
        };
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static IGStringRef MakeStringRef(char* data, int len) => new() { Data = data, Length = len };


    private static void InitStrings()
    {
        _bufPluginId = AllocUtf16(PluginIdString);
        _bufPluginName = AllocUtf16(PluginNameString);
        _bufVersion = AllocUtf16(VersionString);
        _bufCodecId = AllocUtf16(CodecIdString);
        _bufCodecName = AllocUtf16(CodecNameString);

        var count = SupportedExtensions.Length;
        _bufExtensions = (char**)NativeMemory.AllocZeroed((nuint)(sizeof(nint) * count));
        _extArray = (IGStringRef*)NativeMemory.AllocZeroed((nuint)(sizeof(IGStringRef) * count));
        for (var i = 0; i < count; i++)
        {
            var ext = SupportedExtensions[i];
            _bufExtensions[i] = AllocUtf16(ext);
            _extArray[i] = MakeStringRef(_bufExtensions[i], ext.Length);
        }
    }


    private static void InitCodecApi()
    {
        _codecApi = (IGCodecApi*)NativeMemory.AllocZeroed((nuint)sizeof(IGCodecApi));
        _codecApi->GetCapability = &CodecGetCapability;
        _codecApi->CanHandleExtension = &CodecCanHandleExtension;
        _codecApi->CanHandleSignature = &CodecCanHandleSignature;
        _codecApi->LoadMetadata = &CodecLoadMetadata;
        _codecApi->DecodeStaticRaster = &CodecDecodeStaticRaster;
        _codecApi->FreePixelBuffer = &CodecFreePixelBuffer;

        // All three must be non-null or the host downgrades the codec to static-raster only.
        _codecApi->GetAnimationInfo = &CodecGetAnimationInfo;
        _codecApi->FreeAnimationInfo = &CodecFreeAnimationInfo;
        _codecApi->DecodeAnimationFrame = &CodecDecodeAnimationFrame;
    }


    private static void InitPluginApi()
    {
        _pluginApi = (IGPluginApi*)NativeMemory.AllocZeroed((nuint)sizeof(IGPluginApi));
        _pluginApi->StructSize = sizeof(IGPluginApi);
        _pluginApi->AbiVersion = IGNativeAbi.IG_PLUGIN_ABI_VERSION;
        _pluginApi->Info = new IGPluginInfo
        {
            PluginId = MakeStringRef(_bufPluginId, PluginIdString.Length),
            Name = MakeStringRef(_bufPluginName, PluginNameString.Length),
            Version = MakeStringRef(_bufVersion, VersionString.Length),
            AbiVersion = IGNativeAbi.IG_PLUGIN_ABI_VERSION,
            CodecCount = 1,
        };
        _pluginApi->GetCodec = &OnGetCodec;
        _pluginApi->Initialize = &OnInitialize;
        _pluginApi->Shutdown = &OnShutdown;
        _pluginApi->SelfTest = null;
    }


    private static char* AllocUtf16(string s)
    {
        var buf = (char*)NativeMemory.Alloc((nuint)((s.Length + 1) * sizeof(char)));
        for (var i = 0; i < s.Length; i++) buf[i] = s[i];
        buf[s.Length] = '\0';
        return buf;
    }
}
