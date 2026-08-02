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
| Linux | `apng-codec_linux-64.igplugin.zip` |
| macOS (Apple Silicon) | `apng-codec_mac-arm64.igplugin.zip` |

In ImageGlass, open **Settings > Plugins > Add** and pick the `.igplugin.zip`, then enable
**APNG Codec** in the same list. A newly installed plugin stays untrusted (and unloaded)
until you enable it; enabling pins the library's SHA-256. Finally apply the extra step
below and restart ImageGlass.

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
