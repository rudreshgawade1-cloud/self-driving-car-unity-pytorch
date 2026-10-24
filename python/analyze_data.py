"""One-off diagnostic: where is the crash point on the loop, how sharp is it,
and what does the recorded steering data look like. Safe to delete."""
import os
import numpy as np
import pandas as pd

HERE = os.path.dirname(os.path.abspath(__file__))
DATA = os.path.join(HERE, "..", "data")

# ---- path.csv: the recorded loop ----
p = pd.read_csv(os.path.join(DATA, "path.csv"))
xy = p[["x", "z"]].values.astype(float)
seg = np.diff(xy, axis=0)
seglen = np.hypot(seg[:, 0], seg[:, 1])
total = seglen.sum()
print(f"Path: {len(xy)} points, loop length {total:.0f} m")

def frac_along(idx):
    return seglen[:idx].sum() / total

# bearing change per point (degrees), sign = turn direction
v1, v2 = seg[:-1], seg[1:]
a1 = np.arctan2(v1[:, 1], v1[:, 0])
a2 = np.arctan2(v2[:, 1], v2[:, 0])
d = np.degrees((a2 - a1 + np.pi) % (2 * np.pi) - np.pi)  # signed turn per 1 m
curv = np.abs(d)

# crash point from runs.csv
crash = np.array([-66.0, 119.0])
dist = np.hypot(xy[:, 0] - crash[0], xy[:, 1] - crash[1])
ci = int(dist.argmin())
print(f"Crash point (-66,119): nearest path point #{ci} "
      f"({frac_along(ci)*100:.0f}% around the loop, {dist[ci]:.1f} m off path)")
print(f"  turn within +/-15 m of crash: {curv[max(0,ci-15):ci+15].sum():.0f} deg total "
      f"(direction {'left' if d[max(0,ci-15):ci+15].mean() < 0 else 'right'})")

sharp = np.argsort(curv)[::-1][:400]  # cluster sharpest points
clusters, used = [], set()
for i in sorted(sharp):
    if any(abs(int(i) - c) < 20 for c in clusters):
        continue
    clusters.append(int(i))
print("\nSharpest corners (top 5):")
for c in sorted(clusters)[:5]:
    win = curv[max(0, c - 5):c + 5]
    print(f"  #{c} at ({xy[c,0]:6.1f},{xy[c,1]:6.1f})  {frac_along(c)*100:4.0f}% around  "
          f"turn {d[max(0,c-5):c+5].sum():+6.0f} deg over 10 m")

# ---- labels.csv: steering stats ----
df = pd.read_csv(os.path.join(DATA, "labels.csv"))
print(f"\nLabels: {len(df)} rows, {df['episode'].nunique()} episodes "
      f"({sorted(df['episode'].unique())})")
print(df.groupby("episode")["steering"].agg(["count", "mean", "std"]).round(3).to_string())
s = df["steering"].values
print(f"\nSteering: mean {s.mean():+.3f} std {s.std():.3f} min {s.min():+.2f} max {s.max():+.2f}")
for lo, hi, name in [(0, 0.05, "|s|<0.05 (straight)"), (0.05, 0.2, "0.05-0.2 (gentle)"),
                     (0.2, 0.45, "0.2-0.45 (turn)"), (0.45, 2, "|s|>0.45 (hard)")]:
    print(f"  {name:24s} {((np.abs(s) >= lo) & (np.abs(s) < hi)).mean()*100:5.1f}%")
print(f"  left turns (s<-0.2): {(s < -0.2).mean()*100:5.1f}%   right turns (s>0.2): {(s > 0.2).mean()*100:5.1f}%")
