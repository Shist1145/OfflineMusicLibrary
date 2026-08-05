from __future__ import annotations

import stat
import tarfile
import zipfile
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
RELEASE = ROOT / "release"
ARTIFACTS = ROOT / "artifacts"
APP_NAME = "本地音乐库.app"
VERSION = "1.4.0"


def zip_info(name: str, *, executable: bool = False, directory: bool = False) -> zipfile.ZipInfo:
    info = zipfile.ZipInfo(name)
    info.create_system = 3
    mode = 0o755 if executable or directory else 0o644
    kind = stat.S_IFDIR if directory else stat.S_IFREG
    info.external_attr = (kind | mode) << 16
    if directory:
        info.external_attr |= 0x10
    return info


def add_zip_file(archive: zipfile.ZipFile, source: Path, target: str, *, executable: bool = False) -> None:
    info = zip_info(target, executable=executable)
    info.date_time = tuple(source.stat().st_mtime_ns and __import__("time").localtime(source.stat().st_mtime)[:6])
    with source.open("rb") as stream:
        archive.writestr(info, stream.read(), compress_type=zipfile.ZIP_DEFLATED)


def package_macos(architecture: str) -> Path:
    source = RELEASE / f"OfflineMusicLibrary-{VERSION}-macos-{architecture}"
    destination = ARTIFACTS / f"OfflineMusicLibrary-{VERSION}-macos-{architecture}.zip"
    if not (source / "OfflineMusicLibrary").is_file():
        raise FileNotFoundError(f"macOS publish output is missing: {source}")

    prefix = f"{APP_NAME}/Contents"
    with zipfile.ZipFile(destination, "w", allowZip64=True) as archive:
        archive.writestr(zip_info(f"{APP_NAME}/", directory=True), b"")
        archive.writestr(zip_info(f"{prefix}/", directory=True), b"")
        archive.writestr(zip_info(f"{prefix}/MacOS/", directory=True), b"")
        archive.writestr(zip_info(f"{prefix}/Resources/", directory=True), b"")
        add_zip_file(archive, ROOT / "packaging/macos/Info.plist", f"{prefix}/Info.plist")
        add_zip_file(archive, ROOT / "packaging/macos/README.txt", f"{prefix}/Resources/README.txt")
        for path in sorted(source.rglob("*")):
            if not path.is_file():
                continue
            relative = path.relative_to(source).as_posix()
            add_zip_file(
                archive,
                path,
                f"{prefix}/MacOS/{relative}",
                executable=relative == "OfflineMusicLibrary",
            )
    return destination


def package_linux() -> Path:
    source = RELEASE / f"OfflineMusicLibrary-{VERSION}-linux-x64"
    destination = ARTIFACTS / f"OfflineMusicLibrary-{VERSION}-linux-x64.tar.gz"
    if not (source / "OfflineMusicLibrary").is_file():
        raise FileNotFoundError(f"Linux publish output is missing: {source}")

    def normalize(info: tarfile.TarInfo) -> tarfile.TarInfo:
        info.uid = 0
        info.gid = 0
        info.uname = "root"
        info.gname = "root"
        info.mode = 0o755 if info.isdir() or info.name == "OfflineMusicLibrary" else 0o644
        return info

    with tarfile.open(destination, "w:gz", compresslevel=6) as archive:
        for path in sorted(source.rglob("*")):
            archive.add(path, arcname=path.relative_to(source).as_posix(), recursive=False, filter=normalize)
        archive.add(ROOT / "packaging/linux/README.txt", arcname="README.txt", filter=normalize)
        archive.add(
            ROOT / "packaging/linux/offline-music-library.desktop",
            arcname="offline-music-library.desktop",
            filter=normalize,
        )
    return destination


def main() -> None:
    ARTIFACTS.mkdir(parents=True, exist_ok=True)
    results = [package_linux(), package_macos("x64"), package_macos("arm64")]
    for result in results:
        print(f"{result.name}: {result.stat().st_size / 1024 / 1024:.1f} MiB")


if __name__ == "__main__":
    main()
