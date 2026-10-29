#!/bin/sh
# 装 Godot .NET 编辑器与导出模板。
#
# 两件事都不能省：
#   1. .NET 版编辑器（mono_linux_x86_64）自带 GodotSharp，导出时才认得 C# 项目；
#   2. 导出模板目录名必须是 `<版本>.stable.mono`，少一个后缀 Godot 就认为「没装模板」。
#
# GODOT_VERSION 只认 .ci/godot/Dockerfile 里那份（AbyssEcho.csproj 的 Godot.NET.Sdk
# 版本必须和它一致，否则 .NET 侧编出来的 dll 引擎不认）。这里没有默认值兜底：
# 变量没传进来就直接失败，免得悄悄装成另一个版本。
set -eu
: "${GODOT_VERSION:?必须在 Dockerfile 里传入}"
: "${GODOT_CHANNEL:?必须在 Dockerfile 里传入}"

BASE="https://github.com/godotengine/godot-builds/releases/download/${GODOT_VERSION}-${GODOT_CHANNEL}"

curl -fsSL -o /tmp/godot.zip "${BASE}/Godot_v${GODOT_VERSION}-${GODOT_CHANNEL}_mono_linux_x86_64.zip"
mkdir -p /opt/godot
python3 -c "import zipfile;zipfile.ZipFile('/tmp/godot.zip').extractall('/opt/godot')"
mv "/opt/godot/Godot_v${GODOT_VERSION}-${GODOT_CHANNEL}_mono_linux_x86_64" /opt/godot/bin
chmod +x "/opt/godot/bin/Godot_v${GODOT_VERSION}-${GODOT_CHANNEL}_mono_linux.x86_64"
ln -sf "/opt/godot/bin/Godot_v${GODOT_VERSION}-${GODOT_CHANNEL}_mono_linux.x86_64" /usr/local/bin/godot
rm -f /tmp/godot.zip

curl -fsSL -o /tmp/templates.tpz "${BASE}/Godot_v${GODOT_VERSION}-${GODOT_CHANNEL}_mono_export_templates.tpz"
DEST="/root/.local/share/godot/export_templates/${GODOT_VERSION}.${GODOT_CHANNEL}.mono"
mkdir -p "$DEST"
# 只取桌面端两套模板：Android/iOS/macOS 用不到，留着白占 1GB
python3 - "$DEST" <<'PY'
import os, shutil, sys, zipfile
dest = sys.argv[1]
want = {
    'windows_release_x86_64.exe', 'windows_debug_x86_64.exe',
    'linux_release.x86_64', 'linux_debug.x86_64',
    'version.txt',
}
with zipfile.ZipFile('/tmp/templates.tpz') as z:
    for name in z.namelist():
        if os.path.basename(name) in want:
            out = os.path.join(dest, os.path.basename(name))
            with z.open(name) as f, open(out, 'wb') as o:
                shutil.copyfileobj(f, o)
print('templates ->', dest, sorted(os.listdir(dest)))
PY
rm -f /tmp/templates.tpz

# 装完就验：编辑器跑不起来、模板没落地，都在镜像构建期暴露，
# 而不是等到流水线跑到 `--export-release` 才报「没装导出模板」。
godot --version
for f in windows_release_x86_64.exe linux_release.x86_64; do
  [ -f "$DEST/$f" ] || { echo "导出模板缺 $f" >&2; exit 1; }
done
