#!/usr/bin/env bash

set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [[ "$(uname -s)" != "Linux" ]]; then
    echo "LinuxPty.NET's native shim can only be built on Linux." >&2
    exit 1
fi

if ! getconf GNU_LIBC_VERSION 2>/dev/null | grep -q '^glibc '; then
    echo "LinuxPty.NET produces glibc Linux assets; musl is not supported." >&2
    exit 1
fi

case "$(uname -m)" in
    x86_64)
        rid="linux-x64"
        expected_machine="Advanced Micro Devices X86-64"
        ;;
    aarch64|arm64)
        rid="linux-arm64"
        expected_machine="AArch64"
        ;;
    *)
        echo "Unsupported Linux architecture: $(uname -m)" >&2
        exit 1
        ;;
esac

expected_rid="${1:-}"
if [[ -n "$expected_rid" && "$expected_rid" != "$rid" ]]; then
    echo "This runner is $rid, not the requested $expected_rid." >&2
    exit 1
fi

build_dir="$script_dir/build/$rid"
output_dir="$script_dir/output/runtimes/$rid/native"
library="$build_dir/bin/libporta_pty.so"

rm -rf "$build_dir"
mkdir -p "$build_dir"
(
    cd "$build_dir"
    cmake -DCMAKE_BUILD_TYPE=Release "$script_dir"
    cmake --build . --config Release
)

if [[ ! -f "$library" ]]; then
    echo "Native build did not produce $library." >&2
    exit 1
fi

elf_class="$(readelf -h "$library" | awk -F: '/Class:/{sub(/^[[:space:]]+/, "", $2); print $2}')"
machine="$(readelf -h "$library" | awk -F: '/Machine:/{sub(/^[[:space:]]+/, "", $2); print $2}')"
if [[ "$elf_class" != "ELF64" || "$machine" != "$expected_machine" ]]; then
    echo "Unexpected native asset: class='$elf_class', machine='$machine'." >&2
    exit 1
fi

mkdir -p "$output_dir"
cp "$library" "$output_dir/libporta_pty.so"

echo "Built $output_dir/libporta_pty.so"
