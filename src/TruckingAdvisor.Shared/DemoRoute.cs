namespace TruckingAdvisor.Shared;

/// <summary>The demo truck's made-up route (demo mode only): north-east with gentle curves. World +X east, +Z south.</summary>
public static class DemoRoute
{
    public static double X(double t) => t * 29.0;
    public static double Z(double t) => -t * 9.0 + 350 * Math.Sin(t / 30.0);

    /// <summary>Compass heading along the route (0 = north, 90 = east).</summary>
    public static float Heading(double t)
    {
        double dx = 29.0, dz = -9.0 + 350 / 30.0 * Math.Cos(t / 30.0);
        return (float)((Math.Atan2(dx, -dz) * 180 / Math.PI + 360) % 360);
    }
}
