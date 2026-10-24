# Viva notes - explain it bottom-up

## 1. Pixels (input)
- The roof camera renders 320x160 JPEG frames, ~20 per second, over local TCP.
- Preprocessing (preprocess.py): crop rows 56-134 (removes sky + hood), resize to
  200x66, scale to [-1, 1]. Training and live driving use the SAME function - a
  mismatch here is the classic way these projects silently fail.

## 2. Data
- ~8,500 frames, 29 episodes, recorded by hand with R (DataRecorder.cs):
  full laps + "recovery clips" - driving back to the lane centre from an
  off-centre position, so the network learns corrections, not just cruising.
- Labels: the human steering value (-1 left .. +1 right) for each frame.
- Why recovery clips matter: behaviour cloning teaches "what to do when the
  driver is already perfect" - recovery clips teach "what to do when things
  go wrong", which is what the network actually experiences live.

## 3. Network (model.py)
- DAVE-2 style CNN (NVIDIA's 2016 self-driving architecture, scaled down):
  5 conv layers (24/36/48/64/64 filters) + 4 dense layers (100/50/10/1),
  ELU activations, dropout 0.3, ~1 MB of weights.
- Input 3x66x200 -> output ONE number: steering. Regression, not classification.
- Why a CNN and not a big model: the task is simple lane following; a small net
  trains in minutes on a laptop GPU (RTX 3050) and runs at 4 ms/frame.

## 4. Training (train.py)
- MSE loss, Adam, augmentation: mirror flip (flip steering sign), brightness,
  and sideways shift up to 40 px with steering correction (+0.008 per px) -
  this simulates the car being off-centre and teaches it to steer back.
- Turn-weighted sampling: 61% of frames are nearly straight; sampling weight
  0.3 for straight, 2.0 for turns stops the network from "always go straight".
- Validation MSE 0.1480 vs 0.2187 always-zero baseline. Honest caveat: the
  validation set is tiny (261 frames); the real test is driving the loop.

## 5. The link (drive_server.py <-> NNDriver.cs)
- Protocol: Unity sends [4-byte length][JPEG], Python replies [4-byte float].
- The network runs on a background thread in Unity; the car applies the latest
  prediction at ~20 Hz with a 0.5 s staleness timeout.

## 6. Control + safety (CarController.cs, NNDriver.cs, PathMonitor.cs)
- Car: kinematic bicycle model (wheelbase 2.7 m, max steer 30 deg, 6 m/s max).
- The network only steers; speed is a rule: 5 m/s, 3.5 m/s within 15 m of a
  sharp corner (speed governor, computed from the recorded loop curvature).
- Safety state machine: if no fresh prediction for 0.5 s and Safety is ON:
  hold last steering, brake smoothly to a stop (~3.2 m from 5 m/s), switch to
  manual. With Safety OFF: steering goes to zero at speed - the car leaves the
  road. Run B vs run C is exactly this contrast.
- Crash monitor: the loop was recorded once (351 points); > 4 m from it =
  CRASH, the car is frozen, the event is logged to data/runs.csv.

## 7. Results (from data/runs.csv - real logged events, not from memory)
- Run A: network drives the loop (see RESULTS.md for the final logged run).
- Run B: network killed, safety OFF -> car goes straight, CRASH.
- Run C: network killed at the same place, safety ON -> STOPPED_SAFE ~3.2 m later.

## 8. Questions you should expect (and honest answers)

**Why does the car mostly turn left? Why no right turns?**
The demo loop is counterclockwise, so left turns dominate the data (~20% of
frames turn left vs ~8% right). That imbalance is exactly why the car first
crashed at the loop's hardest corner - the network had barely seen that turn.
I fixed it with recovery laps at that corner + turn-weighted sampling. A
production system would collect balanced data across many routes.

**Is it really the network driving, or scripted waypoints?**
Scripted would be: read path.csv, steer towards the next point. My network
never sees path.csv - it sees only 200x66 pixels and outputs steering. You can
prove it: press N (network off) mid-run and the car stops crashing differently
- and the failure at the untrained corner (before the fix) was a neural
failure, not a waypoint one.

**Why did it crash at the same corner every time?**
Behaviour cloning fails where the data is thin. That corner had almost no
training frames in the turning direction. The fix (recovery laps + fine-tune)
is the standard industry answer: collect data where the system fails.

**What happens if the server dies mid-drive?**
No reply for 0.5 s = network considered failed -> safety stop (~3.2 m from
5 m/s), manual control. Run C shows it live.

**Is the 3.2 m stop distance from the network?**
No - it comes from the braking settings (0.5 g at 5 m/s). The network has no
role in the stop; that is by design: the safety layer must not depend on the
thing that just failed.

**What are the limits?**
One loop, one lane, one lighting condition, no obstacles/pedestrians/traffic,
no generalisation to unseen roads. Simulation only.

## 9. If asked "what would you do next?"
- Balanced multi-route data collection, more augmentation (shadows, rain).
- Traffic: train with other cars in view (or a second network/rule layer for
  avoidance), speed control learned instead of ruled.
- Replace the single steering regression with trajectory prediction, and add a
  lane-keeping sanity check that can veto the network.
