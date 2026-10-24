# Full procedure: from zero to a trained self-driving model

The whole pipeline, in order. Every command is copy-paste ready (Windows, cmd).

---

## Step 0 - Install

1. **Unity 6** (6000.6.x) via Unity Hub - open this project:
   `SelfDrivingCar/`
2. **Python 3.12** - then create the environment:

```
cd python
python -m venv venv
venv\Scripts\activate
pip install torch torchvision opencv-python numpy pandas matplotlib
```

Verify CUDA (training needs an NVIDIA GPU):

```
python -c "import torch; print(torch.cuda.is_available())"
```

3. **3D assets**: the .glb models (city + cars) are not in this repository -
   add your own `.glb` models into `SelfDrivingCar/Assets/` (city, one car with
   a camera on the roof). Sizes above 100 MB cannot be hosted by GitHub.

---

## Step 1 - Record the road loop (once)

The car needs to know the loop it should drive: PathMonitor measures how far
the car is from it, and the speed governor uses its curvature.

1. Start the Python server (see Step 4) - or skip it for recording.
2. Unity: press **Play**.
3. Drive to the start of the loop, press **K**, drive ONE clean full lap
   (centre of the road, ~5 m/s), press **K** again at the end.
4. This writes `data/path.csv` (one point per metre, ~351 points).

---

## Step 2 - Record training data (~30-60 min of driving)

The network learns by imitating you. Quality of data decides everything.

1. In the Hierarchy select **Car** → in the Inspector **tick
   Data Recorder (Script)**.
2. Press **Play**, press **R** → drive → press **R** to stop.
   Each R-start writes a new episode into `data/images/` +
   `data/labels.csv` (frame name, steering, speed, episode).
3. Record 3-5 full laps at ~5 m/s, then add **recovery clips**: from an
   off-centre position, steer back to the lane centre. The network must learn
   corrections, not just cruising.
4. Cover every corner several times - corners with unique visuals (open
   junctions, map edges) need the most passes. This project needed ~10 passes
   of its hardest corner (see `LIMITATIONS.md`).
5. When done: **untick Data Recorder** (so the network's own driving never
   enters the training set).

Aim for 6,000+ frames (~10 fps, 320x160 JPEG) before the first training.

---

## Step 3 - Train

```
cd python
venv\Scripts\activate
python train.py
```

What it does (train.py):

- Loads `data/images` + `data/labels.csv`, crops the sky/hood (rows 56-134),
  resizes to 200x66, scales to [-1, 1] (`preprocess.py` - the SAME function the
  live server uses).
- Augmentation: mirror flips (steering sign flipped), brightness changes, and
  a sideways shift up to 60 px with a proportional steering correction - this
  simulates the car being off-centre and teaches recovery.
- Sampling: straight frames get weight 0.3, gentle turns 1.0, real turns 2.0,
  and each lap's sharpest sustained turn gets 6.0 (turns are rare and matter).
- Splits validation laps from training laps, trains MSE + Adam, saves the best
  checkpoint to `best_model.pt` and the curve to `loss_curve.png`.

Options:

```
python train.py --epochs 25            (more epochs)
python train.py --lr 1e-4              (lower learning rate)
python train.py --init best_model.pt   (fine-tune from an existing model)
python train.py --init best_model.pt --lr 3e-5 --epochs 4 --out best_model_ft.pt
```

**Read the road test, not the validation score** - small noisy validation sets
do not predict closed-loop behaviour (see `RESULTS.md`, eight versions tested).

---

## Step 4 - Run the self-driving demo

1. Start the steering server:

```
cd python
venv\Scripts\activate
python drive_server.py
```

It waits on 127.0.0.1:5005. After ANY retraining, restart it.

2. Unity: press **Play**. The HUD shows "server connected".
3. Keys:

| Key | Action |
|-----|--------|
| **G** | network drives |
| **N** | network ON/OFF (simulated failure) |
| **Tab** | safety layer ON/OFF |
| **M** | manual control |
| **X** | reset car to start |
| **1/2/3** | label the run A/B/C in `data/runs.csv` |
| **K** | record the road loop |
| **R** | record training data |
| **V** | record video frames |

4. Press **G** - the dashcam image goes to the server at ~20 Hz, the network's
   steering comes back and drives the car. Speed is a rule: 5 m/s, 2.8 m/s near
   sharp corners (governor, `PathMonitor.cs`).
5. Press **N** mid-drive: with **Tab ON** the car brakes to a stop and hands
   over to manual (~1-3 m); with **Tab OFF** it keeps speed with zero steering
   and crashes - that contrast is the safety proof (`RESULTS.md`, runs B/C).

---

## Step 5 - Improve it (the loop we actually used)

1. Watch where it fails (CRASH rows in `data/runs.csv`).
2. Drive recovery laps around the failing spot, record them (Step 2).
3. Fine-tune gently: `python train.py --epochs 4 --lr 3e-5 --init best_model.pt`
4. Road-test. Repeat.
5. Honest warning from `RESULTS.md`: fine-tuning one corner can degrade
   another (catastrophic forgetting) - always re-test the whole loop, and keep
   the best road-tested model, not the best validation score.

## The honest result of this project

The shipped network drives ~230 m of the 358 m loop and fails at one
visually-unique map-edge corner. It covered the full loop once but not
reliably. Everything else - the safety layer, the contrast proof runs, the
pipeline - works. Full details: `RESULTS.md` + `LIMITATIONS.md`.
