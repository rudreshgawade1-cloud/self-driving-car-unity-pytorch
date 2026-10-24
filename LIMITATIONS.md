# Limitations and downsides (full, honest list)

Read this before forming an opinion of the project. Everything here is real,
observed during testing, and documented in `RESULTS.md`.

## 1. The network does not finish a lap reliably

- It drives ~230 m of the 358 m loop cleanly, then fails at one specific
  corner: the map-edge corner at x ~ -69, z ~ 116.
- Why there: the road at that corner is bordered by open void (the edge of the
  simulation map), and the junction's zebra crossing makes "go straight" look
  correct to the network. It is the only corner in the city that looks like
  that, and it has the fewest training examples.
- It covered the full loop distance once (101.9 s, archived log), but a
  repeatable, logged lap does not exist yet.

## 2. No temporal memory - the steering wobbles

- Each frame is predicted independently. Tiny image changes (shadows, JPEG
  noise) flip the steering slightly, so the car sways.
- Fix would be an output filter, or a recurrent model (LSTM) that sees the
  last few frames.

## 3. Catastrophic forgetting

- Eight model versions are documented in `RESULTS.md`. Fine-tuning that
  improved the weak corner repeatedly degraded another corner instead.
- Proper fixes (sequence models, replay-buffer training) are future work.

## 4. Behaviour cloning copies the human

- The steering labels contain the human driver's hand jitter, and the network
  learns that noise as part of the policy.

## 5. Scope limits

- One loop, one lane, one lighting condition. ~15,000 frames from one city.
- The network cannot see or avoid other cars or pedestrians.
- It does not generalise: on an unseen road it would likely fail immediately.

## 6. Control is only partly "intelligent"

- The network only steers. Speed is a rule: 5 m/s, 2.8 m/s within 25 m of a
  sharp corner (a curvature-based governor computed from the recorded loop).
- The safety-stop distance (1.0-3.3 m from 5 m/s) comes from the braking
  settings, not from the network - by design: the safety layer must not depend
  on the thing that just failed.

## 7. Simulation only

- Kinematic bicycle model, no physics collisions, no sensor noise modelling.
- Nothing in this repository applies directly to a real vehicle.

## What the project DOES prove

- A complete, working autonomy pipeline: dashcam camera -> CNN -> steering ->
  vehicle, over a live TCP link at ~20 Hz.
- A tested safety layer: brake-and-stop on network failure, demonstrated with
  contrast runs B (safety off -> crash) and C (safety on -> safe stop) from the
  same kill point, 13 cm apart.
- An honest, data-backed results log: every claim in `RESULTS.md` comes from
  `data/runs.csv`, written by Unity during the runs.
