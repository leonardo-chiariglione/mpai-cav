using System.Text.Json.Nodes;

namespace Mpai.Cav.Recordings;

// THE CAV'S MECHANICAL SUBSYSTEMS AND THE VEHICLE THEY MOVE (M3237 3.1, decided): a
// motor, brakes and steered wheels, each executing the latest command it was given
// with the lag and the limits of a real one, and a mid-size car that moves as they
// act - longitudinally by its mass, drag and rolling resistance, laterally as a
// bicycle model - the force its tyres transmit bounded by the friction of the road
// (a friction circle). Each device answers each command with its Response; the
// vehicle's sensors of motion give Spatial Data. The ground truth is the state.
//
// The devices take the commands of their schemas: the Motor Command in torque,
// acceleration or velocity mode; the Brake Command as a deceleration, a force or a
// pressure, an Emergency Brake flag, ABS allowed or not; the Wheel Command as the
// angle of the road wheels, in degrees, at a rate limit.
public sealed class Vehicle
{
    public const double Mass = 1500, Wheelbase = 2.8, WheelRadius = 0.33, GearRatio = 9, G = 9.81;
    public const double MaxMotorTorque = 350, MaxPower = 150_000, MaxBrakeDeceleration = 9, BrakeForcePerBar = 150;
    public const double MotorLag = 0.15, BrakeLag = 0.1, MaxSteer = 35 * Math.PI / 180, DefaultSteerRate = 30 * Math.PI / 180;
    public const double DragArea = 0.7, AirDensity = 1.2, RollingResistance = 0.012, AbsGrip = 0.95, LockedGrip = 0.8, AbsSlip = 0.12;
    public const double OdometerScale = 1.015, SpeedometerScale = 1.01;
    // The gyroscope: a yaw rate with noise and a small constant bias, as an automotive
    // inertial unit gives it (degrees/second).
    public const double GyroNoise = 0.05, GyroBias = 0.02;
    private const int SubSteps = 10;

    // THE STATE: where the vehicle is, its heading (radians, anticlockwise from
    // east), speed, acceleration, the angle of its road wheels, the forces the
    // motor and the brakes give, the distance it has travelled.
    public double East { get; private set; }
    public double North { get; private set; }
    public double Heading { get; private set; }
    public double Speed { get; private set; }
    public double Acceleration { get; private set; }
    public double Steer { get; private set; }
    public double SteerRate { get; private set; }
    public double DriveForce { get; private set; }
    public double BrakeForce { get; private set; }
    public double Distance { get; private set; }

    // What the tyres did in the last step: ABS modulating, a wheel locked or
    // spinning, the vehicle sliding sideways, the slip of the braked wheels.
    public bool AbsActive { get; private set; }
    public bool WheelLocked { get; private set; }
    public bool LossOfTraction { get; private set; }
    public bool Skidding { get; private set; }
    public double SlipRatio { get; private set; }

    // The friction under the wheels, which the simulation sets from the road.
    public double Friction { get; set; } = 0.9;

    // What the devices were last told.
    private string motorMode = "torque";
    private double motorTarget, brakeTarget, steerTarget, brakeReleaseSpeed, steerRateLimit = DefaultSteerRate;
    private bool emergency, absAllowed = true;
    private readonly List<(string Device, string Id)> commandedThisStep = [];

    private readonly Random noise;
    private (double Heading, long Ms)? lastYaw;               // the heading at the last Spatial Data
    private long responses;

    public Vehicle(double east, double north, double heading, double speed, int seed)
    {
        (East, North, Heading, Speed) = (east, north, heading, speed);
        noise = new Random(seed);
    }

    // ---- commands ---------------------------------------------------------------

    // A COMMAND, as its schema gives it. What it does not say, the device keeps
    // from its last command.
    public void Command(string dataType, JsonNode command)
    {
        if (dataType.StartsWith("CAV-MRC"))
        {
            var c = command["MotorCommand"]!;
            motorMode = (string?)c["ControlMode"] ?? "torque";
            motorTarget = motorMode switch
            {
                "torque" => (double?)c["TargetTorque"] ?? 0,
                "acceleration" => (double?)c["TargetAcceleration"] ?? 0,
                "velocity" => (double?)c["TargetVelocity"] ?? 0,
                _ => 0
            };
            commandedThisStep.Add(("Motor", (string?)command["MotorID"] ?? "M1"));
        }
        else if (dataType.StartsWith("CAV-BRC"))
        {
            // The Brake Command is a list of set points; the latest one holds.
            var list = command["BrakeCommand"]!.AsArray();
            var c = list[^1]!;
            emergency = (bool?)c["EmergencyBrakeFlag"] ?? false;
            absAllowed = (bool?)c["ABSAllow"] ?? true;
            brakeReleaseSpeed = (double?)c["TargetVelocity"] ?? 0;
            brakeTarget = emergency ? Mass * MaxBrakeDeceleration
                        : c["DecelerationTarget"] is { } d ? Mass * (double)d
                        : c["BrakeForceTarget"] is { } f ? (double)f
                        : c["BrakePressureTarget"] is { } p ? (double)p * BrakeForcePerBar
                        : 0;
            brakeTarget = Math.Clamp(brakeTarget, 0, Mass * MaxBrakeDeceleration);
            commandedThisStep.Add(("Brake", (string?)command["BrakeID"] ?? "B1"));
        }
        else if (dataType.StartsWith("CAV-WHC"))
        {
            var c = command["WheelCommand"]!;
            steerTarget = Math.Clamp((double)c["Angle"]! * Math.PI / 180, -MaxSteer, MaxSteer);
            steerRateLimit = c["SteeringRateLimit"] is { } r ? (double)r * Math.PI / 180 : DefaultSteerRate;
            commandedThisStep.Add(("Wheel", (string?)command["WheelID"] ?? "W1"));
        }
        else throw new ArgumentException($"{dataType} is not a command of the mechanical subsystems.");
    }

    // ---- moving -----------------------------------------------------------------

    // THE VEHICLE OVER A STEP of dt seconds, in sub-steps.
    public void Step(double dt)
    {
        AbsActive = WheelLocked = LossOfTraction = Skidding = false;
        SlipRatio = 0;
        var h = dt / SubSteps;
        double lastAcceleration = 0;
        for (var i = 0; i < SubSteps; i++)
        {
            var grip = Friction * Mass * G;
            var resistance = Speed > 0 ? 0.5 * AirDensity * DragArea * Speed * Speed + RollingResistance * Mass * G : 0;

            // The motor: a force towards its target, with a lag, within its torque and
            // its power. In acceleration mode it gives what that acceleration needs
            // against the resistance; in velocity mode, a proportional correction.
            var driveTarget = motorMode switch
            {
                "torque" => motorTarget * GearRatio / WheelRadius,
                "acceleration" => Mass * motorTarget + resistance,
                "velocity" => Mass * (motorTarget - Speed) + resistance,
                _ => 0
            };
            var maxDrive = Math.Min(MaxMotorTorque * GearRatio / WheelRadius, MaxPower / Math.Max(Speed, 1));
            driveTarget = Math.Clamp(driveTarget, 0, maxDrive);
            DriveForce += (driveTarget - DriveForce) * h / MotorLag;

            // The brakes: a force towards their target, with a lag; released once the
            // vehicle is at the speed the command asked for.
            var brakeGoal = Speed <= brakeReleaseSpeed + 1e-6 && !emergency && brakeReleaseSpeed > 0 ? 0 : brakeTarget;
            BrakeForce += (brakeGoal - BrakeForce) * h / BrakeLag;

            // What the tyres transmit longitudinally: the drive bounded by the grip
            // (the wheels spin beyond it), the braking bounded by the grip - ABS keeps
            // the wheels near the peak, a locked wheel slides with less.
            var drive = DriveForce;
            if (drive > grip) { drive = grip * LockedGrip; LossOfTraction = true; }
            var braking = Speed > 0 || BrakeForce > 0 ? BrakeForce : 0;
            if (braking > AbsGrip * grip)
            {
                if (absAllowed) { braking = AbsGrip * grip; AbsActive = true; SlipRatio = AbsSlip; }
                else { braking = LockedGrip * grip; WheelLocked = true; SlipRatio = 1; }
            }
            else if (braking > 0) SlipRatio = Math.Max(SlipRatio, AbsSlip * braking / (AbsGrip * grip));

            // At a standstill the brakes and the rolling resistance hold the vehicle
            // until the drive exceeds them.
            var a = Speed > 0
                ? (drive - braking - resistance) / Mass
                : Math.Max(0, drive - braking - RollingResistance * Mass * G) / Mass;
            var speed = Math.Max(0, Speed + a * h);
            if (Speed > 0 && speed == 0) a = -Speed / h;

            // The steering: towards its target at a limited rate, within its stops.
            var step = Math.Clamp(steerTarget - Steer, -steerRateLimit * h, steerRateLimit * h);
            Steer = Math.Clamp(Steer + step, -MaxSteer, MaxSteer);
            SteerRate = step / h;

            // Laterally, a bicycle model; what the turn asks of the tyres bounded by the
            // grip the longitudinal force leaves (a friction circle): beyond it the
            // vehicle turns less than its wheels (it slides).
            var v = (Speed + speed) / 2;
            var turn = Math.Tan(Steer);
            var lateral = v * v * Math.Abs(turn) / Wheelbase;
            var available = Math.Sqrt(Math.Max(0, Math.Pow(Friction * G, 2) - a * a));
            if (lateral > available && v > 0.5)
            {
                turn = Math.Sign(turn) * available * Wheelbase / (v * v);
                Skidding = true;
            }
            Heading += v * turn / Wheelbase * h;
            East += v * Math.Cos(Heading) * h;
            North += v * Math.Sin(Heading) * h;
            Distance += v * h;
            Speed = speed;
            lastAcceleration = a;
        }
        Acceleration = lastAcceleration;
    }

    // ---- what the devices answer, and what the sensors read -----------------------

    // A RESPONSE FROM EACH DEVICE commanded since the last responses, in the form of
    // its schema; ms the time of the step.
    public IReadOnlyList<(string DataType, string Json)> Responses(long ms)
    {
        var list = new List<(string, string)>();
        foreach (var (device, id) in commandedThisStep.Distinct())
        {
            var rid = $"{device[0]}R{++responses:D6}";
            list.Add(device switch
            {
                "Motor" => ("CAV-MRP-V2.0", new JsonObject
                {
                    ["Header"] = "CAV-MRP-V2.0", ["MotorResponseID"] = rid, ["MotorID"] = id,
                    ["MotorResponseTime"] = SimpleTime(rid + "-T", ms),
                    ["MotorState"] = LossOfTraction ? "LossOfTraction" : DriveForce > 1 ? "Active" : "Idle",
                    ["AchievedVelocity"] = Math.Round(Speed, 3),
                    ["AchievedTorque"] = Math.Round(DriveForce * WheelRadius / GearRatio, 2),
                    ["AchievedAcceleration"] = Math.Round(Acceleration, 3),
                    ["AnomalyFlags"] = new JsonObject { ["SensorFault"] = false, ["Overload"] = false, ["ThermalWarning"] = false, ["UnexpectedBehavior"] = LossOfTraction }
                }.ToJsonString()),
                "Brake" => ("CAV-BRR-V2.0", new JsonObject
                {
                    ["Header"] = "CAV-BRR-V2.0", ["BrakeResponseID"] = rid, ["BrakeID"] = id,
                    ["BrakeResponseTime"] = SimpleTime(rid + "-T", ms),
                    ["BrakeState"] = Speed <= 0 && BrakeForce > 1 ? "WheelStopped"
                                   : AbsActive ? "ABSActive"
                                   : emergency ? "EmergencyBraking"
                                   : BrakeForce > 1 ? "Braking" : "Released",
                    ["VelocityReached"] = Math.Round(Speed, 3),
                    ["BrakeForceApplied"] = Math.Round(BrakeForce, 1),
                    ["ABSActivation"] = AbsActive,
                    ["WheelSlipRatio"] = Math.Round(SlipRatio, 3)
                }.ToJsonString()),
                _ => ("CAV-WHR-V2.0", new JsonObject
                {
                    ["Header"] = "CAV-WHR-V2.0", ["WheelResponseID"] = rid, ["WheelID"] = id,
                    ["WheelResponseTime"] = SimpleTime(rid + "-T", ms),
                    ["WheelResponse"] = new JsonObject
                    {
                        ["WheelState"] = Math.Abs(SteerRate) > 1e-3 ? "ActiveSteering" : "Normal",
                        ["WheelAngle"] = Math.Round(Steer * 180 / Math.PI, 3),
                        ["SteeringRate"] = Math.Round(SteerRate * 180 / Math.PI, 3)
                    }
                }.ToJsonString())
            });
        }
        commandedThisStep.Clear();
        return list;
    }

    // THE SENSORS OF MOTION: the odometer reads long, the speedometer a little high,
    // the accelerometer and the inclinometers with noise, the gyroscope with noise
    // and a bias; the road is level.
    public string SpatialData(string id, long ms) => new JsonObject
    {
        ["Header"] = "CAV-SPD-V2.0", ["SpatialDataID"] = id, ["SpaceTime"] = SimpleTime(id + "-T", ms),
        ["SpatialData"] = new JsonObject
        {
            ["OdometerData"] = Math.Round(Distance * OdometerScale, 3),
            ["SpeedometerData"] = Math.Round(Math.Max(0, Speed * SpeedometerScale + Gaussian(0.05)), 3),
            ["AccelerometerData"] = Math.Round(Acceleration + Gaussian(0.05), 3),
            ["InclinometerData"] = new JsonObject
            {
                ["LongitudinalInclination"] = Math.Round(Gaussian(0.1), 3),
                ["LateralInclination"] = Math.Round(Gaussian(0.1), 3)
            },
            ["GyroscopeData"] = new JsonObject { ["YawRate"] = Math.Round(YawRate(ms) + GyroBias + Gaussian(GyroNoise), 4) }
        }
    }.ToJsonString();

    // How fast the vehicle turned since the last Spatial Data (degrees/second).
    private double YawRate(long ms)
    {
        var rate = lastYaw is { } l && ms > l.Ms ? Math.IEEERemainder(Heading - l.Heading, 2 * Math.PI) * 180 / Math.PI / ((ms - l.Ms) / 1000.0) : 0;
        lastYaw = (Heading, ms);
        return rate;
    }

    private double Gaussian(double sigma)
    {
        var u1 = 1.0 - noise.NextDouble();
        var u2 = noise.NextDouble();
        return sigma * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    private static JsonObject SimpleTime(string id, long ms) => new()
    {
        ["Header"] = "OSD-STM-V1.5", ["SimpleTimeID"] = id,
        ["SimpleTimeData"] = new JsonArray(new JsonObject { ["FlagsByte"] = 3, ["StartTime"] = ms, ["EndTime"] = ms, ["AccuracyMode"] = "single", ["AccuracyPlusMinus"] = 1 })
    };
}
