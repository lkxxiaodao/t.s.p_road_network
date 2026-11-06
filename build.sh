#!/usr/bin/env bash
# =============================================================================
#  《信号规划师》统一构建与自检脚本
#  依据：技术方案03 §1.3 工程结构 / §1.4 前置准备 / §6.2 收活 6 步
#
#  用法：
#    ./build.sh                      # 只做 C# 侧：dotnet build + dotnet test
#    ./build.sh /path/to/godot_console.exe
#    GODOT_BIN=/path/to/godot_console.exe ./build.sh
#                                    # 追加两个 Godot .NET 工程的 headless 打开自检
#
#  退出码：0 = 全部通过；非 0 = 任一步失败
# =============================================================================
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SOLUTION="$ROOT/SignalPlanner.sln"
CONFIG="${CONFIG:-Release}"
GODOT_BIN="${1:-${GODOT_BIN:-}}"

log()  { printf '\n==> %s\n' "$1"; }
ok()   { printf '✓ %s\n' "$1"; }
warn() { printf '! %s\n' "$1"; }
die()  { printf '✗ %s\n' "$1" >&2; exit 1; }

# ---------------------------------------------------------------------------
# 0. 环境兜底
#    受限环境（脚本从被剥离环境变量的子 shell 启动）会丢失标准 Windows 变量，
#    导致两类报错：MSBuild 的 "MSBuildTemp... is denied"（退化到 C:\windows）、
#    NuGet 的 "Value cannot be null. (Parameter 'path1')"（机器级配置路径解析失败）。
#    这里仅补齐"缺失"的变量，且先探测路径确实存在，避免写错盘符。
# ---------------------------------------------------------------------------
winpath()   { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi; }
posixpath() { if command -v cygpath >/dev/null 2>&1; then cygpath -u "$1" 2>/dev/null || printf '%s' "$1"; else printf '%s' "$1"; fi; }

if [[ -n "${HOME:-}" ]]; then
    # 推导系统盘：优先用已导出的权威值（SYSTEMDRIVE / SYSTEMROOT / WINDIR），
    # 切勿用 "cygpath -w /"——那是 MSYS 安装盘（可能是 D:），不等于系统盘。
    if [[ "${SYSTEMDRIVE:-}" =~ ^[A-Za-z]:$ ]]; then
        drive="$SYSTEMDRIVE"
    elif [[ "${SYSTEMROOT:-}" == ?*:* ]]; then
        drive="${SYSTEMROOT%%\\*}"
    elif [[ "${WINDIR:-}" == ?*:* ]]; then
        drive="${WINDIR%%\\*}"
    else
        drive="$(winpath /)"
        drive="${drive%%\\*}"
    fi

    [[ -n "${USERPROFILE:-}" ]] || USERPROFILE="$(winpath "$HOME")"
    [[ -n "${APPDATA:-}" ]] || APPDATA="$(winpath "$HOME/AppData/Roaming")"
    [[ -n "${LOCALAPPDATA:-}" ]] || LOCALAPPDATA="$(winpath "$HOME/AppData/Local")"
    [[ -n "${HOMEDRIVE:-}" ]] || HOMEDRIVE="${USERPROFILE%%:*}"
    [[ -n "${HOMEPATH:-}" ]] || HOMEPATH="${USERPROFILE#*:}"
    [[ -n "${DOTNET_CLI_HOME:-}" ]] || DOTNET_CLI_HOME="$USERPROFILE"
    [[ -n "${NUGET_PACKAGES:-}" ]] || NUGET_PACKAGES="$USERPROFILE\\.nuget\\packages"
    export USERPROFILE APPDATA LOCALAPPDATA HOMEDRIVE HOMEPATH DOTNET_CLI_HOME NUGET_PACKAGES

    for name in SystemDrive SystemRoot windir ProgramFiles CommonProgramFiles ProgramData ALLUSERSPROFILE; do
        [[ -n "${!name:-}" ]] && continue
        case "$name" in
            SystemDrive)        cand="$drive" ;;
            SystemRoot|windir)  cand="$drive\\Windows" ;;
            ProgramFiles)       cand="$drive\\Program Files" ;;
            CommonProgramFiles) cand="$drive\\Program Files\\Common Files" ;;
            *)                  cand="$drive\\ProgramData" ;;
        esac
        [[ -e "$(posixpath "$cand")" ]] || continue
        export "$name=$cand"
    done
fi

if [[ -z "${TEMP:-}" ]]; then
    for cand in "$HOME/AppData/Local/Temp" "$HOME/.cache" "$ROOT/.build-tmp"; do
        [[ -n "$cand" ]] || continue
        mkdir -p "$cand" 2>/dev/null || continue
        [[ -w "$cand" ]] || continue
        export TEMP="$(winpath "$cand")"
        export TMP="$TEMP"
        export TMPDIR="$TEMP"
        echo "提示：TEMP 缺失，已改用 $TEMP"
        break
    done
fi

# ---------------------------------------------------------------------------
# 1. 前置检查：.NET SDK（§1.4 要求 .NET SDK 8.0；更高版本可向下编译 net8.0）
# ---------------------------------------------------------------------------
log "前置检查 (.NET SDK)"
command -v dotnet >/dev/null 2>&1 || die "未找到 dotnet：请安装 .NET SDK 8.0 或更高版本。"
echo "dotnet: $(dotnet --version)"

# ---------------------------------------------------------------------------
# 2. 验收标准 a) dotnet build 成功 —— 且"无警告"（把警告视为错误）
# ---------------------------------------------------------------------------
log "构建 SimCore + SimCore.Tests（$CONFIG，警告即错误）"
dotnet build "$SOLUTION" -c "$CONFIG" --nologo -warnaserror
ok "dotnet build 通过（无错误、无警告）"

# ---------------------------------------------------------------------------
# 3. 验收标准 b) dotnet test 输出 0 失败
# ---------------------------------------------------------------------------
log "运行单元测试（$CONFIG）"
dotnet test "$SOLUTION" -c "$CONFIG" --nologo --no-build
ok "dotnet test 通过（0 失败）"

# ---------------------------------------------------------------------------
# 4. 可选：验收标准 c) 两个 Godot .NET 工程 headless 打开无报错
#    需要设置 GODOT_BIN 指向 .NET(mono) 版 Godot 的 console 可执行文件。
# ---------------------------------------------------------------------------
if [[ -n "${GODOT_BIN:-}" ]]; then
    [[ -x "$GODOT_BIN" ]] || die "GODOT_BIN 不可执行：$GODOT_BIN"
    for proj in Game Editor; do
        # Game（游戏本体）不属于本仓库时跳过
        [[ -d "$ROOT/$proj" ]] || { warn "$proj 不在本仓库，跳过"; continue; }
        log "Godot 打开自检：$proj"
        # --headless：无窗口；--import：扫描并导入资源；随后退出。
        # 注意：Godot 即使报错也常返回退出码 0，因此必须同时检查日志内容。
        if ! out="$("$GODOT_BIN" --headless --path "$ROOT/$proj" --import --quit 2>&1)"; then
            printf '%s\n' "$out" | tail -30
            die "$proj 打开失败（Godot 退出码非 0）。"
        fi
        if printf '%s\n' "$out" | grep -qE '\.NET Sdk not found'; then
            printf '%s\n' "$out" | grep -nE '\.NET Sdk not found' | head -3
            warn "$proj：Godot 找不到匹配的 .NET SDK——属本机工具链问题（见 §1.4 前置准备），不是工程问题。"
            warn "      请安装 Godot 要求的 .NET SDK，或在设置了正确 DOTNET_ROOT 的终端里重跑。已跳过该检查。"
            continue
        fi
        if printf '%s\n' "$out" | grep -qE 'ERROR|错误|Unhandled exception'; then
            printf '%s\n' "$out" | grep -nE 'ERROR|错误|Unhandled exception' | head -20
            die "$proj 打开时报告了错误（见上）。"
        fi
        ok "$proj 打开成功（无报错）"
    done
else
    printf '\n提示：未设置 GODOT_BIN，跳过验收标准 c)。\n'
    printf '     例：GODOT_BIN="/d/Godot/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe" ./build.sh\n'
fi

printf '\n✓ T0.1 工程骨架与工具链 自检全部通过。\n'
