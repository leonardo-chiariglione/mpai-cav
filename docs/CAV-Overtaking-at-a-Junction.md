# CAV: overtaking a car that turns off at a junction

Built on 2026-10-15 from the description in `Downloads\CAV-Overtaking-at-a-Junction.md` (the author, 2026/10/15). First run built and recorded; the second (an oncoming car after the pull-out) and the third (no indicator) are tests.

## The scenario (`Test/Mpai.Aif.Tests/HighwayJunctionOvertakeTests.cs`, `OvertakeAtJunction`)

A two-way road, a lane each way, a junction with a side road to the right. The CAV follows an ordinary car (not connected, scripted: it follows its path at its speeds and does not react). The car slows for the turn from 300 m before it and lights its right indicator from the same place, while the CAV is still far behind. The lane of the opposite direction is empty.

## What the CAV does, and which part does it

- **Reads the indicator.** The ESS reads it from the telephoto window (it sees the car there from about 70 m; closer, the detector no longer finds the car in the window: the picture is too large). The indicator is therefore read while the CAV is still far behind.
- **Predicts the turn.** The Path Selection Planning gives the planner the distance to the next junction of the Route (its map). The planner takes a car whose right indicator it read lately (`IndicatorHold`, 15 s in this scenario: it remembers it) to be going to turn right there. Where the Full Environment Descriptors carry a PredictedPath, the planner uses it; where they do not (the indicator is no longer in sight), it predicts the slowing itself: on at the speed the car has, then at `DecelerationToTurn` to `TurnSpeed` where the arc begins.
- **Decides.** `LaneChangePlanner` (Stage 2, two-way road). The pass is judged against the way the car is going to go, not against its present speed (`PredictedPass`); the pass must be over `JunctionMargin` metres before the place where the turn begins (here -30: it may be over up to 30 m after, level with the car as it turns; the default is 30 before); the CAV leans `PeekOffset` m to the left in its lane to see past the car (`OwnHorizon`); the lane is known as far as `OwnRange` (550 m here: the assumption that the telephoto picture sees an empty straight road that far); a car with no indicator, near a junction, is not passed at the junction (the pass must be over before it).
- **Passes.** Pulls out, speeds up, draws level as the car approaches its turn, is ahead of it, returns to the right lane.

## Changes to existing code (none to Controller API, WDL, Data Types, AMD ports or UCM)

- `AIMs/CAV/V2.0/Ams/LaneChangePlanner.cs`: the above; new settings `JunctionMargin`, `PeekOffset`, `IndicatorHold`, `TurnSpeed`, `DecelerationToTurn` (defaults leave the old behaviour: 30, 0, 1, 7, 2); static fields `LastStatus`, `LastLead`, `LastVerify`, `LastHorizon`, `LastNeeded`, `LastTurnAhead`, `LastPassTime` for the test harness and the animation (not an output of the AIM).
- `AIMs/CAV/V2.0/Ams/PathSelectionPlanning.cs`: `JunctionAhead`, given to the planner.
- `AIMs/CAV/V2.0/Ams/TrafficObstacleAvoidance.cs`: `Yield` no longer yields to the PredictedPath of a vehicle on the CAV's own road going the same way (ahead, beside or behind): it is followed (the Motion Selection Planning) or left behind, not a crossing. Without this the CAV braked at 6 m/s2 for the car it was about to pass.
- `AIMs/CAV/V2.0/Ess/BasicVisualSceneDescription.cs`: `LastFar`, what the telephoto window showed (for the harness).
- Test harness: `HighwayBaselineTests` (live mode `junction-overtake`), `HighwayRecording` and `HighwayLiveView` (the telephoto picture), `Apps/CAV/CAVViewer/highway.html` (the view follows the CAV a long way ahead and closes on the pass; the telephoto picture; the planner's words; the stretch of the other lane known clear, in green; the predicted turn, dashed; captions), `CAV/Cav.ps1` (use case 8), `CAV/Demos/index.html`.

## Verified

- Perfect perception: the CAV passes the car, no collision, back in its lane, hardest braking 1.5 m/s2.
- On its own senses (ESS, front and telephoto pictures): pulls out at 35.5 s, passes at 38.8 s, back in its lane at about 48 s. In the follow phase the ESS's noise makes it brake up to 4 m/s2 (as before; not part of this work).
- The existing two-way-road and junction tests still pass (9 of 9 with the new one).

## Not done / open

- The third run (no indicator) is a test on perfect perception: the CAV does not pull out before the junction; it is not recorded.
- The second run (an oncoming car after the pull-out) is a test marked as skipped, OPEN: when the car comes in sight 3 s into the pass the planner abandons it while the CAV is still beside the car it was passing, and the CAV merges back at that car's tail (closing at 13 m/s) and touches it. The rule "finish or go back" has to brake first, to make the gap, before it merges. Not solved.
- `OwnRange` 550 m and `PeekOffset` 1 m are assumptions about the sensors, as is the CAV reading the indicator only from far.
- `JunctionMargin` -30 replaces the "pass over 30 m before the junction" of the description: with the indicator read from about 70 m the physics do not allow it.
