#!/usr/bin/env bash
set -euo pipefail

archive="${1:-reference-audit/bilibili-helper-3.0.4.zip}"
main_script="${2:-reference-audit/bilibili-helper-content-script.js}"
test "$(sha256sum "$archive" | awk '{print $1}')" = "95036016a004107979b179bd4cb43de76e40d95dc2ef9a020f6a3b385f54e1a4"
test "$(sha256sum "$main_script" | awk '{print $1}')" = "89474c3750f92ac9ea2fe5e099c8d8ecccb96d0cc644d989c7c62c959d95963d"
echo "Reference hashes verified."
