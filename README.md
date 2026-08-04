# APNG Codec for ImageGlass

An **animated PNG (`.apng`) codec plugin** for [ImageGlass](https://imageglass.org) 10.

ImageGlass shows an APNG file as a single still frame out of the box. This plugin decodes
the whole animation: it reads the APNG frame stream, composites each frame according to the
format's blend and disposal rules, and hands ImageGlass a ready-to-display canvas with the
right delay for every frame.

| | |
| --- | --- |
| **Extensions** | `.apng`, `.png` (either can be switched off in Settings > Plugins) |
| **Plugin id** | `Plugin_ApngCodec` |
| **Platforms** | Windows (x64, ARM64), Linux (x64), macOS (ARM64) |
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

Grab the package for your platform from the [latest release](https://github.com/d2phap/apng-imageglass-plugin/releases),
or [build it yourself](#build):

| Platform | Package |
| --- | --- |
| Windows (Intel/AMD) | `apng-codec_win-x64.igplugin.zip` |
| Windows (ARM) | `apng-codec_win-arm64.igplugin.zip` |
| Linux | `apng-codec_linux-x64.igplugin.zip` |
| macOS (Apple Silicon) | `apng-codec_mac-arm64.igplugin.zip` |

In ImageGlass, open **Settings > Plugins > Add** and pick the `.igplugin.zip`, then enable
**APNG Codec** in the same list. A newly installed plugin stays untrusted (and unloaded)
until you enable it; enabling pins the library's SHA-256.

That is the whole install. Enabling a plugin is an act of trust, so ImageGlass honours the
priority this codec reports and lets it take `.apng` from the built-in decoder. No config
editing is needed. Open an APNG and the title bar should show a frame counter such as
`29/40 frame(s)`.

### Choosing which formats it handles

The plugin claims **both `.apng` and `.png`**, because many animated PNGs ship with a plain
`.png` extension. That means it becomes the decoder for every PNG you open, animated or not.

If you would rather leave ordinary PNGs to the built-in decoder, open the plugin from
**Settings > Plugins** (click its name) and untick **Decode** for `.png`. The change takes
effect immediately, and it survives disabling and re-enabling the plugin.

## Build

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) and a native AOT
toolchain: the "Desktop development with C++" workload on Windows, `clang` + zlib dev
packages on Linux, Xcode command line tools on macOS.

Targets match the platforms ImageGlass ships on: **win-x64**, **win-arm64**, **linux-x64**
and **osx-arm64**.

### In VS Code

`.vscode/tasks.json` has a task per target — **Terminal > Run Task**, then pick one:

| Task | Output |
| --- | --- |
| `publish: Windows x64` / `publish: Windows ARM64` | `dist/win-x64`, `dist/win-arm64` |
| `publish: Windows (all)` | both Windows architectures |
| `publish: Linux x64` | `dist/linux-x64` |
| `publish: macOS ARM64` | `dist/osx-arm64` |
| `build` (default build task) | compile-only check, no native AOT |

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
