import hashlib
import html
import http.client
import mimetypes
import re
import sys
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

import fitz


ROOT = Path("Findings-gmv")
REGISTER = ROOT / "register.md"
IMAGE_INDEX = ROOT / "image_assets_register.md"


def clean_text(value, limit=160):
    value = html.unescape(value or "")
    value = re.sub(r"\s+", " ", value).strip()
    return value[: limit - 3] + "..." if len(value) > limit else value


def slug(value, limit=90):
    value = clean_text(value).lower()
    value = re.sub(r"[^a-z0-9]+", "_", value).strip("_")
    return (value or "image")[:limit].strip("_") or "image"


def rel(path):
    return path.relative_to(ROOT).as_posix()


def md_link(label, path):
    return f"[{label}](<{rel(path)}>)"


def read_source_url_map():
    if not REGISTER.exists():
        return {}
    text = REGISTER.read_text(encoding="utf-8")
    mapping = {}
    pattern = re.compile(r"- \[[^\]]+\]\(<([^>]+)>\) - Source: <([^>]+)>")
    for match in pattern.finditer(text):
        mapping[match.group(1).replace("\\", "/")] = match.group(2)
    return mapping


def first_page_context(page):
    try:
        lines = [clean_text(line, 100) for line in page.get_text("text").splitlines()]
    except Exception:
        return ""
    lines = [line for line in lines if line and len(line) > 2]
    return " / ".join(lines[:2])


def image_dimensions(path):
    try:
        doc = fitz.open(path)
        if len(doc):
            rect = doc[0].rect
            return int(round(rect.width)), int(round(rect.height))
    except Exception:
        return None, None
    return None, None


def write_manifest(folder, title, rows):
    manifest = folder / "image_manifest.md"
    lines = [
        f"# {title} Image Manifest",
        "",
        f"Image count: {len(rows)}",
        "",
        "| Image | Source | Size | Description |",
        "| --- | --- | --- | --- |",
    ]
    for row in rows:
        size = f"{row.get('width') or '?'} x {row.get('height') or '?'}"
        lines.append(
            f"| [{row['file'].name}](<{row['file'].name}>) | {row['source']} | {size} | {row['description']} |"
        )
    manifest.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return manifest


def extract_pdf_images(pdf_path):
    image_dir = pdf_path.with_name(f"{pdf_path.stem}_images")
    image_dir.mkdir(exist_ok=True)
    rows = []
    try:
        doc = fitz.open(pdf_path)
    except Exception as exc:
        return {"source": pdf_path, "count": 0, "manifest": None, "description": f"Could not open PDF: {exc}"}

    occurrences = {}
    for page_index in range(len(doc)):
        page = doc[page_index]
        context = first_page_context(page)
        for img in page.get_images(full=True):
            xref = img[0]
            item = occurrences.setdefault(
                xref,
                {
                    "pages": [],
                    "contexts": [],
                    "width": img[2],
                    "height": img[3],
                    "filter": img[8] if len(img) > 8 else "",
                },
            )
            item["pages"].append(page_index + 1)
            if context and context not in item["contexts"]:
                item["contexts"].append(context)

    for idx, (xref, info) in enumerate(sorted(occurrences.items()), 1):
        try:
            extracted = doc.extract_image(xref)
        except Exception:
            continue
        data = extracted.get("image")
        if not data:
            continue
        ext = extracted.get("ext") or "bin"
        first_page = min(info["pages"]) if info["pages"] else 0
        file_path = image_dir / f"image_{idx:04d}_p{first_page:03d}_xref{xref}.{ext}"
        file_path.write_bytes(data)
        width = extracted.get("width") or info["width"]
        height = extracted.get("height") or info["height"]
        context = "; ".join(info["contexts"][:2])
        if width and height and max(width, height) < 150:
            kind = "Small embedded graphic, logo, icon, or marker"
        else:
            kind = "Embedded PDF image or figure"
        description = f"{kind} from {pdf_path.name}"
        if context:
            description += f"; page context: {context}"
        rows.append(
            {
                "file": file_path,
                "source": f"PDF page(s) {', '.join(map(str, sorted(set(info['pages']))[:8]))}",
                "width": width,
                "height": height,
                "description": clean_text(description, 220),
            }
        )

    manifest = write_manifest(image_dir, pdf_path.stem, rows) if rows else None
    return {
        "source": pdf_path,
        "count": len(rows),
        "manifest": manifest,
        "description": f"Extracted embedded images from {pdf_path.name}.",
    }


ATTR_RE = re.compile(r"""([:\w-]+)\s*=\s*(['"])(.*?)\2""", re.S)
TAG_RE = re.compile(r"<(?:img|source)\b[^>]*>", re.I | re.S)
MD_IMG_RE = re.compile(r"!\[([^\]]*)\]\(([^)\s]+)(?:\s+['\"][^'\"]*['\"])?\)")


def parse_srcset(value):
    urls = []
    for part in value.split(","):
        bit = part.strip().split()
        if bit:
            urls.append(bit[0])
    return urls


def html_image_links(path, source_url):
    text = path.read_text(encoding="utf-8", errors="ignore")
    found = []
    for tag in TAG_RE.findall(text):
        attrs = {k.lower(): html.unescape(v) for k, _, v in ATTR_RE.findall(tag)}
        label = clean_text(attrs.get("alt") or attrs.get("title") or attrs.get("aria-label") or "")
        for key in ("src", "data-src", "data-original", "data-lazy-src"):
            if attrs.get(key):
                found.append((attrs[key], label))
        for key in ("srcset", "data-srcset"):
            if attrs.get(key):
                for url in parse_srcset(attrs[key]):
                    found.append((url, label))
    for alt, url in MD_IMG_RE.findall(text):
        found.append((html.unescape(url), clean_text(alt)))

    resolved = []
    seen = set()
    for url, label in found:
        url = url.strip().strip("'\"")
        if not url or url.startswith(("data:", "blob:", "javascript:", "mailto:")):
            continue
        if url.startswith("//"):
            url = "https:" + url
        absolute = urllib.parse.urljoin(source_url or path.as_uri(), url)
        absolute = quote_url(absolute)
        if absolute in seen:
            continue
        seen.add(absolute)
        resolved.append((absolute, label))
    return resolved


def quote_url(url):
    parts = urllib.parse.urlsplit(url)
    path = urllib.parse.quote(parts.path, safe="/%:@")
    query = urllib.parse.quote(parts.query, safe="=&%:@/?+,")
    fragment = urllib.parse.quote(parts.fragment, safe="")
    return urllib.parse.urlunsplit((parts.scheme, parts.netloc, path, query, fragment))


def extension_from_response(url, headers, fallback=".img"):
    content_type = (headers.get("Content-Type") or "").split(";")[0].strip().lower()
    ext = mimetypes.guess_extension(content_type) if content_type else None
    if ext == ".jpe":
        ext = ".jpg"
    if ext:
        return ext
    parsed = urllib.parse.urlparse(url)
    suffix = Path(urllib.parse.unquote(parsed.path)).suffix
    return suffix if suffix and len(suffix) <= 8 else fallback


def download_url(url):
    request = urllib.request.Request(
        url,
        headers={
            "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36",
            "Accept": "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8",
        },
    )
    with urllib.request.urlopen(request, timeout=30) as response:
        data = response.read()
        headers = response.headers
    content_type = (headers.get("Content-Type") or "").split(";")[0].strip().lower()
    if content_type and not content_type.startswith("image/"):
        raise ValueError(f"response is not an image: {content_type}")
    return data, headers


def extract_linked_images(doc_path, source_url):
    links = html_image_links(doc_path, source_url)
    if not links:
        return {"source": doc_path, "count": 0, "manifest": None, "description": "No linked images found."}

    image_dir = doc_path.with_name(f"{doc_path.stem}_images")
    image_dir.mkdir(exist_ok=True)
    rows = []
    failures = []
    for idx, (url, label) in enumerate(links, 1):
        try:
            data, headers = download_url(url)
        except (urllib.error.URLError, TimeoutError, OSError, ValueError, http.client.InvalidURL) as exc:
            failures.append((url, str(exc)))
            continue
        digest = hashlib.sha1(data).hexdigest()[:10]
        ext = extension_from_response(url, headers)
        name_source = label or Path(urllib.parse.unquote(urllib.parse.urlparse(url).path)).stem or "linked_image"
        file_path = image_dir / f"linked_{idx:04d}_{slug(name_source, 50)}_{digest}{ext}"
        file_path.write_bytes(data)
        width, height = image_dimensions(file_path)
        description = f"Linked image from {doc_path.name}"
        if label:
            description += f"; label/alt text: {label}"
        else:
            description += f"; source filename: {Path(urllib.parse.unquote(urllib.parse.urlparse(url).path)).name or url}"
        rows.append(
            {
                "file": file_path,
                "source": f"[source]({url})",
                "width": width,
                "height": height,
                "description": clean_text(description, 220),
            }
        )

    if failures:
        (image_dir / "download_failures.txt").write_text(
            "\n".join(f"{url}\t{message}" for url, message in failures) + "\n",
            encoding="utf-8",
        )
    manifest = write_manifest(image_dir, doc_path.stem, rows) if rows else None
    note = f"Downloaded linked images from {doc_path.name}."
    if failures:
        note += f" {len(failures)} linked image(s) could not be downloaded; see download_failures.txt."
    return {"source": doc_path, "count": len(rows), "manifest": manifest, "description": note}


def update_register(results):
    if not REGISTER.exists():
        return
    text = REGISTER.read_text(encoding="utf-8")
    start_marker = "<!-- IMAGE_ASSETS_START -->"
    end_marker = "<!-- IMAGE_ASSETS_END -->"
    block_lines = [
        "## Image Assets",
        "",
        "Detailed per-image descriptions are stored in the linked image manifests. PDF images were extracted from embedded image streams; HTML and Markdown images were downloaded from linked image URLs where accessible.",
        "",
        f"- Master image register: [image_assets_register.md](<image_assets_register.md>)",
        "",
        "| Source document | Images | Manifest | Description |",
        "| --- | ---: | --- | --- |",
    ]
    for result in results:
        if result["count"] <= 0:
            continue
        source = md_link(result["source"].name, result["source"])
        manifest = md_link("manifest", result["manifest"]) if result.get("manifest") else ""
        block_lines.append(
            f"| {source} | {result['count']} | {manifest} | {clean_text(result['description'], 180)} |"
        )
    block = start_marker + "\n" + "\n".join(block_lines) + "\n" + end_marker
    if start_marker in text and end_marker in text:
        text = re.sub(
            re.escape(start_marker) + r".*?" + re.escape(end_marker),
            block,
            text,
            flags=re.S,
        )
    else:
        text = text.rstrip() + "\n\n" + block + "\n"
    REGISTER.write_text(text, encoding="utf-8")


def write_master_index(results):
    lines = [
        "# GMV Image Assets Register",
        "",
        "This file is generated from the PDF, HTML, and Markdown documents in the findings folder.",
        "",
        "| Theme | Source document | Images | Manifest | Description |",
        "| --- | --- | ---: | --- | --- |",
    ]
    for result in results:
        if result["count"] <= 0:
            continue
        theme = result["source"].parent.relative_to(ROOT).as_posix() if result["source"].parent != ROOT else "Root"
        lines.append(
            f"| {theme} | {md_link(result['source'].name, result['source'])} | {result['count']} | {md_link('manifest', result['manifest'])} | {clean_text(result['description'], 220)} |"
        )
    IMAGE_INDEX.write_text("\n".join(lines) + "\n", encoding="utf-8")


def main():
    if not ROOT.exists():
        print("Findings-gmv folder not found.", file=sys.stderr)
        return 1
    source_map = read_source_url_map()
    results = []

    pdfs = sorted(ROOT.rglob("*.pdf"))
    for pdf in pdfs:
        if any(part.endswith("_images") for part in pdf.parts):
            continue
        result = extract_pdf_images(pdf)
        results.append(result)
        print(f"PDF {rel(pdf)}: {result['count']} image(s)")

    docs = sorted(list(ROOT.rglob("*.html")) + list(ROOT.rglob("*.md")))
    for doc in docs:
        if doc.name in {"register.md", "image_assets_register.md", "image_manifest.md"}:
            continue
        if any(part.endswith("_images") for part in doc.parts):
            continue
        source_url = source_map.get(rel(doc))
        result = extract_linked_images(doc, source_url)
        results.append(result)
        print(f"LINKED {rel(doc)}: {result['count']} image(s)")

    write_master_index(results)
    update_register(results)
    print(f"Wrote {rel(IMAGE_INDEX)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
