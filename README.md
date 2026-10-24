# Virtual Self-Driving Car (Unity + PyTorch)

A simulated self-driving car: a small convolutional neural network (CNN) looks at the
car's dashcam image and predicts a steering value; Unity applies it to the car.
A local TCP link connects Unity (C#) and the model server (Python/PyTorch).
If the network fails, a safety layer brakes the car to a stop and hands control
back to the driver. **Simulation only — not for real roads.**

## Architecture

```
DashCam (320x160 JPEG, ~20 fps)
      |  TCP 127.0.0.1:5005
      v
Python server (drive_server.py)
      |  crop (rows 56-134) + resize to 200x66, normalise to [-1,1]
      v
CNN (model.py, DAVE-2 style: 5 conv + 4 dense, ~9 MB)
      |  one float: steering -1 (left) .. +1 (right)
      v
NNDriver.cs  ->  CarController.cs (kinematic bicycle model, max 30 deg steer)
```

- **The network only steers.** Speed is a rule: hold 5 m/s, reduced to 3.5 m/s
  while a sharp corner is within ~15 m ahead (speed governor, PathMonitor.cs).
- **Safety layer (NNDriver.cs):** if no steering reply arrives within 0.5 s
  (server dead, network off), and Safety is ON: hold last steering, brake at
  0.5 g, stop, hand over to manual. With Safety OFF the car keeps its speed
  with zero steering - it drifts off the road (the contrast run B shows why).
- **Crash monitor (PathMonitor.cs):** the road loop is recorded once (351
  points, ~358 m); if the car gets > 4 m from the loop it is frozen and a CRASH
  row is appended to `data/runs.csv`. LAP_COMPLETE is logged when the car has
  covered >= ~90% of the loop and returns to its start.

## Files

| Path | What it is |
|---|---|
| `SelfDrivingCar/` | Unity 6 project (scene: `Assets/Scenes/SampleScene.unity`) |
| `python/model.py` | CNN definition |
| `python/train.py` | Training (mirror/shift/brightness augmentation, turn-weighted sampling) |
| `python/drive_server.py` | Live TCP steering server |
| `python/best_model.pt` | Final weights |
| `data/images`, `data/labels.csv` | ~8,500 dashcam frames with steering labels, 29 episodes |
| `data/runs.csv` | Event log of the proof runs |
| `recordings/` | Dashcam frame captures of the demo runs |

## How to run

The full end-to-end procedure (setup, recording data, training, demo): **`TRAINING.md`**.

1. Start the server: `cd python`, `venv\Scripts\activate`, `python drive_server.py`
   (waits for Unity on 127.0.0.1:5005).
2. Open the Unity project, press Play.
3. Keys: **G** network drives | **N** network ON/OFF (simulated failure) |
   **Tab** safety ON/OFF | **M** manual | **X** reset | **1/2/3** label run A/B/C |
   **R** record training data | **K** record road loop | **V** record video frames.

## Training

- Data: ~8,500 frames recorded by hand in the scene (several full laps + recovery
  clips around difficult corners), 10 fps, steering from the human driver.
- Augmentation: sideways shift up to 40 px with proportional steering correction
  (teaches recovery to the lane centre), mirror flip, brightness change.
- Sampling: straight frames (|steer| < 0.05) down-weighted to 0.3, turn frames
  (|steer| >= 0.2) up-weighted 2.0 - the loop has more straight and left-turn
  driving than turns to the right.
- Result: validation MSE **0.1480** vs **0.2187** for an always-zero baseline
  (~32% better; validation set is small and noisy - the road test is what counts).
- The final model = first training + a 6-epoch fine-tune (lr 1e-4) on two extra
  recovery laps recorded at the corner where the car originally failed.

## Results

See `RESULTS.md` (filled from `data/runs.csv` after the final recorded runs).

## Known limitations (stated honestly)

- One loop, one lane, one lighting condition; small dataset.
- The network cannot see or avoid other cars or pedestrians.
- The steering network was trained only on this loop; on unseen roads it would
  not generalise.
- Speed control is a rule, not learned; the 3.2 m safety-stop distance comes
  from the braking settings, not from the network.
- Full downsides and current failures: see `LIMITATIONS.md`.
