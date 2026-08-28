#!/usr/bin/env python3
"""Verify YTray application icons and the approved website brand artwork."""

from __future__ import annotations

import hashlib
import struct
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / "assets/app-icon/YTray.png"
WINDOWS = ROOT / "windows/src/Assets/Icons"
SITE_APP = ROOT / "site/src/app"
SITE_PUBLIC = ROOT / "site/public/icons"
SITE_BRAND = ROOT / "site/public/brand"


def fail(message: str) -> None:
    raise AssertionError(message)


def png_dimensions(path: Path) -> tuple[int, int]:
    data = path.read_bytes()
    if data[:8] != b"\x89PNG\r\n\x1a\n" or data[12:16] != b"IHDR":
        fail(f"not a PNG: {path.relative_to(ROOT)}")
    width, height, bit_depth, color_type = struct.unpack(">IIBB", data[16:26])
    if bit_depth != 8 or color_type not in (4, 6):
        fail(f"PNG must be 8-bit with alpha: {path.relative_to(ROOT)}")
    return width, height


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def require_png(path: Path, size: int) -> None:
    if not path.is_file():
        fail(f"missing PNG: {path.relative_to(ROOT)}")
    if png_dimensions(path) != (size, size):
        fail(f"wrong PNG dimensions: {path.relative_to(ROOT)}")


def require_safe_svg(path: Path) -> None:
    if not path.is_file():
        fail(f"missing SVG: {path.relative_to(ROOT)}")
    try:
        root = ET.fromstring(path.read_text(encoding="utf-8"))
    except ET.ParseError as error:
        fail(f"invalid SVG in {path.relative_to(ROOT)}: {error}")
    if root.tag.rsplit("}", 1)[-1] != "svg":
        fail(f"unexpected SVG root in {path.relative_to(ROOT)}")
    if root.attrib.get("width") != "1024" or root.attrib.get("height") != "1024":
        fail(f"website SVG must be 1024px: {path.relative_to(ROOT)}")
    if root.attrib.get("viewBox") != "0 0 1024 1024":
        fail(f"unexpected website SVG viewBox: {path.relative_to(ROOT)}")

    blocked_tags = {"script", "foreignObject", "image", "use"}
    for element in root.iter():
        if element.tag.rsplit("}", 1)[-1] in blocked_tags:
            fail(f"unsafe SVG element in {path.relative_to(ROOT)}: {element.tag}")
        for attribute, value in element.attrib.items():
            if attribute.rsplit("}", 1)[-1] == "href" or "javascript:" in value.lower():
                fail(f"unsafe SVG reference in {path.relative_to(ROOT)}")


def ico_sizes(path: Path) -> list[int]:
    data = path.read_bytes()
    if len(data) < 6:
        fail(f"truncated ICO: {path.relative_to(ROOT)}")
    reserved, kind, count = struct.unpack_from("<HHH", data)
    if reserved != 0 or kind != 1 or count < 1 or len(data) < 6 + count * 16:
        fail(f"invalid ICO header: {path.relative_to(ROOT)}")
    sizes: list[int] = []
    for index in range(count):
        width, height = struct.unpack_from("BB", data, 6 + index * 16)
        width = width or 256
        height = height or 256
        if width != height:
            fail(f"non-square ICO frame: {path.relative_to(ROOT)}")
        sizes.append(width)
    return sizes


def require_text(path: Path, needle: str) -> None:
    if needle not in path.read_text(encoding="utf-8"):
        fail(f"missing expected icon reference in {path.relative_to(ROOT)}: {needle}")


def main() -> int:
    require_png(SOURCE, 1024)
    source_hash = sha256(SOURCE)
    digest_file = ROOT / "assets/app-icon/YTray.png.sha256"
    expected_source_hash, digest_name = digest_file.read_text(encoding="utf-8").split()
    if digest_name != "YTray.png":
        fail(f"unexpected canonical source name in {digest_file.relative_to(ROOT)}")
    if source_hash != expected_source_hash:
        fail(f"canonical source hash changed: {source_hash}")

    app_sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256, 512, 1024]
    tray_sizes = [16, 20, 24, 32, 40, 48, 64]
    for size in app_sizes:
        require_png(WINDOWS / f"png/app/ytray-app-{size}.png", size)
    if sha256(WINDOWS / "png/app/ytray-app-1024.png") != source_hash:
        fail("Windows 1024px icon does not match the canonical source")

    for theme in ("tray-on-light", "tray-on-dark"):
        for size in tray_sizes:
            path = WINDOWS / f"png/{theme}/ytray-{theme}-{size}.png"
            require_png(path, size)
            if sha256(path) != sha256(WINDOWS / f"png/app/ytray-app-{size}.png"):
                fail(f"Windows {theme} {size}px icon drifted from the app icon")

    expected_icos = {
        WINDOWS / "ytray-app.ico": [16, 20, 24, 32, 40, 48, 64, 96, 128, 256],
        WINDOWS / "ytray-tray-on-light.ico": tray_sizes,
        WINDOWS / "ytray-tray-on-dark.ico": tray_sizes,
        SITE_APP / "favicon.ico": [16, 32, 48],
    }
    for path, expected in expected_icos.items():
        actual = ico_sizes(path)
        if actual != expected:
            fail(f"wrong ICO frames in {path.relative_to(ROOT)}: {actual} != {expected}")

    require_png(SITE_APP / "icon.png", 1024)
    require_png(SITE_APP / "apple-icon.png", 180)
    require_png(SITE_PUBLIC / "icon-192.png", 192)
    require_png(SITE_PUBLIC / "icon-512.png", 512)
    if sha256(SITE_APP / "icon.png") != source_hash:
        fail("website icon.png does not match the canonical source")

    website_digests: dict[str, str] = {}
    for line in (SITE_BRAND / "SHA256SUMS").read_text(encoding="utf-8").splitlines():
        digest, name = line.split()
        website_digests[name] = digest
    expected_website_assets = {"ytray-flat.png", "ytray-vector.svg"}
    if set(website_digests) != expected_website_assets:
        fail("unexpected website brand checksum manifest")
    require_png(SITE_BRAND / "ytray-flat.png", 1024)
    require_safe_svg(SITE_BRAND / "ytray-vector.svg")
    for name, expected_hash in website_digests.items():
        if sha256(SITE_BRAND / name) != expected_hash:
            fail(f"website brand artwork changed: site/public/brand/{name}")

    for obsolete in (
        ROOT / "darwin/Resources/YTrayAppIcon.svg",
        WINDOWS / "ytray-app-icon.svg",
        WINDOWS / "ytray-tray-on-light.svg",
        WINDOWS / "ytray-tray-on-dark.svg",
        SITE_APP / "icon.svg",
    ):
        if obsolete.exists():
            fail(f"obsolete icon source still exists: {obsolete.relative_to(ROOT)}")

    require_text(ROOT / "script/package-macos.sh", "assets/app-icon/YTray.png")
    brand_component = ROOT / "site/src/components/site/brand-mark.tsx"
    require_text(brand_component, 'vector: "/brand/ytray-vector.svg"')
    require_text(brand_component, 'flat: "/brand/ytray-flat.png"')
    require_text(brand_component, 'material: "/icon.png"')
    require_text(ROOT / "site/src/app/manifest.ts", "/ytray/icons/icon-192.png")
    require_text(ROOT / "site/src/app/manifest.ts", "/ytray/icons/icon-512.png")

    showcase_source = ROOT / "docs/images/v0.1.2"
    showcase_output = ROOT / "docs/images/v0.1.4"
    for source_image in sorted(showcase_source.glob("ytray-windows-*.png")):
        output_image = showcase_output / source_image.name
        if not output_image.is_file():
            fail(f"missing v0.1.4 website showcase: {output_image.relative_to(ROOT)}")
        if png_dimensions(output_image) != png_dimensions(source_image):
            fail(f"showcase dimensions changed: {output_image.relative_to(ROOT)}")
        if source_image.name == "ytray-windows-widget.png":
            if sha256(output_image) != sha256(source_image):
                fail("icon-free Windows widget capture should remain byte-identical")
        elif sha256(output_image) == sha256(source_image):
            fail(f"Windows title-bar icon was not refreshed: {output_image.relative_to(ROOT)}")

    print(f"verified canonical YTray icons and website artwork ({source_hash})")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError) as error:
        print(f"icon verification failed: {error}", file=sys.stderr)
        raise SystemExit(1)
