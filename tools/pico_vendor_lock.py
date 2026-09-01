#!/usr/bin/env python3
"""Verify the vendored PICO Unity package and Android binary lock."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import sys


REPOSITORY_ROOT = Path(__file__).resolve().parents[1]
LOCK_PATH = REPOSITORY_ROOT / "PICO_SDK_VENDOR_LOCK.json"
UNITY_MANIFEST_PATH = REPOSITORY_ROOT / "Packages" / "manifest.json"


def sha256(file_path: Path) -> str:
    digest = hashlib.sha256()
    with file_path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def relative_path(file_path: Path) -> str:
    return file_path.relative_to(REPOSITORY_ROOT).as_posix()


def discover_android_artifacts(sdk_path: Path) -> set[str]:
    return {
        relative_path(file_path)
        for file_path in sdk_path.rglob("*")
        if file_path.is_file() and file_path.suffix.lower() in {".aar", ".jar"}
    }


def verify() -> int:
    lock = json.loads(LOCK_PATH.read_text(encoding="utf-8"))
    errors: list[str] = []

    package = lock["package"]
    sdk_path = REPOSITORY_ROOT / package["path"]
    package_manifest_path = sdk_path / "package.json"
    if not package_manifest_path.is_file():
        errors.append(f"missing SDK package manifest: {relative_path(package_manifest_path)}")
    else:
        package_manifest = json.loads(package_manifest_path.read_text(encoding="utf-8"))
        if package_manifest.get("name") != package["name"]:
            errors.append(
                f"package name mismatch: {package_manifest.get('name')!r} != {package['name']!r}"
            )
        if package_manifest.get("version") != package["version"]:
            errors.append(
                f"package version mismatch: {package_manifest.get('version')!r} != "
                f"{package['version']!r}"
            )
        actual_manifest_hash = sha256(package_manifest_path)
        if actual_manifest_hash != package["manifest_sha256"]:
            errors.append(
                f"SDK package.json SHA-256 mismatch: {actual_manifest_hash} != "
                f"{package['manifest_sha256']}"
            )

    unity_manifest = json.loads(UNITY_MANIFEST_PATH.read_text(encoding="utf-8"))
    actual_dependency = unity_manifest.get("dependencies", {}).get(package["name"])
    if actual_dependency != lock["unity_dependency"]:
        errors.append(
            f"Unity dependency mismatch: {actual_dependency!r} != "
            f"{lock['unity_dependency']!r}"
        )

    expected_artifacts = {entry["path"] for entry in lock["artifacts"]}
    actual_artifacts = discover_android_artifacts(sdk_path)
    for unexpected_path in sorted(actual_artifacts - expected_artifacts):
        errors.append(f"unlocked Android artifact: {unexpected_path}")
    for missing_path in sorted(expected_artifacts - actual_artifacts):
        errors.append(f"missing locked Android artifact: {missing_path}")

    for entry in lock["artifacts"]:
        artifact_path = REPOSITORY_ROOT / entry["path"]
        if not artifact_path.is_file():
            continue
        actual_size = artifact_path.stat().st_size
        if actual_size != entry["size_bytes"]:
            errors.append(
                f"size mismatch for {entry['path']}: {actual_size} != {entry['size_bytes']}"
            )
        actual_hash = sha256(artifact_path)
        if actual_hash != entry["sha256"]:
            errors.append(
                f"SHA-256 mismatch for {entry['path']}: {actual_hash} != {entry['sha256']}"
            )

        meta_path = artifact_path.with_name(artifact_path.name + ".meta")
        if not meta_path.is_file():
            errors.append(f"missing Unity importer metadata: {relative_path(meta_path)}")
        else:
            actual_meta_hash = sha256(meta_path)
            if actual_meta_hash != entry["meta_sha256"]:
                errors.append(
                    f"metadata SHA-256 mismatch for {relative_path(meta_path)}: "
                    f"{actual_meta_hash} != {entry['meta_sha256']}"
                )

    for entry in lock.get("custom_files", []):
        custom_path = REPOSITORY_ROOT / entry["path"]
        if not custom_path.is_file():
            errors.append(f"missing locked customization: {entry['path']}")
            continue
        actual_hash = sha256(custom_path)
        if actual_hash != entry["sha256"]:
            errors.append(
                f"SHA-256 mismatch for {entry['path']}: {actual_hash} != {entry['sha256']}"
            )

    if errors:
        print("PICO vendor lock verification failed:", file=sys.stderr)
        for error in errors:
            print(f"- {error}", file=sys.stderr)
        return 1

    print(
        f"PICO vendor lock verified: SDK {package['version']}, "
        f"{len(expected_artifacts)} Android artifacts."
    )
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.parse_args()
    return verify()


if __name__ == "__main__":
    raise SystemExit(main())
