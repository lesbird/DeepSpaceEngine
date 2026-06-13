using Game.Universe;
using Silk.NET.Maths;
using Xunit;

namespace Engine.Core.Tests;

/// <summary>
/// Exercises the pure <see cref="Rover"/> sim with injected height fields (no GL/input): gravity
/// settling, following the planet's curvature while driving, falling off a ledge, and determinism.
/// </summary>
public class RoverTests
{
    private const double R = 6.0e6; // planet radius, m
    private const double G = 9.0;   // surface gravity, m/s²

    private static Rover FlatRover() => new(R, G, _ => 0.0);

    [Fact]
    public void Settles_OnFlatGround_AndStaysGrounded()
    {
        var rover = FlatRover();
        var up = Vector3D.Normalize(new Vector3D<double>(0.2, 1.0, -0.3));
        rover.Seed(up, new Vector3D<double>(1, 0, 0));
        rover.LocalPos = up * (R + 50.0); // lift it 50 m and let gravity pull it down

        for (int i = 0; i < 600; i++) rover.Update(1.0 / 60.0, 0, 0, false);

        Assert.True(rover.Grounded);
        Assert.True(Math.Abs(rover.LocalPos.Length - (R + Rover.RideHeight)) < 1.0,
            $"settled radius {rover.LocalPos.Length}, expected {R + Rover.RideHeight}");
    }

    [Fact]
    public void DrivingForward_FollowsCurvature_StaysAtRideHeight()
    {
        var rover = FlatRover();
        var up = new Vector3D<double>(0, 1, 0);
        rover.Seed(up, new Vector3D<double>(0, 0, -1));
        var start = rover.LocalPos;

        for (int i = 0; i < 1800; i++) rover.Update(1.0 / 60.0, 1.0, 0, false); // 30 s at full throttle

        Assert.True(rover.Grounded);
        // Glued to the surface: radius never drifts off the ride height despite moving along tangents.
        Assert.InRange(rover.LocalPos.Length, R + Rover.RideHeight - 1.0, R + Rover.RideHeight + 2.0);
        // It actually travelled across the ground.
        Assert.True((rover.LocalPos - start).Length > 50.0, "rover did not move forward");
    }

    [Fact]
    public void DrivingOffLedge_FallsThenLandsLower()
    {
        // A 200 m step down across the Z = 0 plane.
        var rover = new Rover(R, G, d => d.Z < 0 ? -200.0 : 0.0);
        var up = new Vector3D<double>(0, 1, 0);     // sitting on the high side at the edge
        rover.Seed(up, new Vector3D<double>(0, 0, -1)); // heading toward the drop

        bool wentAirborne = false;
        for (int i = 0; i < 1200; i++)
        {
            rover.Update(1.0 / 60.0, 1.0, 0, false);
            if (!rover.Grounded) wentAirborne = true;
        }

        Assert.True(wentAirborne, "rover should have left the ground crossing the ledge");
        Assert.True(rover.Grounded, "rover should have landed on the lower surface");
        Assert.True(Vector3D.Normalize(rover.LocalPos).Z < 0, "rover should be on the low side");
        Assert.True(Math.Abs(rover.LocalPos.Length - (R - 200.0 + Rover.RideHeight)) < 2.0,
            $"landed radius {rover.LocalPos.Length}, expected {R - 200.0 + Rover.RideHeight}");
    }

    [Fact]
    public void Simulation_IsDeterministic()
    {
        var a = FlatRover();
        var b = FlatRover();
        var up = Vector3D.Normalize(new Vector3D<double>(0.3, 1.0, 0.2));
        a.Seed(up, new Vector3D<double>(1, 0, 0));
        b.Seed(up, new Vector3D<double>(1, 0, 0));

        for (int i = 0; i < 300; i++)
        {
            double throttle = Math.Sin(i * 0.1);
            double steer = Math.Cos(i * 0.07);
            a.Update(1.0 / 60.0, throttle, steer, false);
            b.Update(1.0 / 60.0, throttle, steer, false);
        }

        Assert.Equal(a.LocalPos.X, b.LocalPos.X);
        Assert.Equal(a.LocalPos.Y, b.LocalPos.Y);
        Assert.Equal(a.LocalPos.Z, b.LocalPos.Z);
        Assert.Equal(a.Velocity.X, b.Velocity.X);
        Assert.Equal(a.Velocity.Y, b.Velocity.Y);
        Assert.Equal(a.Velocity.Z, b.Velocity.Z);
    }
}
