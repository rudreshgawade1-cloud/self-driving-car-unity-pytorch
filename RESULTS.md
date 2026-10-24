# Results

All numbers are read from `data/runs.csv` - the event log Unity wrote during the
runs (crash monitor + safety state machine). Nothing is typed by hand. Dev-run
archives are kept next to it (`runs_dev_archive*.csv`, `runs_finals_attempt*.csv`).

## Proof runs (final recorded runs, 24 Oct 2026 - all three on video)

| Run | Network | Safety | What happened | Logged result |
|-----|---------|--------|---------------|---------------|
| A | ON | ON | Network drives; at the map-edge corner (x ~ -69) it leaves the road; car frozen | `CRASH` after 56 s / ~230 m, at 2.8 m/s |
| B | switched OFF at (-0.1, 118.4) | OFF | Zero steering, cruise keeps 5 m/s | `CRASH` 5.3 s after the kill, at 5.0 m/s |
| C | switched OFF at (-0.2, 118.5) - same spot (13 cm from B) | ON | Brakes immediately | `STOPPED_SAFE` 1.1 m after the kill |

B and C were killed at the same place (the two kill points are 13 cm apart on the
road) - the contrast is purely the safety layer.

## Run A - honest status

- The network reliably drives ~230 m of the 358 m loop (through several corners)
  and fails at ONE specific place: the map-edge corner at x ~ -69, z ~ 116, where
  the road is bordered by open void and the camera view is unlike any other corner.
- That corner appears in the training data only in a handful of laps. Recovery
  laps (8 extra passes) moved the crash point steadily deeper into the corner but
  did not eliminate it tonight.
- In one dev run the network DID cover the full loop distance (101.9 s, log:
  `runs_dev_archive2.csv`) but the run ended in a safety stop 17 m before the
  start line when the server connection stalled - so the lap is within reach, not
  yet a repeatable logged event.

## Safety stop statistics

Across all recorded safety stops tonight: stop distances 1.0-3.3 m from 5 m/s
(2.8 m/s near corners), zero collisions during any safety stop. The stop
distance comes from the braking rule (0.5 g), not from the network - by design:
the safety layer must not depend on the thing that just failed.

## Model versions tried (road-tested, not just validation score)

| Model | Training | Road result |
|-------|----------|-------------|
| original | 12 epochs, base augmentation | clean except the map-edge corner |
| full retrain #1 | re-weighted sampling | crashed immediately - rejected |
| ft2 | + stronger shift augmentation | same as original |
| ft3 | + 4 recovery laps, low lr | **completed a full loop once**; usually deepest into the corner - SHIPPED as `best_model.pt` |
| ft6 | + corner-frame boost (6x weight) | corner approach improved, still crashed |
| v4 | from scratch on all data | worst - rejected |
| ft7, ft8 | + 3 more recovery laps, + wobble damping, + 2.0 m/s corners | corner unchanged; a different corner broke each time |

Conclusion across 8 model versions: fine-tuning that teaches the map-edge
corner consistently degrades another region - classic catastrophic forgetting
in behaviour cloning. Fixing it properly needs sequence models (LSTM) or
replay-buffer training (mix old and new data), which is future work.

Lesson (real, and a good viva answer): validation MSE on a small noisy set does
NOT predict closed-loop driving quality. Only road tests decide.

## Videos

- `recordings/run_A_network_drives.mp4` - dashcam of the best Run A attempt
  (53 s: spawn -> several corners -> the map-edge corner failure).
- `recordings/run_B_safety_off_crash.mp4` - Run B: network switched off,
  safety OFF - car keeps 5 m/s with no steering and leaves the road (37 s).
- `recordings/run_C_safety_on_stops.mp4` - Run C: network switched off at the
  same spot, safety ON - car brakes to a stop in 1.1 m (36 s).
