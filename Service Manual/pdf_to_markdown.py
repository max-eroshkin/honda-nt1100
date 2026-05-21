#!/usr/bin/env python3
"""Convert PDF service manual pages to Markdown with extracted images."""

from __future__ import annotations

import argparse
import re
import sys
from concurrent.futures import ProcessPoolExecutor, as_completed
from pathlib import Path

import fitz


def slugify_stem(stem: str) -> str:
    return re.sub(r'[<>:"/\\|?*]', "_", stem)


def convert_pdf(pdf_path: Path, output_root: Path, render_scale: float = 2.0) -> tuple[str, int, int]:
    stem = pdf_path.stem
    out_dir = output_root / slugify_stem(stem)
    img_dir = out_dir / "images"
    img_dir.mkdir(parents=True, exist_ok=True)

    doc = fitz.open(pdf_path)
    md_parts: list[str] = [
        f"# {stem}\n",
        f"\nИсточник: `{pdf_path.name}`\n",
    ]
    image_count = 0

    for page_num in range(len(doc)):
        page = doc[page_num]
        page_no = page_num + 1
        prefix = f"page-{page_no:03d}"
        image_refs: list[str] = []
        seen_xrefs: set[int] = set()

        for img_info in page.get_images(full=True):
            xref = img_info[0]
            if xref in seen_xrefs:
                continue
            seen_xrefs.add(xref)
            try:
                base = doc.extract_image(xref)
            except Exception:
                continue
            ext = base["ext"]
            if ext == "jpeg":
                ext = "jpg"
            image_count += 1
            img_idx = len(image_refs) + 1
            img_name = f"{prefix}-img-{img_idx:02d}.{ext}"
            (img_dir / img_name).write_bytes(base["image"])
            image_refs.append(img_name)

        if not image_refs:
            matrix = fitz.Matrix(render_scale, render_scale)
            pix = page.get_pixmap(matrix=matrix, alpha=False)
            img_name = f"{prefix}.png"
            pix.save(img_dir / img_name)
            image_refs.append(img_name)
            image_count += 1

        md_parts.append(f"\n## Страница {page_no}\n")
        for img_name in image_refs:
            md_parts.append(f"\n![Страница {page_no}](images/{img_name})\n")

        text = page.get_text("text").strip()
        if text:
            md_parts.append(f"\n{text}\n")

    page_count = len(doc)
    doc.close()

    md_path = out_dir / f"{stem}.md"
    md_path.write_text("".join(md_parts), encoding="utf-8")
    return stem, page_count, image_count


def collect_pdfs(source: Path) -> list[Path]:
    return sorted(source.glob("*.pdf"))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--source",
        type=Path,
        default=Path(__file__).resolve().parent,
        help="Directory containing PDF files",
    )
    parser.add_argument(
        "--output",
        type=Path,
        default=None,
        help="Output root (default: <source>/markdown)",
    )
    parser.add_argument(
        "--workers",
        type=int,
        default=4,
        help="Parallel worker processes",
    )
    parser.add_argument(
        "--only",
        type=str,
        default="",
        help="Convert only PDFs whose stem contains this substring",
    )
    args = parser.parse_args()

    source = args.source.resolve()
    output_root = (args.output or source / "markdown").resolve()
    output_root.mkdir(parents=True, exist_ok=True)

    pdfs = collect_pdfs(source)
    if args.only:
        pdfs = [p for p in pdfs if args.only in p.stem]

    if not pdfs:
        print("No PDF files found.", file=sys.stderr)
        return 1

    print(f"Converting {len(pdfs)} PDF(s) -> {output_root}")

    ok = 0
    errors: list[str] = []

    if args.workers <= 1:
        for pdf in pdfs:
            try:
                stem, pages, images = convert_pdf(pdf, output_root)
                ok += 1
                print(f"  OK {stem}: {pages} page(s), {images} image(s)")
            except Exception as exc:
                errors.append(f"{pdf.name}: {exc}")
                print(f"  FAIL {pdf.name}: {exc}", file=sys.stderr)
    else:
        with ProcessPoolExecutor(max_workers=args.workers) as pool:
            futures = {
                pool.submit(convert_pdf, pdf, output_root): pdf for pdf in pdfs
            }
            for future in as_completed(futures):
                pdf = futures[future]
                try:
                    stem, pages, images = future.result()
                    ok += 1
                    print(f"  OK {stem}: {pages} page(s), {images} image(s)")
                except Exception as exc:
                    errors.append(f"{pdf.name}: {exc}")
                    print(f"  FAIL {pdf.name}: {exc}", file=sys.stderr)

    print(f"\nDone: {ok}/{len(pdfs)} succeeded.")
    if errors:
        print("Errors:", file=sys.stderr)
        for err in errors:
            print(f"  - {err}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
