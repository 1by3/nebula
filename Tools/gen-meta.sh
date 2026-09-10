#!/usr/bin/env bash
# Generates deterministic Unity .meta files for every script/folder/asmdef/text asset under the
# given roots that does not already have one. GUID = md5(project-relative path), so a file gets
# the same GUID on every machine and in every checkout - which lets hand-written or generated
# prefabs/scenes reference scripts before the Editor has ever seen them.
#
#   bash Tools/gen-meta.sh Packages/com.1by3.nebula Assets/Samples
set -euo pipefail
cd "$(dirname "$0")/.."

guid_for() { printf '%s' "$1" | md5sum | cut -c1-32; }

emit_folder() {
  cat > "$1.meta" <<EOF
fileFormatVersion: 2
guid: $2
folderAsset: yes
DefaultImporter:
  externalObjects: {}
  userData:
  assetBundleName:
  assetBundleVariant:
EOF
}
emit_cs() {
  cat > "$1.meta" <<EOF
fileFormatVersion: 2
guid: $2
MonoImporter:
  externalObjects: {}
  serializedVersion: 2
  defaultReferences: []
  executionOrder: 0
  icon: {instanceID: 0}
  userData:
  assetBundleName:
  assetBundleVariant:
EOF
}
emit_asmdef() {
  cat > "$1.meta" <<EOF
fileFormatVersion: 2
guid: $2
AssemblyDefinitionImporter:
  externalObjects: {}
  userData:
  assetBundleName:
  assetBundleVariant:
EOF
}
emit_text() {
  cat > "$1.meta" <<EOF
fileFormatVersion: 2
guid: $2
TextScriptImporter:
  externalObjects: {}
  userData:
  assetBundleName:
  assetBundleVariant:
EOF
}
emit_default() {
  cat > "$1.meta" <<EOF
fileFormatVersion: 2
guid: $2
DefaultImporter:
  externalObjects: {}
  userData:
  assetBundleName:
  assetBundleVariant:
EOF
}

count=0
for root in "$@"; do
  # Folders (skip Unity-hidden ones ending in ~ and anything inside them)
  while IFS= read -r d; do
    [[ "$d" == *~ ]] && continue
    [[ "$d" == *~/* ]] && continue
    [[ -f "$d.meta" ]] && continue
    emit_folder "$d" "$(guid_for "$d")"; count=$((count+1))
  done < <(find "$root" -type d | sort)
  # Files
  while IFS= read -r f; do
    [[ "$f" == *.meta ]] && continue
    [[ "$f" == *~/* ]] && continue
    [[ -f "$f.meta" ]] && continue
    g="$(guid_for "$f")"
    case "$f" in
      *.cs) emit_cs "$f" "$g" ;;
      *.asmdef) emit_asmdef "$f" "$g" ;;
      *.txt|*.md|*.json|*.xml) emit_text "$f" "$g" ;;
      *) emit_default "$f" "$g" ;;
    esac
    count=$((count+1))
  done < <(find "$root" -type f | sort)
done
echo "[gen-meta] wrote $count meta files"
