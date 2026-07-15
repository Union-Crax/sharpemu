"""Regenerate Hyper5 application image assets from a square RGBA master image."""

from __future__ import annotations

import argparse
from pathlib import Path

from PIL import Image, ImageColor


PNG_SIZES = (16, 20, 24, 32, 40, 48, 64, 96, 128, 256, 512, 1024)
ICO_SIZES = (16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
ICNS_SIZES = (16, 32, 64, 128, 256, 512, 1024)


def rendered(master: Image.Image, size: int, background: str) -> Image.Image:
    alpha = master.getchannel("A").resize((size, size), Image.Resampling.LANCZOS)
    background_rgba = (*ImageColor.getrgb(background), 255)
    image = Image.new("RGBA", (size, size), background_rgba)
    image.paste(Image.new("RGBA", (size, size), "white"), mask=alpha)
    return image


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
    png_dir = output_dir / "hyper5"
    png_dir.mkdir(parents=True, exist_ok=True)

    variants = {size: rendered(master, size, args.background) for size in PNG_SIZES}
    for size, image in variants.items():
        image.save(png_dir / f"hyper5-{size}.png", format="PNG", optimize=True)

    variants[1024].save(output_dir / "Hyper5.png", format="PNG", optimize=True)
    variants[1024].save(output_dir / "logo.png", format="PNG", optimize=True)
    variants[256].save(
        output_dir / "Hyper5.ico",
        format="ICO",
        sizes=[(size, size) for size in ICO_SIZES],
    )
    variants[1024].save(
        output_dir / "Hyper5.icns",
        format="ICNS",
        append_images=[variants[size] for size in ICNS_SIZES[:-1]],
    )


if __name__ == "__main__":
    main()
