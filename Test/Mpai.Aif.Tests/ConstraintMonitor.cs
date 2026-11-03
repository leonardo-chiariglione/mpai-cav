using System.Text.Json.Nodes;

using Mpai.Cav.Ams.Planning;

namespace Mpai.Aif.Tests;

// THE MONITOR OF THE MOTION CONSTRAINTS (M3287, S-4 and V-1): it watches the recorded run of any strategy, from the simulation's truth, and measures each
// constraint whether the strategy was built to respect it or not.
public sealed class ConstraintMonitor(MotionConstraints constraints)
{
    private const double LaneHalf = 1.75;                   // m: further than this from the middle of its lane, a vehicle is out of it

    private double previousAcceleration, previousHeading, previousTime = double.NaN, speedNow;
    private double minGap = double.PositiveInfinity, minMargin = double.PositiveInfinity, secondsBelowSafe, maxOffset, secondsOut, maxLateral, maxOverLimit,
                   maxBrake, maxAcceleration, maxJerk, secondsOverNormalBrake, secondsStopped, secondsWithEmergency;
    private int emergencySteps, steps;
    private bool slid;

    public void Observe(JsonNode truth, double dt)
    {
        var ego = truth["Ego"]!;
        var v = (double)ego["Speed"]!; var a = (double)ego["Acceleration"]!; var heading = (double)ego["Heading"]! * Math.PI / 180;
        steps++; speedNow = v;

        // C1: the gap to the vehicle ahead in the lane against the safe distance at the speeds the two have
        foreach (var o in truth["Vehicles"]!.AsArray())
        {
            if ((int)o!["Lane"]! != 0) continue;
            var gap = (double)o["Distance"]!;
            if (gap < -1) continue;
            minGap = Math.Min(minGap, gap);
            var margin = gap - constraints.SafeLongitudinal(v, (double)o["Speed"]!);
            minMargin = Math.Min(minMargin, margin);
            if (margin < 0) secondsBelowSafe += dt;
        }

        // C2
        var offset = Math.Abs((double)ego["Offset"]!);
        maxOffset = Math.Max(maxOffset, offset);
        if (offset > LaneHalf) secondsOut += dt;

        // C4: the lateral acceleration is the speed times the rate of turn
        if (!double.IsNaN(previousTime))
        {
            var turn = Math.Abs(Math.IEEERemainder(heading - previousHeading, 2 * Math.PI)) / dt;
            maxLateral = Math.Max(maxLateral, v * turn);
            maxJerk = Math.Max(maxJerk, Math.Abs(a - previousAcceleration) / dt);
        }
        slid |= ego["Skidding"] is { } k && (bool)k;

        // C5
        maxOverLimit = Math.Max(maxOverLimit, v - (double)ego["SpeedLimit"]!);

        // C6, C7
        maxBrake = Math.Max(maxBrake, -a); maxAcceleration = Math.Max(maxAcceleration, a);
        if (-a > constraints.Normal.Brake) secondsOverNormalBrake += dt;
        if (-a > constraints.P.EmergencyThreshold) { emergencySteps++; secondsWithEmergency += dt; }

        // C8
        if (v < 0.1) secondsStopped += dt;

        (previousAcceleration, previousHeading, previousTime) = (a, heading, 0);
    }

    // What was measured, against the limits of the constraints.
    public JsonObject Report(bool arrived, bool collided)
    {
        static double R(double x) => double.IsInfinity(x) ? -1 : Math.Round(x, 2);
        var normal = constraints.Normal;
        return new JsonObject
        {
            ["Arrived"] = arrived, ["Collided"] = collided, ["Steps"] = steps,
            ["C1_MinGap_m"] = R(minGap), ["C1_MinMarginOverSafeDistance_m"] = R(minMargin), ["C1_SecondsBelowSafeDistance"] = R(secondsBelowSafe),
            ["C2_MaxOffsetFromLaneCentre_m"] = R(maxOffset), ["C2_SecondsOutOfLane"] = R(secondsOut),
            ["C4_MaxLateralAcceleration"] = R(maxLateral), ["C4_LateralLimit"] = R(normal.Lateral), ["C4_Slid"] = slid,
            ["C5_MaxOverSpeedLimit"] = R(maxOverLimit),
            ["C6_MaxBraking"] = R(maxBrake), ["C6_MaxAcceleration"] = R(maxAcceleration), ["C6_MaxJerk"] = R(maxJerk),
            ["C6_SecondsOverNormalBraking"] = R(secondsOverNormalBrake),
            ["C7_EmergencySteps"] = emergencySteps,
            ["C8_SecondsStopped"] = R(secondsStopped)
        };
    }
}
