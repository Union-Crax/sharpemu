"""Regenerate backed and transparent Hyper5 assets from a square RGBA master."""

from __future__ import annotations

import argparse
from pathlib import Path

from PIL import Image, ImageColor


PNG_SIZES = (16, 20, 24, 32, 40, 48, 64, 96, 128, 256, 512, 1024)
ICO_SIZES = (16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
ICNS_SIZES = (16, 32, 64, 128, 256, 512, 1024)


def resized(master: Image.Image, size: int) -> Image.Image:
    # Resize in premultiplied-alpha space so transparent pixels cannot create
    # dark fringes around the already-white artwork.
    return master.convert("RGBa").resize(
        (size, size), Image.Resampling.LANCZOS
    ).convert("RGBA")


def with_background(image: Image.Image, background: str) -> Image.Image:
    background_rgba = (*ImageColor.getrgb(background), 255)
    result = Image.new("RGBA", image.size, background_rgba)
    result.alpha_composite(image)
    return result


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("master", type=Path, help="Path to the square Hyper5 RGBA master PNG")
    parser.add_argument(
        "--output-dir",
        type=Path,
        default=Path("assets/images"),
        help="Asset output directory (default: assets/images)",
    )
    parser.add_argument(
        "--background",
        default="#121212",
        help="Opaque icon background color (default: #121212)",
    )
    args = parser.parse_args()

    with Image.open(args.master) as source:
        master = source.convert("RGBA")

    if master.width != master.height:
        raise ValueError(f"Master image must be square, got {master.width}x{master.height}")

    output_dir = args.output_dir
    backed_png_dir = output_dir / "hyper5"
    transparent_png_dir = output_dir / "hyper5-transparent"
    backed_png_dir.mkdir(parents=True, exist_ok=True)
    transparent_png_dir.mkdir(parents=True, exist_ok=True)

    transparent = {size: resized(master, size) for size in PNG_SIZES}
    backed = {
        size: with_background(image, args.background)
        for size, image in transparent.items()
    }
    for size in PNG_SIZES:
        backed[size].save(
            backed_png_dir / f"hyper5-{size}.png", format="PNG", optimize=True
        )
        transparent[size].save(
            transparent_png_dir / f"hyper5-{size}.png", format="PNG", optimize=True
        )

    # Hyper5.png is consumed by the GUI splash and intentionally stays transparent.
    transparent[1024].save(output_dir / "Hyper5.png", format="PNG", optimize=True)
    transparent[1024].save(
        output_dir / "Hyper5-transparent.png", format="PNG", optimize=True
    )
    backed[1024].save(
        output_dir / "Hyper5-background.png", format="PNG", optimize=True
    )
    # README pages may use a light or dark theme, so its logo has a fixed backdrop.
    backed[1024].save(output_dir / "logo.png", format="PNG", optimize=True)
    backed[256].save(
        output_dir / "Hyper5.ico",
        format="ICO",
        sizes=[(size, size) for size in ICO_SIZES],
    )
    backed[1024].save(
        output_dir / "Hyper5.icns",
        format="ICNS",
        append_images=[backed[size] for size in ICNS_SIZES[:-1]],
    )


if __name__ == "__main__":
    main()
