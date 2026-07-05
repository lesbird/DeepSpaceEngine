using System;
using System.Collections.Generic;
using Silk.NET.Maths;

namespace Game.Universe;

/// <summary>
/// Analytic visibility of the system's star from the camera: how much of the sightline to the sun is
/// blocked by intervening planets and moons. Pure ray-vs-sphere geometry in double precision, so it is
/// deterministic and unit-testable — no depth buffer readback or GPU occlusion query. Drives the
/// lens-flare fade (and could gate any other "is the sun actually in view" effect): 1 = clear sky,
/// 0 = fully eclipsed, with a soft penumbra as the sun slips behind a limb so the effect ramps rather
/// than pops. Because it works on camera-relative metre vectors it stays correct at any distance from
/// the universe origin.
/// </summary>
public static class SunOcclusion
{
    /// <summary>
    /// Visibility in [0,1] of a star at <paramref name="camToSun"/> (metres, camera-relative) given a
    /// set of occluders, each as (centre camera-relative metres, radius metres). A body only occludes
    /// when it lies between the camera and the sun (0 &lt; along-ray distance &lt; sun distance); the
    /// perpendicular miss distance versus its radius gives the soft limb. Occluders compound
    /// multiplicatively, so two overlapping bodies can't over-darken past fully blocked.
    /// </summary>
    public static double Visibility(Vector3D<double> camToSun,
        IReadOnlyList<(Vector3D<double> center, double radius)> occluders)
    {
        double sunDist = camToSun.Length;
        if (sunDist <= 1e-6) return 1.0;
        Vector3D<double> dir = camToSun / sunDist;

        double vis = 1.0;
        for (int i = 0; i < occluders.Count; i++)
        {
            (Vector3D<double> c, double r) = occluders[i];
            if (r <= 0.0) continue;

            double t = Vector3D.Dot(c, dir);         // projection of the body centre onto the sightline
            if (t <= 0.0 || t >= sunDist) continue;  // behind the camera, or past the sun → can't block

            double miss = (c - dir * t).Length;      // perpendicular distance from the ray to the centre
            // Soft limb: fully blocked well inside the disc, clear just outside it, penumbra between.
            double occ = 1.0 - Smoothstep(r * 0.85, r * 1.20, miss);
            vis *= 1.0 - occ;
            if (vis <= 0.0) return 0.0;
        }
        return vis;
    }

    private static double Smoothstep(double edge0, double edge1, double x)
    {
        double t = Math.Clamp((x - edge0) / (edge1 - edge0), 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }
}
