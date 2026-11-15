"""Compare game speeds on the same save: run the save for N cycles at each setting and diff the end state.

    python accel_matrix.py --save "Live Human Testing.sav" --cycles 1
    python accel_matrix.py --save X.sav --cycles 2 --variants '[["1x",{"speed":1}],["3x",{"speed":3}],["3x-b",{"speed":3}],["10x",{"multiplier":10}],["20x",{"multiplier":20}]]'

Each variant is one probe run (game start, load, soak with snapshots, quit), so they run one after another. The first variant is the
baseline; a repeated setting (3x and 3x-b) shows how much two runs differ by themselves (the sim is not bit-for-bit repeatable).
Writes accel-matrix/<timestamp>/report.json (+ report.txt) next to this script and prints the text report.
"""
import argparse
import json
import sys
import time
from pathlib import Path

import probe

HERE = Path(__file__).resolve().parent
DEFAULT_VARIANTS = [["1x", {"speed": 1}], ["3x", {"speed": 3}], ["3x-b", {"speed": 3}], ["10x", {"multiplier": 10}]]
VITALS = ("calories", "health", "stress", "breath")


def pct(a, b):
    return None if not a else round((b - a) / a * 100, 3)


def load(run_dir, name):
    try:
        return json.loads((Path(run_dir) / name).read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None


def describe(label, out):
    soak = out.get("soak") or {}
    start, end = load(out["run_dir"], "snapshot_start.json"), load(out["run_dir"], "snapshot_end.json")
    return {"label": label, "ok": out.get("ok"), "stage": out.get("stage"), "error": out.get("error"), "run_dir": out.get("run_dir"),
            "soak": soak, "start": start, "end": end}


def compare(base, other):
    """Differences of `other` from `base` at the end of the run."""
    if not (base["end"] and other["end"]):
        return {"note": "missing end snapshot"}
    b, o = base["end"], other["end"]
    diff = {
        "game_time_s": [b["time"], o["time"]],
        "grid_mass_pct": pct(b["grid_mass"], o["grid_mass"]),
        "grid_mean_temperature_c_delta": round(o["grid_mean_temperature"] - b["grid_mean_temperature"], 3),
        "pickupable_mass_pct": pct(b["pickupable_mass"], o["pickupable_mass"]),
        "duplicant_count": [b["duplicant_count"], o["duplicant_count"]],
    }
    elements = {}
    for el in set(b["grid_mass_by_element"]) | set(o["grid_mass_by_element"]):
        x, y = b["grid_mass_by_element"].get(el, 0), o["grid_mass_by_element"].get(el, 0)
        if x and abs(y - x) / x > 0.01 and abs(y - x) > 50:
            elements[el] = [x, y]
    diff["elements_changed_over_1pct"] = elements
    names_b = {d["name"]: d for d in b["duplicants"]}
    dup = {}
    for d in o["duplicants"]:
        if d["name"] in names_b:
            dup[d["name"]] = {v: [names_b[d["name"]].get(v), d.get(v)] for v in VITALS}
    diff["duplicants"] = dup
    diff["lost_duplicants"] = sorted(set(names_b) - {d["name"] for d in o["duplicants"]})
    return diff


def text_report(results, diffs, args):
    lines = [f"save={args.save}  cycles={args.cycles}  chunk={args.chunk}s", ""]
    for r in results:
        s = r["soak"]
        lines.append(f"[{r['label']}] ok={r['ok']} stage={r['stage']} {r.get('error') or ''}")
        if s:
            lines.append(f"  stop={s.get('stop_reason')} cycles={s.get('cycles_advanced')} real={s.get('real_seconds')}s "
                         f"({s.get('game_s_per_real_s')}x) fps={s.get('fps')} mem_max={s.get('working_set_mb_max')}MB "
                         f"new_log_exceptions={s.get('new_log_exceptions')}")
        e = r["end"]
        if e:
            lines.append(f"  end: t={e['time']:.0f}s grid_mass={e['grid_mass']} temp={e['grid_mean_temperature']} "
                         f"items={e['pickupable_count']} dupes={e['duplicant_count']}")
    lines.append("")
    for label, d in diffs.items():
        lines.append(f"vs baseline [{results[0]['label']}] -> [{label}]")
        for k, v in d.items():
            if k == "duplicants":
                for name, vit in v.items():
                    lines.append("  " + name + ": " + ", ".join(f"{n} {a if a is None else round(a, 1)}->{b if b is None else round(b, 1)}" for n, (a, b) in vit.items()))
            else:
                lines.append(f"  {k}: {v}")
    return "\n".join(lines)


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--save", required=True)
    ap.add_argument("--cycles", type=float, default=1)
    ap.add_argument("--chunk", type=float, default=60, help="game seconds per sample")
    ap.add_argument("--max-minutes", type=float, default=60, help="real-time cap per variant")
    ap.add_argument("--variants", help="JSON list of [label, soak options]")
    ap.add_argument("--project", action="append", help="harness.json of a mod to load along with the harness (default: harness only)")
    args = ap.parse_args()
    variants = json.loads(args.variants) if args.variants else DEFAULT_VARIANTS
    cfg = probe.load_config(args.project)
    out_dir = HERE / "accel-matrix" / time.strftime("%Y%m%d-%H%M%S")
    out_dir.mkdir(parents=True)
    results = []
    for label, opts in variants:
        opts = dict(opts, max_cycles=args.cycles, max_minutes=args.max_minutes, chunk_seconds=args.chunk, snapshots=True)
        print(f"== {label}: {opts}", flush=True)
        out = probe.run_cycle(cfg, save=args.save, soak=opts)
        results.append(describe(label, out))
        print(f"   ok={out.get('ok')} stage={out.get('stage')} {out.get('error') or ''}", flush=True)
        if not out.get("ok") and out.get("stage") not in ("soak",):
            break  # the game does not start / load: more variants would fail the same way
    diffs = {r["label"]: compare(results[0], r) for r in results[1:]}
    (out_dir / "report.json").write_text(json.dumps({"args": vars(args), "results": results, "diffs": diffs}, indent=1, ensure_ascii=False), encoding="utf-8")
    text = text_report(results, diffs, args)
    (out_dir / "report.txt").write_text(text, encoding="utf-8")
    print(text)
    print(f"\nreport: {out_dir}")
    return 0 if all(r["ok"] for r in results) else 1


if __name__ == "__main__":
    sys.exit(main())
