#!/usr/bin/env python3
"""性能门禁比较脚本：BenchmarkDotNet JSON 结果 vs 基线 JSON。

用法:
  python3 compare_benchmarks.py <results_dir> <baseline.json> [allowable_regression]

- results_dir: BenchmarkDotNet --artifacts-path（含每基准一个 JSON 文件）
- baseline.json: 仓库内基准基线（与 results 相同的 FullName → Mean 结构）
- allowable_regression: 允许的最大回归比例（默认 0.20 = 20%）

任一基准均值超过基线 × (1 + allowable_regression) 即退出码 1（CI 失败）。
缺失基线的基准输出警告（新基准需重新生成基线）。
"""

import json
import os
import sys


def load_benchmarks(results_dir: str) -> dict[str, float]:
    """递归解析 artifacts 目录下所有 BenchmarkDotNet JSON，返回 FullName -> Mean(ns)。"""
    out: dict[str, float] = {}
    if not os.path.isdir(results_dir):
        return out
    for root, _, files in os.walk(results_dir):
        for fname in files:
            if not fname.endswith(".json"):
                continue
            with open(os.path.join(root, fname), encoding="utf-8") as f:
                try:
                    data = json.load(f)
                except json.JSONDecodeError:
                    continue
            for bench in data.get("Benchmarks", []):
                full = bench.get("FullName")
                stats = bench.get("Statistics") or {}
                mean = stats.get("Mean")
                if full and mean is not None:
                    # BenchmarkDotNet 单位：TimeUnit 字段（ns/us/ms/s；缺省 ns）
                    unit = (bench.get("TimeUnit") or "ns").lower()
                    ns = float(mean) * {"ns": 1.0, "us": 1e3, "ms": 1e6, "s": 1e9}.get(unit, 1.0)
                    out[full] = ns
    return out


def main() -> int:
    if len(sys.argv) < 3:
        print(__doc__)
        return 2
    results_dir, baseline_path = sys.argv[1], sys.argv[2]
    allowable = float(sys.argv[3]) if len(sys.argv) > 3 else 0.20

    with open(baseline_path, encoding="utf-8") as f:
        baseline = json.load(f)

    results = load_benchmarks(results_dir)
    if not results:
        print(f"ERROR: 未解析到任何基准结果（目录: {results_dir}）")
        return 1

    failures = []
    for full, mean_ns in results.items():
        base = baseline.get(full)
        if base is None:
            print(f"WARN: 基准 {full} 无基线（请重新生成基线）")
            continue
        regression = (mean_ns - base) / base
        status = "OK" if regression <= allowable else "FAIL"
        print(f"{status} {full}: {mean_ns:.1f}ns vs 基线 {base:.1f}ns ({(regression * 100):+.1f}%)")
        if regression > allowable:
            failures.append(f"{full}: 回归 {(regression * 100):+.1f}%（允许 {allowable * 100:.0f}%）")

    if failures:
        print("性能门禁失败:")
        for f in failures:
            print(f"  - {f}")
        return 1
    print("性能门禁通过")
    return 0


if __name__ == "__main__":
    sys.exit(main())
