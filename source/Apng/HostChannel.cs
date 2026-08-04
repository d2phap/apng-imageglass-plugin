/*
ImageGlass APNG Codec Plugin
Copyright (C) 2026 DUONG DIEU PHAP
MIT License
*/
using System.Runtime.CompilerServices;
using ImageGlass.SDK.Plugins;

namespace ApngCodec.Apng;

/// <summary>
/// Thin wrapper over the host services the plugin uses (log channel and the opaque
/// cancellation token). The host table is stashed once at plugin load.
/// </summary>
internal static unsafe class HostChannel
{
    private static IGHostApi* _hostApi;


    /// <summary>
    /// Stores the host API table handed to the plugin entry point.
    /// </summary>
    public static void Attach(IGHostApi* hostApi) => _hostApi = hostApi;


    /// <summary>
    /// Polls the host for cancellation of the given opaque token.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsCanceled(void* cancellation)
    {
        if (cancellation == null || _hostApi == null || _hostApi->Core == null) return false;

        var fn = _hostApi->Core->IsCancellationRequested;
        if (fn == null) return false;

        return fn(cancellation) != 0;
    }


    /// <summary>
    /// Sends a UTF-16 message to the host's plugin log channel.
    /// Levels: 0=trace, 1=debug, 2=info, 3=warn, 4=error.
    /// </summary>
    public static void Log(int level, string message)
    {
        if (_hostApi == null || _hostApi->Core == null) return;

        var fn = _hostApi->Core->Log;
        if (fn == null) return;

        fixed (char* pMsg = message)
        {
            fn(level, new IGStringRef { Data = pMsg, Length = message.Length });
        }
    }
}
