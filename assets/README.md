# Brand assets

A gate arch with a portcullis inside it. The arch is the gateway; the three bars
with spear points are the portcullis being lowered — the gate deciding who
passes. One colour, no gradient, no container tile, so it survives being scaled
to a favicon and being printed in one ink.

| File | Use |
| ---- | --- |
| `icon.svg` | The mark. Square, 64-unit grid. |
| `icon-dark.svg` | The mark, light ink, for dark backgrounds. |
| `icon.png` | 128×128 raster. Packed into the NuGet package as `PackageIcon`. |
| `logo.svg` | Horizontal lock-up: mark plus wordmark. |
| `logo-dark.svg` | Lock-up, light ink, for dark backgrounds. |

Ink is `#221F2B` on light, `#F2F2F5` on dark.

The SVGs draw in `currentColor` and set a `color` fallback, so a file dropped
into a page inherits the surrounding text colour. That inheritance does not
survive being loaded through an `<img>` tag, which is how GitHub renders README
images — hence the separate `-dark` files rather than one clever one. They are
generated from their light counterparts by substituting the `color` value, so
edit the light file and regenerate:

    for f in icon logo; do
      sed 's|color="#221F2B"|color="#F2F2F5"|' assets/$f.svg > assets/$f-dark.svg
    done

## Regenerating `icon.png`

`icon.png` is the only committed raster, and it is committed because NuGet
requires a raster icon. It is rendered from `icon.svg` at 128×128 — NuGet's
recommended size — with [Svg.Skia](https://github.com/wieslawsoltes/Svg.Skia).
Any renderer that resolves `currentColor` will do; ImageMagick and rsvg-convert
both work:

    rsvg-convert -w 128 -h 128 assets/icon.svg -o assets/icon.png

If you change `icon.svg`, regenerate `icon.png` in the same commit. A package
icon that disagrees with the source SVG is the kind of drift nobody notices for
a year.

## Usage

These marks identify the Gatehouse project. The Apache-2.0 licence covers the
code, not the marks; see the trademark section of [../GOVERNANCE.md](../GOVERNANCE.md)
for what use is permitted. Referring to Gatehouse, linking to it, and saying your
software works with it are all fine and always will be.
