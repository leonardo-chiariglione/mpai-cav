using Mpai.Mmm.Client;

namespace Mpai.Aif.Tests;

// THE ROOM HAS THE SEATS THAT WERE PLANNED, no more, and they are given in the world's order: the Secretary (first to take a seat) at the side, the first two people across the table, so that they see each other.
// What is in front of a shared screen is drawn aside by the viewer, for whoever looks at it (MMM/Viewer/index.html, compactSeat).
public class MmmSeatsTests
{
    // The eight seats of MMM/Viewer/world/world.json: x east, y north, yaw (0 looks north).
    private static readonly (string ID, double X, double Y, double Yaw)[] Eight =
    [
        ("MeetingRoom-S1", 1.5, 0.0, 90), ("MeetingRoom-S2", 0.0, -1.5, 0), ("MeetingRoom-S3", 0.0, 1.5, 180), ("MeetingRoom-S4", -1.5, 0.0, 270),
        ("MeetingRoom-S5", 1.061, -1.061, 45), ("MeetingRoom-S6", -1.061, 1.061, 225), ("MeetingRoom-S7", -1.061, -1.061, 315), ("MeetingRoom-S8", 1.061, 1.061, 135)
    ];

    [Fact]
    public void TwoPeopleAndTheSecretaryHaveThreeSeats_TheFirstThreeOfTheWorld()
    {
        var seats = Meeting.Plan(Eight, 3);
        Assert.Equal(["MeetingRoom-S1", "MeetingRoom-S2", "MeetingRoom-S3"], seats.Select(s => s.ID));
    }

    [Fact]
    public void TheSeatsAreGivenInTheOrderOfTheWorld() =>
        Assert.Equal("MeetingRoom-S1", Meeting.InOrderFor("Leonardo", Eight).First().ID);

    [Fact]
    public void MoreSeatsPlannedThanTheRoomHasKeepsTheRoomsSeats() => Assert.Equal(8, Meeting.Plan(Eight, 12).Length);
}
