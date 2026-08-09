from __future__ import annotations

import argparse
import os
import shutil
import stat
import tempfile
import zipfile
from pathlib import Path


FIXED_TIMESTAMP = (1980, 1, 1, 0, 0, 0)
EXCLUDED_PREFIXES = ("libvlc/win-x86/", "libvlc/win-arm64/")


def is_link_like(path: Path) -> bool:
    is_junction = getattr(path, "is_junction", None)
    return path.is_symlink() or bool(is_junction and is_junction())


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Create a deterministic Windows release ZIP.")
    parser.add_argument("--source", type=Path, required=True, help="Published application directory")
    parser.add_argument("--output", type=Path, required=True, help="Destination ZIP path")
    return parser.parse_args()


def safe_files(source: Path) -> list[Path]:
    source_resolved = source.resolve(strict=True)
    files: list[Path] = []
    for path in source.rglob("*"):
        if is_link_like(path):
            raise ValueError(f"Refusing to package a symbolic link or junction: {path}")
        if not path.is_file():
            continue
        relative = path.relative_to(source).as_posix()
        if relative.startswith(EXCLUDED_PREFIXES) or relative.lower().endswith(".pdb"):
            continue
        resolved = path.resolve(strict=True)
        if not resolved.is_relative_to(source_resolved):
            raise ValueError(f"Refusing to package a file outside the publish directory: {path}")
        files.append(path)
    return sorted(files, key=lambda item: item.relative_to(source).as_posix())


def create_archive(source: Path, output: Path) -> None:
    if is_link_like(source):
        raise ValueError(f"Refusing to package a redirected publish directory: {source}")
    source = source.resolve(strict=True)
    if not source.is_dir():
        raise NotADirectoryError(source)
    if output.exists() and is_link_like(output):
        raise ValueError(f"Refusing to replace a symbolic-link output: {output}")
    output = output.resolve(strict=False)
    output.parent.mkdir(parents=True, exist_ok=True)

    descriptor, temporary_name = tempfile.mkstemp(
        prefix=output.name + ".",
        suffix=".tmp",
        dir=output.parent,
    )
    os.close(descriptor)
    temporary = Path(temporary_name)
    try:
        with zipfile.ZipFile(
            temporary,
            "w",
            compression=zipfile.ZIP_DEFLATED,
            compresslevel=9,
            allowZip64=True,
        ) as archive:
            for path in safe_files(source):
                relative = path.relative_to(source).as_posix()
                info = zipfile.ZipInfo(relative, FIXED_TIMESTAMP)
                info.create_system = 3
                info.compress_type = zipfile.ZIP_DEFLATED
                info.external_attr = (stat.S_IFREG | 0o644) << 16
                with path.open("rb") as source_stream, archive.open(info, "w", force_zip64=True) as target_stream:
                    shutil.copyfileobj(source_stream, target_stream, length=1024 * 1024)
        os.replace(temporary, output)
    finally:
        temporary.unlink(missing_ok=True)


def main() -> None:
    arguments = parse_args()
    create_archive(arguments.source, arguments.output)
    print(f"Created {arguments.output} ({arguments.output.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
