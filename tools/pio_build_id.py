"""Generate a stable 32-bit firmware Build ID from source content and role.

PlatformIO loads this as a pre-build SCons script. The same source tree and
environment produce the same ID; changing firmware source, build configuration,
or the master/slave role changes it. Line endings are normalized so a Windows
and CI checkout agree.
"""

from __future__ import annotations

import argparse
import hashlib
from pathlib import Path


HASHED_EXTENSIONS = {".c", ".cc", ".cpp", ".h", ".hpp", ".ino", ".py"}


def calculate_build_id(project_dir: Path, environment: str) -> str:
    digest = hashlib.sha256()
    digest.update(b"b1-chat-firmware-build-id-v1\0")
    digest.update(environment.encode("utf-8"))
    digest.update(b"\0")

    candidates = [project_dir / "platformio.ini", project_dir / "tools" / "pio_build_id.py"]
    for root_name in ("src", "include", "lib"):
        root = project_dir / root_name
        if root.exists():
            candidates.extend(
                path for path in root.rglob("*")
                if path.is_file() and path.suffix.lower() in HASHED_EXTENSIONS
            )

    for path in sorted(set(candidates), key=lambda item: item.relative_to(project_dir).as_posix()):
        relative = path.relative_to(project_dir).as_posix().encode("utf-8")
        content = path.read_bytes().replace(b"\r\n", b"\n")
        digest.update(relative)
        digest.update(b"\0")
        digest.update(content)
        digest.update(b"\0")

    return digest.hexdigest()[:8].upper()


def configure_platformio(scons_env) -> None:
    project_dir = Path(scons_env.subst("$PROJECT_DIR"))
    environment = scons_env.subst("$PIOENV")
    build_id = calculate_build_id(project_dir, environment)
    scons_env.Append(CPPDEFINES=[("FW_BUILD_ID", f"0x{build_id}UL")])

    # PlatformIO's $BUILD_DIR is already environment-specific while this script
    # is running (for example .pio/build/b1_slave); do not append PIOENV again.
    build_dir = Path(scons_env.subst("$BUILD_DIR"))
    build_dir.mkdir(parents=True, exist_ok=True)
    (build_dir / "firmware_build_id.txt").write_text(build_id + "\n", encoding="ascii")
    print(f"B1 firmware Build ID: {build_id} ({environment})")


try:
    Import("env")  # type: ignore[name-defined]  # provided by PlatformIO/SCons
except NameError:
    if __name__ == "__main__":
        parser = argparse.ArgumentParser()
        parser.add_argument("project_dir", type=Path)
        parser.add_argument("environment")
        args = parser.parse_args()
        print(calculate_build_id(args.project_dir.resolve(), args.environment))
else:
    configure_platformio(env)  # type: ignore[name-defined]
