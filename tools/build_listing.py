#!/usr/bin/env python3
"""VPM listing (index.json) とパッケージ zip を作る。依存ゼロ (標準ライブラリのみ)。

形式の根拠 (2026-08-20 に一次情報を実読):
  - VPM repository の形 = トップレベル name / author / url / id / packages、
    packages.<pkg>.versions.<ver> = package.json の内容 + url + zipSHA256
    (https://vcc.docs.vrchat.com/vpm/repos/ と、実物の公式 listing
     https://vrchat.github.io/packages/index.json を突き合わせて確認)
  - url は zip への direct-download link (https://vcc.docs.vrchat.com/vpm/packages/)
  - zip の中身のレイアウトは公式 doc に記載が無い。**package.json を zip のルートに置く**
    慣行に従う (VRChat 配布物と同形)。ここは doc の裏付けが無い箇所なので、
    最初の 1 本は実際に VCC から入れて確かめること

なぜ決定論的に作るか: BOOTH に置く zip は GitHub Releases の zip と**同一物**である必要があり
(2 枠方式・ミラー運用)、照合は sha256 で行う。同じ入力から同じバイト列が出ないと照合できない。
"""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
import zipfile
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
PACKAGE_DIR = REPO / "Packages" / "com.mizprism.mizeye"
BUILD_DIR = REPO / "build"

# listing 自身の素性。id は listing の識別子で、パッケージ名とは別物。
LISTING_NAME = "MIZPRISM"
LISTING_ID = "com.mizprism.repos.mizeye"
LISTING_AUTHOR = "MIZPRISM"
LISTING_URL = "https://mizprism.github.io/mizeye/index.json"

# zip に入れない物。Tests~ は dotnet 側のテストハーネス (csproj 同梱) で、
# 利用者がインストールする物ではない。Unity は `~` 付きディレクトリを取り込まないので
# 入れても実害は無いが、配布物 = 利用者が入れる物、に揃える。
EXCLUDE_DIRS = {"Tests~", "bin", "obj", ".git"}
EXCLUDE_NAMES = {".DS_Store"}

# zip のタイムスタンプ。zip 形式の下限が 1980-01-01 なのでそこに固定する
# (実時刻を入れるとビルドのたびに sha256 が変わり、ミラー照合ができない)。
FIXED_TIMESTAMP = (1980, 1, 1, 0, 0, 0)


def collect_files(root: Path) -> list[Path]:
    """zip に入れるファイルを、パス順に並べて返す (順序も決定論に効く)。"""
    out = []
    for path in root.rglob("*"):
        if not path.is_file():
            continue
        rel = path.relative_to(root)
        if any(part in EXCLUDE_DIRS for part in rel.parts):
            continue
        if path.name in EXCLUDE_NAMES:
            continue
        out.append(rel)
    return sorted(out, key=lambda p: p.as_posix())


def check_meta(files: list[Path]) -> list[str]:
    """.meta の欠落を報告する。

    VPM 配布物に .meta が無いと、Unity が導入時に GUID を生成する = 利用者ごとに GUID が
    変わり、参照が壊れる。GUID が焼き込まれるまでここで止める。
    """
    has_meta = any(f.suffix == ".meta" for f in files)
    if has_meta:
        return []
    return [
        ".meta ファイルが 1 つも無い。VPM 配布物としては GUID が導入ごとに変わるため、"
        "参照が利用者側で壊れる。"
        "生成する Unity 版と環境を決めて 1 回だけ焼き込んでから配布すること。"
    ]


def build_zip(package_dir: Path, out_path: Path, files: list[Path]) -> bytes:
    """決定論的な zip を書き、そのバイト列を返す。"""
    out_path.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(out_path, "w", compression=zipfile.ZIP_DEFLATED) as zf:
        for rel in files:
            info = zipfile.ZipInfo(rel.as_posix(), date_time=FIXED_TIMESTAMP)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16  # 実行ビットを混ぜない (環境差の排除)
            zf.writestr(info, (package_dir / rel).read_bytes())
    return out_path.read_bytes()


def show(path: Path) -> str:
    """表示用のパス。**表示のために落ちない** — リポジトリ外や相対パスを渡されても
    絶対パスで出す。成果物を書き終えた後に整形で落ちると、出来ているのに失敗が返る
    (2026-08-20 実測: --listing に相対パスを渡すと relative_to が ValueError)。"""
    try:
        return str(path.resolve().relative_to(REPO))
    except ValueError:
        return str(path.resolve())


def load_existing_listing(path: Path) -> dict:
    """既存 listing を読む。**過去バージョンを消さないため** に必ずマージする。

    listing に最新版しか載っていないと、利用者は前の版へ戻せない。
    """
    if not path.exists():
        return {}
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        sys.exit(f"既存 listing を読めない ({path}): {exc}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--zip-url",
        required=True,
        help="この版の zip の direct-download URL "
        "(例: https://github.com/mizprism/mizeye/releases/download/v0.1.0/"
        "com.mizprism.mizeye-0.1.0.zip)",
    )
    parser.add_argument(
        "--listing",
        type=Path,
        default=BUILD_DIR / "index.json",
        help="出力先の listing。既存があればバージョンをマージする",
    )
    parser.add_argument(
        "--allow-missing-meta",
        action="store_true",
        help="開発用。.meta が無くてもビルドする (配布物には使わない)",
    )
    args = parser.parse_args()

    manifest_path = PACKAGE_DIR / "package.json"
    if not manifest_path.exists():
        sys.exit(f"package.json が無い: {manifest_path}")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))

    name = manifest["name"]
    version = manifest["version"]

    files = collect_files(PACKAGE_DIR)
    if not files:
        sys.exit(f"パッケージが空に見える: {PACKAGE_DIR}")

    problems = check_meta(files)
    if problems and not args.allow_missing_meta:
        for p in problems:
            print(f"ERROR: {p}", file=sys.stderr)
        print(
            "配布物でないなら --allow-missing-meta を付けて続行できる。",
            file=sys.stderr,
        )
        return 1
    for p in problems:
        print(f"WARNING (--allow-missing-meta): {p}", file=sys.stderr)

    zip_path = BUILD_DIR / f"{name}-{version}.zip"
    payload = build_zip(PACKAGE_DIR, zip_path, files)
    digest = hashlib.sha256(payload).hexdigest()

    # version エントリ = package.json の内容 + url + zipSHA256 (公式 listing と同形)
    entry = dict(manifest)
    entry["url"] = args.zip_url
    entry["zipSHA256"] = digest

    listing = load_existing_listing(args.listing)
    listing["name"] = LISTING_NAME
    listing["id"] = LISTING_ID
    listing["author"] = LISTING_AUTHOR
    listing["url"] = LISTING_URL
    packages = listing.setdefault("packages", {})
    versions = packages.setdefault(name, {}).setdefault("versions", {})

    previous = versions.get(version)
    if previous is not None and previous.get("zipSHA256") != digest:
        print(
            f"ERROR: {version} は既に listing にあり、しかも中身が違う "
            f"(既存 {previous.get('zipSHA256')} / 今回 {digest})。"
            "公開済みの版を差し替えると、既に入れた利用者と別物になる。"
            "版を上げること。",
            file=sys.stderr,
        )
        return 1
    versions[version] = entry

    args.listing.parent.mkdir(parents=True, exist_ok=True)
    args.listing.write_text(
        json.dumps(listing, indent=2, ensure_ascii=False, sort_keys=False) + "\n",
        encoding="utf-8",
    )

    print(f"zip     : {show(zip_path)} ({len(payload)} bytes)")
    print(f"sha256  : {digest}")
    print(f"listing : {show(args.listing)}")
    print(f"versions: {', '.join(sorted(versions))}")
    print()
    print("BOOTH に置く zip はこのファイルそのもの。差し替え時は上の sha256 で照合する。")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
