# The junction round (CAV, 2026/10/14)

The two-way road of the oncoming round, with a road that crosses it at right angles. A vehicle turns - to the left or to the right - across
the CAV's way, and the CAV is to understand that it turns, and slow, instead of finding it in its lane. Three ways of knowing, the same turn:

| the CAV knows | how | what the CAV does |
|---|---|---|
| nothing | it sees a car in the other lane | goes on; the turning car is in its lane when it can do nothing: **collision** |
| the route | the car is a CAV and tells it: a Sender Info message of the Ego-Remote AMS Message - "the Trajectory the Ego CAV intends to adopt" (already in the standard) | slows from 14 s before the meeting, gently: 28 to 22 m/s, nearest approach 13 m |
| the indicator | the ESS sees it lit (read within 150 m: 3.5 s before they meet at these speeds) | slows late and hard (nearest approach 5 m, 17 m/s) - but does not collide |
| a vehicle that goes straight on, its route told | | holds its speed: the turn is not imagined |

## What was built

* `Test/CAV/HighwayJunction.cs`, `Highway.cs`: the junction (`Junction`), paths made of lines and arcs (`HighwayPath`), a staged vehicle that
  follows one at the speed of a profile (`HighwayWorld.Scripted`), its indicator (blinking 1.5 Hz from 40 m before the turn), oriented-box
  collision detection for such vehicles, the map with the junction (`HighwaySimulation.HighwayMap`).
* `AIMs/CAV/V2.0/Ams/FullEnvironmentDescription.cs`: the Sender Info message gives the entry of the Remote CAV its `PredictedPath`; an object whose
  `TurnIndicator` is seen lit (remembered 1 s: it blinks) gets one too, from the turn drivers make at the next junction of the Offline Map.
* `AIMs/CAV/V2.0/Ams/TrafficObstacleAvoidance.cs`: `Yield`: the CAV goes on straight and sees where it would meet a predicted path; it brakes as little as
  it takes to have 0.5 s more room than the least (1.5 s behind a crossing vehicle, 2 s of room where it would pass in front of one).
* `Test/Mpai.Aif.Tests/HighwayIntersectionTests.cs`: the scenarios and the tests (the CAV in the loop on perfect perception, as in the oncoming round).

## Changes to the standard's data (additive, optional)

1. `CAV2/V2.0/data/BasicEnvironmentDescriptors.json`: `TurnIndicator` ("Left" | "Right") in the Basic Environment Object: what the ESS reads of a vehicle's
   direction indicator. **Reason:** nothing there said it.
2. `CAV2/V2.0/data/FullEnvironmentDescriptors.json`: `PredictedPath` (T, East, North) in the Full Environment Object. **Reason:** where a vehicle is going.

## Not done yet

* The ESS does not read the indicator from the picture: the test gives it (perfect perception of a lit indicator within 150 m). Next: draw the lamps in
  the renderer and read them in the ESS.
* A turn to the right, a car that enters the highway from the cross road, a car ahead in the CAV's lane that turns off: the world has the paths for them
  (`Junction.Leading`, `Junction.FromSide`); the scenarios and tests are not written.
* The remote CAV's Trajectory comes from the test (a staged vehicle knows its path): where a CAV's own Trajectory goes to the Remote ones is not in the
  AMS.
* The yield assumes the CAV goes straight on: a CAV that turns itself is not covered.
