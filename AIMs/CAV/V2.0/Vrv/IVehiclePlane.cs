using System.Text.Json.Nodes;

namespace Mpai.Cav.Vrv;

// A VEHICLE ON A PLANE: the CAV on a flat surface that has no end, no lane and nothing else on it, commanded through its three devices as it is on a road, and sensed through what a CAV
// senses of its own motion. The probe that makes the Vehicle Response Vocabulary (CAV-VRV) asks no more of it, so that the same measurements are made on a simulated vehicle or a real one.
public interface IVehiclePlane
{
    // A new run: the vehicle on a surface of that friction, heading east at that speed, nothing commanded.
    void Reset(double speed, double friction);

    // A command to a device, as the schema of its data type gives it: the Motor Command (CAV-MRC), the Brake Command (CAV-BRC), the Wheel Command (CAV-WHC).
    void Command(string dataType, JsonNode command);

    // The vehicle over that many seconds.
    void Step(double seconds);

    double Speed { get; }                    // m/s
    double Acceleration { get; }             // m/s2 along its heading, as the accelerometer reads it
    double Heading { get; }                  // radians, anticlockwise from east: its change is what the gyroscope reads
    bool Slipping { get; }                   // the tyres could not give what was commanded along the road: ABS modulating, a wheel locked or spinning
    bool Sliding { get; }                    // it turned less than its wheels asked: sideways
}
