#!/usr/bin/env python3
"""Convert macOS asset PNGs into multi-resolution ICO files for the installer."""

import argparse
import struct
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, List, Tuple

from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_APPICON_SRC = ROOT / "src" / "App" / "Assets" / "Brand" / "Assets.xcassets" / "AppIcon.appiconset"
DEFAULT_CLOP_SRC = ROOT / "src" / "App" / "Assets" / "Brand" / "Assets.xcassets" / "clop.imageset"
DEFAULT_OUTPUT_DIR = ROOT / "installer" / "assets"
DEFAULT_APPICON_DEST = DEFAULT_OUTPUT_DIR / "Clop.ico"
DEFAULT_CLOP_DEST = DEFAULT_OUTPUT_DIR / "ClopMark.ico"


@dataclass
class IconFrame:
    width: int
    height: int
    bit_count: int
    data: bytes


def load_square_pngs(folder: Path) -> List[IconFrame]:
    if not folder.exists():
        raise FileNotFoundError(f"Missing asset folder: {folder}")

    size_map: Dict[Tuple[int, int], IconFrame] = {}
    for png_path in sorted(folder.glob("*.png")):
        with Image.open(png_path) as img:
            image = img.convert("RGBA")
            if image.width != image.height:
                print(f"Skipping non-square asset {png_path.name} ({image.width}x{image.height}).")
                continue

            key = (image.width, image.height)
            bit_count = 32 if "A" in image.getbands() else 24
            frame = IconFrame(
                width=image.width,
                height=image.height,
                bit_count=bit_count,
                data=png_path.read_bytes(),
            )
            size_map[key] = frame

    if not size_map:
        raise ValueError(f"No square PNGs found in {folder}")

    return [size_map[key] for key in sorted(size_map.keys())]


def write_ico(images: List[IconFrame], destination: Path) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)

    ordered = sorted(images, key=lambda frame: frame.width)
    header = bytearray()
    header.extend(struct.pack("<HHH", 0, 1, len(ordered)))

    data_chunks: List[bytes] = []
    offset = 6 + 16 * len(ordered)
    for frame in ordered:
        width_byte = frame.width if frame.width < 256 else 0
        height_byte = frame.height if frame.height < 256 else 0
        header.extend(
            struct.pack(
                "<BBBBHHII",
                width_byte,
                height_byte,
                0,
                0,
                1,
                frame.bit_count,
                len(frame.data),
                offset,
            )
        )
        data_chunks.append(frame.data)
        offset += len(frame.data)

    with destination.open("wb") as ico_file:
        ico_file.write(header)
        for chunk in data_chunks:
            ico_file.write(chunk)


def convert_folder(source: Path, destination: Path) -> None:
    print(f"Converting {source} -> {destination}")
    images = load_square_pngs(source)
    write_ico(images, destination)
    print(f"Wrote {destination}")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Convert asset catalogs into ICO files.")
    parser.add_argument("--appicon-src", type=Path, default=DEFAULT_APPICON_SRC, help="Path to AppIcon.appiconset")
    parser.add_argument("--appicon-dest", type=Path, default=DEFAULT_APPICON_DEST, help="Destination ICO path for the full icon")
    parser.add_argument("--clop-src", type=Path, default=DEFAULT_CLOP_SRC, help="Path to clop.imageset")
    parser.add_argument("--clop-dest", type=Path, default=DEFAULT_CLOP_DEST, help="Destination ICO path for the glyph icon")
    parser.add_argument("--skip-appicon", action="store_true", help="Skip converting the AppIcon assets")
    parser.add_argument("--skip-clop", action="store_true", help="Skip converting the clop assets")
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    tasks = []

    if not args.skip_appicon:
        tasks.append((args.appicon_src, args.appicon_dest))
    if not args.skip_clop:
        tasks.append((args.clop_src, args.clop_dest))

    if not tasks:
        raise SystemExit("No conversion targets were scheduled.")

    for source, destination in tasks:
        convert_folder(source, destination)


if __name__ == "__main__":
    main()
