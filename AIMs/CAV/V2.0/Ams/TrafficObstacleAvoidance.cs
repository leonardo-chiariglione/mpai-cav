using System.Text.Json.Nodes;

using AIF.Controller;

using Mpai.Cav.Ess;

namespace Mpai.Cav.Ams;

// TRAFFIC OBSTACLE AVOIDANCE, STAGE 1 (CAV-TOA; M3233 3.7). Each Trajectory refined
// where an Alert came within the last AlertWindow ms, or where the object ahead in the
// CAV's lane - in the latest Full Environment Descriptors - would be reached within
// TimeToCollision seconds: the deceleration that stops the closing before the gap
// falls below StopGap metres, at least the Trajectory's own, at most Emergency m/s2,
// held over the Trajectory. The result is the AMS-MAS Message, the Command Execute.
public sealed class TrafficObstacleAvoidance(string instanceId, IReadOnlyDictionary<string, string> settings) : IAimProcessor, IAimRunner
{
    private readonly double ttc = EssJson.Setting(settings, "TimeToCollision", 2), emergency = EssJson.Setting(settings, "Emergency", 6),
                            stopGap = EssJson.Setting(settings, "StopGap", 2), alertWindow = EssJson.Setting(settings, "AlertWindow", 300);
    private JsonNode? fed;
    private long? lastAlert;
    private long count;

    public string InstanceId { get; } = instanceId;
    public Task<Message> ProcessAsync(Message message) => throw new NotSupportedException("CAV-TOA runs continuously.");

    public async Task RunAsync(IAimPorts ports, AimContext context)
    {
        while (await ports.SelectAsync(-1, (AmsTypes.Alert, 1), (AmsTypes.Fed, 1), (AmsTypes.Trajectory, 1)) is { } port)
        {
            if (await ports.ReadAsync(port.DataType, 1, 0) is not { } m) continue;
            var json = JsonNode.Parse(m.Json)!;
            switch (port.DataType)
            {
                case AmsTypes.Alert: Alerted(json); continue;
                case AmsTypes.Fed: Observe(json); continue;
            }
            await ports.WriteAsync(AmsTypes.Message, 1, Refine(json).ToJsonString());
        }
    }

    public void Observe(JsonNode fed) => this.fed = fed;
    public void Alerted(JsonNode alert) => lastAlert = Math.Max(lastAlert ?? long.MinValue, AmsTypes.Ms(alert["AlertTime"]));

    public JsonObject Refine(JsonNode trajectory)
    {
        var ms = AmsTypes.Ms(trajectory["TrajectoryTime"]);
        var refined = trajectory.DeepClone();
        var points = refined["Trajectory"]!.AsArray();
        var speed = Speed(points[0]!);
        var own = points.Count > 1 ? (Speed(points[1]!) - speed) / MotionSelectionPlanning.Step : 0;

        // The object ahead in the CAV's lane, and how soon it would be reached.
        double? gap = null, closing = null;
        foreach (var o in fed?["FullEnvironmentObjects"]?.AsArray() ?? [])
        {
            if ((int?)o?["Placement"]?["Lane"] != 0) continue;
            var p = EssJson.Vector(o!["BasicEnvironmentObject"]!["SpatialAttitude"]?["Position"]?["CartPosition"]);
            var v = EssJson.Vector(o["BasicEnvironmentObject"]!["SpatialAttitude"]?["Position"]?["CartVelocity"]);
            if (p is null || p.Value.X <= 0 || (gap is not null && p.Value.X >= gap)) continue;
            (gap, closing) = (p.Value.X, -(v?.X ?? 0));
        }
        var alerted = lastAlert is { } last && ms - last <= alertWindow;
        var danger = gap is { } g && closing is { } c && c > 0.1 && g / c < ttc;
        if (!alerted && !danger) return Message(refined, ms);

        // The deceleration that ends the closing before the gap falls below StopGap.
        var needed = gap is { } g2 && closing is { } c2 && c2 > 0 ? c2 * c2 / (2 * Math.Max(0.5, g2 - stopGap)) : 0;
        var brake = Math.Min(emergency, Math.Max(needed, -own));
        if (brake <= -own + 0.01) return Message(refined, ms);

        // Held over the Trajectory: each point's speed from the braking, its position along the way.
        double v0 = speed, travelled = 0;
        var (e0, n0, h0) = Position(points[0]!);
        for (var k = 0; k < points.Count; k++)
        {
            var sa = points[k]!["ExpectedSpaceTime"]!["SpatialAttitude1"]!["Position"]!;
            var h = Heading(points[k]!, h0);
            if (k > 0)
            {
                var next = Math.Max(0, v0 - brake * MotionSelectionPlanning.Step);
                travelled += (v0 + next) / 2 * MotionSelectionPlanning.Step;
                v0 = next;
            }
            sa["CartPosition"] = EssJson.Triple((e0 + travelled * Math.Cos(h0), n0 + travelled * Math.Sin(h0), 0));
            sa["CartVelocity"] = EssJson.Triple((v0 * Math.Cos(h), v0 * Math.Sin(h), 0));
            sa["CartAccel"] = EssJson.Triple((-brake * Math.Cos(h), -brake * Math.Sin(h), 0));
        }
        return Message(refined, ms);
    }

    private JsonObject Message(JsonNode trajectory, long ms) => new()
    {
        ["Header"] = AmsTypes.Message, ["AMSMASMessageID"] = $"AMM{++count:D6}",
        ["AMSMASMessageTime"] = EssJson.SimpleTime($"AMM{count:D6}-T", ms),
        ["AMSMessage"] = new JsonObject { ["Trajectory"] = trajectory, ["Command"] = "Execute" }
    };

    public static double Speed(JsonNode point)
    {
        var v = EssJson.Vector(point["ExpectedSpaceTime"]?["SpatialAttitude1"]?["Position"]?["CartVelocity"]) ?? (0, 0, 0);
        return Math.Sqrt(v.X * v.X + v.Y * v.Y);
    }

    private static (double East, double North, double Heading) Position(JsonNode point)
    {
        var p = EssJson.Vector(point["ExpectedSpaceTime"]?["SpatialAttitude1"]?["Position"]?["CartPosition"]) ?? (0, 0, 0);
        return (p.X, p.Y, Heading(point, 0));
    }

    private static double Heading(JsonNode point, double fallback)
    {
        var v = EssJson.Vector(point["ExpectedSpaceTime"]?["SpatialAttitude1"]?["Position"]?["CartVelocity"]);
        return v is { } x && (Math.Abs(x.X) > 1e-6 || Math.Abs(x.Y) > 1e-6) ? Math.Atan2(x.Y, x.X) : fallback;
    }
}
