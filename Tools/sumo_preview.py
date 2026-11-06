#!/usr/bin/env python3
"""一键用 SUMO 预览一次导出结果（可视化，默认 300 秒）。

## 用法

    python Tools/sumo_preview.py "导出测试/sumo/01-1"

    # 只跑命令行统计（不开窗口，适合对照数字）
    python Tools/sumo_preview.py "导出测试/sumo/01-1" --no-gui --end 600

    # 让画面更慢一点看清楚（每仿真步停 200 ms）
    python Tools/sumo_preview.py "导出测试/sumo/01-1" --delay 200

## 它会做什么

1. 找 `<base>.net.xml` 与 `<base>.rou.xml`（缺 net 直接报错退出）；
2. 同目录还有 `<base>.tll.xml` 就自动挂上（多套方案 / 感应控制才需要）；
3. 生成 `<base>.preview.sumocfg`（不覆盖你手写的 sumocfg）；
4. 起 `sumo-gui`（或 `sumo`），跑 `--end` 秒。

## 为什么单独造 sumocfg 而不复用编辑器的

编辑器目前**不产出** `.sumocfg`（那是 S5 的事）。这个脚本是过渡用的：
参数固定在"能直接看懂"的几项，不追求完整。
"""

from __future__ import annotations

import argparse
import shutil
import subprocess
import sys
from pathlib import Path

# SUMO 的常见安装位置（PATH 里找不到时按这个顺序试）
SUMO_DIRS = [
    Path(r"C:\Program Files (x86)\SUMO\bin"),
    Path(r"C:\Program Files\SUMO\bin"),
    Path(r"C:\Program Files (x86)\Eclipse\Sumo\bin"),
]


def find_exe(name: str) -> str | None:
    """先在 PATH 里找，再在常见安装目录里找。"""
    found = shutil.which(name)
    if found:
        return found
    for d in SUMO_DIRS:
        for candidate in (d / f"{name}.exe", d / name):
            if candidate.exists():
                return str(candidate)
    return None


def inspect_net(net: Path) -> tuple[set[str], set[tuple[str, str]]]:
    """读 net.xml：返回（所有 tlLogic 的 id, 所有 (id, programID) 组合）。"""
    import xml.etree.ElementTree as ET

    ids: set[str] = set()
    pairs: set[tuple[str, str]] = set()
    try:
        root = ET.parse(net).getroot()
    except ET.ParseError:
        return ids, pairs
    for tl in root.findall("tlLogic"):
        tid = tl.get("id", "")
        if tid:
            ids.add(tid)
            pairs.add((tid, tl.get("programID", "0")))
    return ids, pairs


def check_tll(tll: Path, net_ids: set[str]) -> tuple[bool, str]:
    """判断这份 `.tll.xml` 能不能跟当前 net 一起用。

    返回 (能不能挂, 说明)。宁可少挂一个，也不要让 SUMO 报
    "No initial signal plan loaded for tls 'X'" —— 那个灯在 net 里根本不存在。
    """
    import xml.etree.ElementTree as ET

    try:
        root = ET.parse(tll).getroot()
    except (ET.ParseError, OSError) as exc:
        return False, f"解析失败（{exc}）"

    tls = root.findall("tlLogic")
    if not tls:
        return False, "里面没有任何 tlLogic"

    missing = [tl.get("id", "?") for tl in tls if tl.get("id") not in net_ids]
    if missing:
        return False, ("这些灯在当前网络里不存在：{} —— 多半是**旧文件**"
                       "（关卡改过之后没重新导出）".format("、".join(missing)))

    has_waut = root.find("WAUT") is not None
    return (True, "多套方案（含 WAUT 分时段切换）" if has_waut
            else "单套附加程序（仅当你的专业档确实产了 tll 时才需要）")


def build_config(base: Path, end: float, seed: int, use_tll: bool,
                 has_add: bool = False) -> Path:
    """生成 preview 用的 sumocfg（写完就返回路径）。

    additional-files 可以有多个，SUMO 用逗号分隔。`.tll.xml`（多套信号方案）与
    `.add.xml`（检测器 / 公交站 / 停车场）互不相干，两边都要挂。
    """
    extras = []
    if use_tll:
        extras.append("{}.tll.xml".format(base.name))
    if has_add:
        extras.append("{}.add.xml".format(base.name))
    additional = ""
    if extras:
        additional = '\n    <additional-files value="{}"/>'.format(",".join(extras))

    text = """<configuration>
  <input>
    <net-file value="{name}.net.xml"/>
    <route-files value="{name}.rou.xml"/>{additional}
  </input>
  <time>
    <begin value="0"/>
    <end value="{end}"/>
    <step-length value="1"/>
  </time>
  <report>
    <no-step-log value="true"/>
    <duration-log.statistics value="true"/>
  </report>
  <random_number>
    <seed value="{seed}"/>
  </random_number>
</configuration>
""".format(name=base.name, end=end, seed=seed, additional=additional)

    path = base.with_suffix(".preview.sumocfg")
    path.write_text(text, encoding="utf-8")
    return path


def main() -> int:
    ap = argparse.ArgumentParser(description="用 SUMO 预览一次导出结果")
    ap.add_argument("base", help="导出 basename 或目录（不用带扩展名）")
    ap.add_argument("--end", type=float, default=300.0, help="仿真结束时刻（秒），默认 300")
    ap.add_argument("--delay", type=int, default=100,
                    help="GUI 每仿真步停多少毫秒（默认 100；0 = 全速）")
    ap.add_argument("--seed", type=int, default=42, help="随机种子")
    ap.add_argument("--no-gui", action="store_true", help="不开窗口，只跑命令行统计")
    ap.add_argument("--tll", choices=["auto", "on", "off"], default="auto",
                    help="要不要挂同目录的 .tll.xml（默认 auto：与网络配套才挂）")
    args = ap.parse_args()

    raw = Path(args.base).expanduser()
    if raw.is_dir():
        # 传了目录：取目录里第一个 .net.xml
        nets = sorted(raw.glob("*.net.xml"))
        if not nets:
            print(f"[x] 目录里没有 .net.xml：{raw}")
            return 1
        base = nets[0].with_suffix("").with_suffix("")
    else:
        base = raw.with_suffix("") if raw.suffix in (".xml", ".spmap") else raw
        # 兼容 "01-1.net.xml" 这种写法
        name = base.name
        if name.endswith(".net"):
            base = base.with_name(name[: -len(".net")])

    net = base.with_name(base.name + ".net.xml")
    rou = base.with_name(base.name + ".rou.xml")
    tll = base.with_name(base.name + ".tll.xml")
    add = base.with_name(base.name + ".add.xml")

    print(f"[i] 导出目录：{base.parent}")
    if not net.exists():
        print(f"[x] 找不到 {net.name} —— 先在编辑器里导出（勾上 SUMO .net.xml）")
        return 1
    if not rou.exists():
        print(f"[x] 找不到 {rou.name} —— 导出时勾上「SUMO .rou.xml（车流）」")
        return 1

    net_ids, _ = inspect_net(net)
    use_tll = False
    if tll.exists() and args.tll == "off":
        print(f"[i] 按参数跳过 {tll.name}")
    elif tll.exists() and args.tll == "on":
        use_tll = True
        print(f"[!] 强制加载 {tll.name}（--tll on）—— 与网络不配套时 SUMO 会报错")
    elif tll.exists():
        ok, why = check_tll(tll, net_ids)
        if ok:
            use_tll = True
            print(f"[i] 附加配时：{tll.name} —— {why}")
        else:
            print(f"[!] 跳过 {tll.name}：{why}")
            print("    如果这份 tll 确实该用，请回编辑器重新导出一次再跑。")

    # 检测设施：存在就挂（S3）。它是独立的 additional 文件，与 tll 不冲突。
    has_add = add.exists()
    if has_add:
        print(f"[i] 检测设施：{add.name}")

    cfg = build_config(base, args.end, args.seed, use_tll, has_add)
    print(f"[i] 配置：{cfg.name}（0 ~ {args.end:.0f} 秒，seed {args.seed}）")
    print(f"[i] 网络：{net.name}（信号灯 {len(net_ids)} 个）　车流：{rou.name}")

    exe_name = "sumo" if args.no_gui else "sumo-gui"
    exe = find_exe(exe_name)
    if exe is None:
        print(f"[x] 找不到 {exe_name} —— 确认 SUMO 已安装，或把它的 bin 目录加进 PATH")
        return 1

    cmd = [exe, "-c", cfg.name]
    if not args.no_gui:
        cmd += ["--delay", str(args.delay)]

    print(f"[i] 启动：{Path(exe).name} {' '.join(cmd[1:])}")
    print("[i] 可视化里：F9 加速 / F10 减速，右键车辆看详情；关掉窗口即结束\n")

    try:
        # cwd 设成导出目录，sumocfg 里的相对路径才解析得到
        return subprocess.call(cmd, cwd=str(base.parent))
    except KeyboardInterrupt:
        return 0


if __name__ == "__main__":
    sys.exit(main())
