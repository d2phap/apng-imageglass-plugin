# APNG Codec for ImageGlass

Plays **animated PNG** files in [ImageGlass](https://imageglass.org) 10. This plugin reads the APNG frame stream,
composites every frame according to the format's blend and disposal rules, and hands the host
a ready-to-display canvas with the right delay for each one.

Chunk parsing comes from [APNG.NET](https://github.com/xupefei/APNG.NET), pixel decoding from
[SkiaSharp](https://github.com/mono/SkiaSharp), and the whole thing is compiled with Native
AOT for Windows x64/ARM64, Linux x64 and macOS ARM64.


## Supported Formats

| | |
|---|---|
| **Read** | `.apng` `.png` |
| **Write** | none – this is a decoder |

The plugin claims **both** extensions, because many animated PNGs ship with a plain `.png`
name. That means it becomes the decoder for every PNG you open, animated or not. To leave
ordinary PNGs to the built-in decoder, open the plugin from **Settings → Plugins** (click its
name) and untick **Decode** for `.png`; the change takes effect immediately and survives
disabling and re-enabling the plugin.

Within those extensions it handles per-frame delays and the `acTL` loop count,
`APNG_BLEND_OP_SOURCE` / `_OVER` combined with `APNG_DISPOSE_OP_NONE` / `_BACKGROUND` /
`_PREVIOUS`, straight-alpha output so semi-transparent edges keep no dark halo, the embedded
ICC profile (`iCCP`) so ImageGlass can colour-manage the result, and still PNGs renamed to
`.apng`, which load as ordinary single-frame images.

Two things it does not do: HDR – no HDR transfer function is reported, so a PQ/HLG PNG
renamed to `.apng` is treated as SDR – and 16-bit frames, which are decoded down to 8 bits
per channel.


## Install

1. Download `apng-codec_<version>_win-x64.igplugin.zip` (or `win-arm64`, `linux-x64`,
   `mac-arm64`) from [Releases](https://github.com/d2phap/apng-imageglass-plugin/releases).
2. ImageGlass → **Settings → Plugins → Add**, and pick the `.igplugin.zip`.
3. Click **Trust and enable**.

Open an APNG afterwards and the title bar should show a frame counter such as
`29/40 frame(s)`.


## Build

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) and a native AOT toolchain:
the "Desktop development with C++" workload on Windows, `clang` + zlib dev packages on Linux,
Xcode command line tools on macOS. Native AOT does not cross-compile between operating
systems, so each target is built on the OS it runs on.

```powershell
# compile only - quick syntax check
dotnet build source/ApngCodec.csproj -c Debug -p:Platform=x64

# publish + pack this OS's targets into dist/apng-codec_<version>_<arch>.igplugin.zip
./pack.ps1

# publish x64 and drop it straight into %LOCALAPPDATA%\ImageGlass\_plugins
./pack.ps1 -Rid win-x64 -Deploy
```

The package version comes from `version` in `source/igplugin.json` – the one ImageGlass shows
in Settings > Plugins. `.vscode/tasks.json` wraps the same commands for **Terminal → Run
Task**, plus a publish task per target.


## License

MIT. See [LICENSE](LICENSE).

The vendored APNG.NET sources under `source/LibAPNG/` are MIT by Amemiya – see
`source/LibAPNG/LICENSE.txt`. They are unmodified apart from a provenance header and a
`#nullable disable` directive.
