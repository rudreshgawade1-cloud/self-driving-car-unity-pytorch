"""Train the steering CNN on data recorded by DataRecorder.cs.

Run from the python folder:   python train.py
Optional:                     python train.py --data D:\\self_driving_car\\data --epochs 25

Version 2: adds a sideways-shift augmentation (SHIFT_PX / STEER_PER_PX below) that
teaches the network to steer back towards the lane centre.
"""
import argparse
import os
import time

import cv2
import matplotlib
import numpy as np
import pandas as pd
import torch
import torch.nn as nn
from torch.utils.data import DataLoader, Dataset, WeightedRandomSampler

matplotlib.use("Agg")
import matplotlib.pyplot as plt

from model import SteeringNet
from preprocess import preprocess_uint8, to_tensor_array, OUT_W, OUT_H

HERE = os.path.dirname(os.path.abspath(__file__))

# Sideways-shift augmentation (simulates the car being off-centre)
SHIFT_PX = 60          # max shift in pixels of the 200 px wide image
STEER_PER_PX = 0.008   # steering correction added per pixel of shift (60 px -> 0.48)
SHIFT_PROB = 0.7       # fraction of training samples that get shifted


def load_data(data_dir):
    csv_path = os.path.join(data_dir, "labels.csv")
    img_dir = os.path.join(data_dir, "images")
    df = pd.read_csv(csv_path)
    keep, imgs, missing = [], [], 0
    for i, row in df.iterrows():
        s = row["steering"]
        if not np.isfinite(s) or abs(s) > 1.0:
            continue
        p = os.path.join(img_dir, row["image"])
        im = cv2.imread(p) if os.path.exists(p) else None
        if im is None:
            missing += 1
            continue
        imgs.append(preprocess_uint8(cv2.cvtColor(im, cv2.COLOR_BGR2RGB)))
        keep.append(i)
    df = df.loc[keep].reset_index(drop=True)
    print(f"Loaded {len(df)} frames ({missing} rows skipped: image missing/unreadable)")
    return df, np.stack(imgs)


class DrivingSet(Dataset):
    def __init__(self, X, y, augment):
        self.X, self.y, self.augment = X, y.astype(np.float32), augment

    def __len__(self):
        return len(self.y)

    def __getitem__(self, i):
        img, y = self.X[i], float(self.y[i])
        if self.augment:
            if np.random.rand() < SHIFT_PROB:          # sideways shift + steering correction
                dx = int(np.random.randint(-SHIFT_PX, SHIFT_PX + 1))
                M = np.float32([[1, 0, dx], [0, 1, 0]])
                img = cv2.warpAffine(img, M, (OUT_W, OUT_H), borderMode=cv2.BORDER_REPLICATE)
                y = float(np.clip(y + dx * STEER_PER_PX, -1.0, 1.0))
            if np.random.rand() < 0.5:                 # mirror image, flip steering sign
                img, y = img[:, ::-1, :].copy(), -y
            if np.random.rand() < 0.5:                 # brightness change
                f = np.random.uniform(0.7, 1.3)
                img = np.clip(img.astype(np.float32) * f, 0, 255).astype(np.uint8)
        return torch.from_numpy(to_tensor_array(img)), torch.tensor(y, dtype=torch.float32)


def evaluate(model, loader, device):
    model.eval()
    se, ae, n = 0.0, 0.0, 0
    with torch.no_grad():
        for x, y in loader:
            x, y = x.to(device), y.to(device)
            p = model(x)
            se += ((p - y) ** 2).sum().item()
            ae += (p - y).abs().sum().item()
            n += len(y)
    return se / n, ae / n


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", default=os.path.join(HERE, "..", "data"))
    ap.add_argument("--epochs", type=int, default=25)
    ap.add_argument("--batch", type=int, default=64)
    ap.add_argument("--lr", type=float, default=1e-3)
    ap.add_argument("--out", default=os.path.join(HERE, "best_model.pt"))
    ap.add_argument("--init", default=None, help="start from an existing checkpoint (fine-tune)")
    args = ap.parse_args()

    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    print("Device:", device, "| CUDA available:", torch.cuda.is_available())

    df, X = load_data(args.data)
    y = df["steering"].values
    eps = sorted(df["episode"].unique())
    print("Episodes found:", eps)

    # Split so validation frames are never next to training frames:
    # whole laps if there are 5 or more, otherwise 20-second blocks (200 frames) in recording order.
    if len(eps) >= 5:
        val_eps = eps[4::5]
        is_val = df["episode"].isin(val_eps).values
        split_note = f"validation laps {val_eps}"
    else:
        block = (df.groupby("episode").cumcount() // 200).values
        key = df["episode"].values * 1000 + block
        uniq = sorted(set(key.tolist()))
        val_keys = set(uniq[4::5]) if len(uniq) >= 5 else set(uniq[-1:])
        is_val = np.array([k in val_keys for k in key])
        split_note = f"{len(val_keys)} validation blocks of 200 frames"
    Xtr, ytr, Xva, yva = X[~is_val], y[~is_val], X[is_val], y[is_val]
    if len(ytr) == 0 or len(yva) == 0:
        raise SystemExit("Not enough data for a train/validation split. Record more driving.")
    print(f"Train {len(ytr)} frames | Val {len(yva)} frames | {split_note}")
    print(f"Steering: mean {y.mean():+.3f}, std {y.std():.3f}, "
          f"|steer|<0.05: {np.mean(np.abs(y) < 0.05) * 100:.0f}% of frames")
    print(f"Baseline (always predict 0) val MSE: {np.mean(yva ** 2):.4f}")
    print(f"Shift augmentation: up to +/-{SHIFT_PX} px, {STEER_PER_PX} steering per px, on {SHIFT_PROB * 100:.0f}% of samples")

    # Straight-road frames dominate; sample them less often, real turns more often.
    # The sharpest sustained turn of each lap (the big corner) gets a heavy boost,
    # because it is rare in the data and visually ambiguous (open junction).
    w = np.where(np.abs(ytr) < 0.05, 0.3, np.where(np.abs(ytr) < 0.2, 1.0, 2.0))
    boost = np.zeros(len(ytr), dtype=bool)
    eps_arr = df["episode"].values[~is_val]
    for ep in pd.unique(eps_arr):
        ep_idx = np.where(eps_arr == ep)[0]
        s = ytr[ep_idx]
        m = s < -0.2   # sustained left turns (the loop's hard corner is left)
        runs, start = [], None
        for i, v in enumerate(m):
            if v and start is None: start = i
            if not v and start is not None:
                runs.append((start, i)); start = None
        if start is not None: runs.append((start, len(m)))
        runs = [r for r in runs if r[1] - r[0] >= 12]
        if runs:
            best = max(runs, key=lambda r: r[1] - r[0])
            boost[ep_idx[best[0]:best[1]]] = True
    w[boost] = 6.0
    print(f"Corner boost: {boost.sum()} frames at weight 6.0")
    sampler = WeightedRandomSampler(w, num_samples=len(w), replacement=True)
    tr = DataLoader(DrivingSet(Xtr, ytr, True), batch_size=args.batch, sampler=sampler, num_workers=0)
    va = DataLoader(DrivingSet(Xva, yva, False), batch_size=args.batch, num_workers=0)

    model = SteeringNet().to(device)
    if args.init:
        model.load_state_dict(torch.load(args.init, map_location=device, weights_only=True)["model"])
        print(f"Fine-tuning from {args.init}")
    opt = torch.optim.Adam(model.parameters(), lr=args.lr)
    loss_fn = nn.MSELoss()

    best, hist_tr, hist_va = 1e9, [], []
    for ep in range(1, args.epochs + 1):
        t0 = time.time()
        model.train()
        run, n = 0.0, 0
        for x, yb in tr:
            x, yb = x.to(device), yb.to(device)
            opt.zero_grad()
            loss = loss_fn(model(x), yb)
            loss.backward()
            opt.step()
            run += loss.item() * len(yb)
            n += len(yb)
        tr_loss = run / n
        va_mse, va_mae = evaluate(model, va, device)
        hist_tr.append(tr_loss)
        hist_va.append(va_mse)
        flag = ""
        if va_mse < best:
            best = va_mse
            torch.save({"model": model.state_dict(), "val_mse": va_mse, "val_mae": va_mae}, args.out)
            flag = "  <- saved"
        print(f"Epoch {ep:2d}/{args.epochs}  train {tr_loss:.4f}  val MSE {va_mse:.4f}  "
              f"val MAE {va_mae:.4f}  ({time.time() - t0:.0f}s){flag}")

    plt.figure(figsize=(7, 4))
    plt.plot(hist_tr, label="train loss")
    plt.plot(hist_va, label="validation loss")
    plt.xlabel("epoch"); plt.ylabel("MSE"); plt.legend(); plt.grid(alpha=0.3)
    plt.title("Steering CNN training")
    plt.tight_layout()
    plt.savefig(os.path.join(HERE, "loss_curve.png"), dpi=120)
    print(f"Done. Best val MSE {best:.4f}. Saved {args.out} and loss_curve.png")


if __name__ == "__main__":
    main()
