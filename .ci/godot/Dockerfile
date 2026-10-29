# Godot 4.7 + .NET 构建环境。
#
# 为什么自建镜像：Godot 的 .NET 版必须和导出用的 .NET SDK 对齐，而且导出模板
# （export templates）有 1.2GB、从 GitHub 下载又慢。统一放进镜像，
# 流水线每次只跑 dotnet publish + export，不重复下载。
# 标签要写全版本号：`8.0-bookworm` 这种浮动标签已经从 MCR 下架了，
# 构建时会直接报 `not found`（表现是「镜像拉不到」，和代码无关）。
# 具体版本号同时也让这个镜像可复现——.NET SDK 的小版本会改编译行为。
FROM mcr.microsoft.com/dotnet/sdk:8.0.300-bookworm-slim

ARG GODOT_VERSION=4.7.2
ARG GODOT_CHANNEL=stable

ENV DEBIAN_FRONTEND=noninteractive \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    GODOT_VERSION=${GODOT_VERSION} \
    GODOT_CHANNEL=${GODOT_CHANNEL}

# headless 导出所需的系统库 + 中文字体（Godot 默认字体没有 CJK 字形，界面会全变方块）
# 打包工具（zip/tar）必须显式列进来：debian-slim 两样都不带，缺了会在
# 「导出成功之后」才报 `zip: not found`（退出码 127），一次完整导出白跑。
RUN apt-get update -qq && \
    apt-get install -y -qq --no-install-recommends \
      ca-certificates curl unzip python3 fontconfig \
      libfontconfig1 libfreetype6 libgl1 libxkbcommon0 libxcursor1 \
      libxinerama1 libxrandr2 libxi6 libglu1-mesa fonts-noto-cjk \
      xvfb xauth libglx-mesa0 libgbm1 libwayland-client0 \
      libnss3 libasound2 zip tar \
      nodejs npm && \
    rm -rf /var/lib/apt/lists/*

# 构建期自检：打包工具缺一个就让镜像构建红，而不是等到导出跑完 40 分钟才发现
# node 是 tag 流水线「回写版本号」那一步的执行器（scripts/set-godot-version.mjs）：
# 旧镜像来自 .NET SDK 的 slim 层，不带 node，那一步会以 `node: not found`（127）失败。
RUN for bin in zip unzip tar xvfb-run dotnet node; do \
      command -v "$bin" >/dev/null || { echo "镜像缺 $bin" >&2; exit 1; }; \
    done

# COPY 的源路径相对 build context 根。CNB 的 build context 根 = 仓库根，
# 所以这里必须写全路径；只写文件名会报「/install-godot.sh: not found」。
COPY .ci/godot/install-godot.sh /usr/local/bin/install-godot
RUN chmod +x /usr/local/bin/install-godot && /usr/local/bin/install-godot

WORKDIR /workspace

# 构建产物（界面截图）的存放点。
#
# 为什么放镜像里声明：CNB 只把 Dockerfile 的 VOLUME 声明的路径当作「构建产物」保留下来，
# 别的路径留在容器里就随容器一起没了。截图正是在这套镜像里拍出来的，
# 拍到 /opt/shots 就等于「这次构建确实看了一眼」，下一个人可以在构建详情里复核。
# 也正因为它被保留，截图绝不能落在工作区：Godot 导出会把工作区里的资源打进 pck。
VOLUME ["/opt/shots"]
