# APNG Codec for ImageGlass

An **animated PNG (`.apng`) codec plugin** for [ImageGlass](https://imageglass.org) 10.

ImageGlass shows an APNG file as a single still frame out of the box. This plugin decodes
the whole animation: it reads the APNG frame stream, composites each frame according to the
format's blend and disposal rules, and hands ImageGlass a ready-to-display canvas with the
right delay for every frame.

| | |
| --- | --- |
| **Extension** | `.apng` |
| **Plugin id** | `Plugin_ApngCodec` |
| **Platforms** | Windows, Linux, macOS (x64 and ARM64) |
| **Requires** | ImageGlass 10 |
| **License** | MIT |

## Features

- Full APNG playback: per-frame delays, `acTL` loop count, and correct compositing of
  `APNG_BLEND_OP_SOURCE` / `APNG_BLEND_OP_OVER` with `APNG_DISPOSE_OP_NONE` /
  `_BACKGROUND` / `_PREVIOUS`
- Straight-alpha output, so semi-transparent edges stay clean (no dark halo)
- Reads the embedded ICC profile (`iCCP`) so ImageGlass can colour-manage the result
- Still PNGs renamed to `.apng` load as a normal single-frame image
- Native AOT: a self-contained shared library with no .NET runtime dependency
- Cancellation-aware, thread-safe, and memory-bounded (a small cache of parsed files)

## Install

1. Download the release archive for your platform, or [build it yourself](#build).
2. Copy the folder into ImageGlass's `_plugins` directory:

   | Platform | Config directory |
   | --- | --- |
   | Windows | `%LocalAppData%\ImageGlass` |
   | Linux | `~/.local/share/ImageGlass` |
   | macOS | `~/Library/Application Support/ImageGlass` |

   ```text
   %LOCALAPPDATA%\ImageGlass\_plugins\Plugin_ApngCodec\
       ApngCodec.dll
       igplugin.json
       libSkiaSharp.dll
   ```

3. Start ImageGlass and enable **APNG Codec** in **Settings > Plugins**. A newly installed
   plugin stays untrusted (and unloaded) until you do; enabling pins the library's SHA-256.
4. Apply the extra step below, then restart ImageGlass.

### Required: allow the plugin to override the built-in decoder

`.apng` is already in ImageGlass's built-in format list, so the host deliberately ranks
plugins **below** its own decoders for that extension. Until you opt in, the built-in
decoder keeps winning and you still get a still image.

Close ImageGlass (it rewrites its config on exit), open
`%LOCALAPPDATA%\ImageGlass\igconfig.json`, and add `AllowOverrideBuiltins` to this plugin's
trust entry:

```jsonc
"PluginTrust": {
  "Plugin_ApngCodec": {
    "Enabled": true,
    "Hash": "…",              // written for you when you enable the plugin
    "AllowOverrideBuiltins": true
  }
}
```

Then start ImageGlass and open an APNG. The title bar should show a frame counter such as
`29/40 frame(s)` and the image should animate. If there is no frame counter, the override
did not take effect.

> Toggling the plugin off and on again in **Settings > Plugins** rewrites the trust entry
> and resets `AllowOverrideBuiltins` to `false`. Re-apply it after re-enabling, or after
> replacing the library with a new build.

### Also want plain `.png` APNG files?

Many animated PNGs are published with the `.png` extension, which the built-in decoder
owns. To route those here too, add a `supportedExtensions` override to `igplugin.json`:

```jsonc
"supportedExtensions": ".apng;.png"
```

This makes the plugin handle **every** PNG, not just animated ones, so only do it if you
want that.

## Build

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) and a native AOT
toolchain (on Windows, the "Desktop development with C++" workload).

```powershell
# Windows x64
dotnet publish source/ApngCodec.csproj -c Release -r win-x64 -p:Platform=x64 -o dist/win-x64
```

```bash
# Linux x64
dotnet publish source/ApngCodec.csproj -c Release -r linux-x64 -p:Platform=x64 -o dist/linux-x64

# macOS Apple Silicon
dotnet publish source/ApngCodec.csproj -c Release -r osx-arm64 -p:Platform=ARM64 -o dist/osx-arm64
```

The output folder is the plugin folder: `ApngCodec.dll` (or `.so` / `.dylib`),
`igplugin.json`, and the `libSkiaSharp` native asset. Copy the whole folder to `_plugins`.

> `Platforms` is `x64;ARM64`, so `-p:Platform=…` is required — a plain `dotnet build` with
> the default `AnyCPU` will not configure.

## How it works

ImageGlass loads codec plugins **in-process** through the C ABI defined by the
[ImageGlass SDK](https://github.com/ImageGlass/SDK), consumed here as the
[`ImageGlass.SDK`](https://www.nuget.org/packages/ImageGlass.SDK) NuGet package. The plugin
exports one function, `ig_plugin_get_api`, and returns function-pointer tables for the
codec.

| File | Role |
| --- | --- |
| `source/ApngCodecPlugin.cs` | The ABI surface: entry point, API tables, codec callbacks, native buffer ownership |
| `source/Apng/ApngDocument.cs` | Parses one file and replays the canvas frame by frame |
| `source/Apng/ApngDocumentCache.cs` | Reference-counted LRU cache of parsed files |
| `source/Apng/PngChunkScanner.cs` | Reads `iCCP` / `sRGB` / `tRNS` straight from the chunk stream |
| `source/Apng/HostChannel.cs` | Host log channel and cancellation polling |
| `source/LibAPNG/` | Vendored [APNG.NET](https://github.com/xupefei/APNG.NET) chunk parser |

### Frame composition

The host requires every decoded frame to be a **fully composed** canvas: it does no
sub-rect composition and no disposal replay. APNG, however, stores each frame as a
sub-rectangle with its own blend and disposal operation, so the plugin does that work:

1. Rebuild a standalone PNG for the frame's sub-rectangle (APNG.NET re-wraps the `fdAT`
   payload as `IDAT` and patches `IHDR` to the frame size).
2. Decode it with SkiaSharp to straight-alpha BGRA.
3. Merge it into the canvas — `SOURCE` overwrites the rectangle alpha and all, `OVER`
   alpha-composites.
4. Copy the canvas out as the frame ImageGlass displays.
5. Apply the disposal op to prepare the canvas for the next frame — `BACKGROUND` clears the
   rectangle, `PREVIOUS` restores the snapshot taken before step 3.

Because frame *N* depends on frame *N-1*, the canvas keeps its playhead: a sequential
request continues from where it left off, and a backwards request replays from frame 0.

## Limitations

- **SDR only.** The plugin reports no HDR transfer function, so a PQ/HLG PNG renamed to
  `.apng` would be treated as SDR.
- 16-bit PNG frames are decoded down to 8 bits per channel.
- The parsed-file cache holds up to 4 files; each keeps one full canvas
  (`width x height x 4` bytes), plus a second one for files that use
  `dispose_op = PREVIOUS`.

## Credits

- Chunk parsing uses [APNG.NET](https://github.com/xupefei/APNG.NET) by Amemiya (MIT),
  vendored under `source/LibAPNG/` — see `source/LibAPNG/LICENSE.txt`. The files are
  unmodified apart from a provenance header and a `#nullable disable` directive.
- Pixel decoding uses [SkiaSharp](https://github.com/mono/SkiaSharp).

## License

MIT — see [LICENSE](LICENSE).
