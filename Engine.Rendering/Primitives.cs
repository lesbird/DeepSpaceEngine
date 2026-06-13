using System.Collections.Generic;
using Silk.NET.OpenGL;

namespace Engine.Rendering;

/// <summary>Factory helpers for simple debug meshes used in early milestones.</summary>
public static class Primitives
{
    /// <summary>A line grid on the XZ plane, centered at the local origin.</summary>
    public static Mesh BuildGrid(GL gl, float halfExtent, float step, (float r, float g, float b) color)
    {
        var v = new List<float>();
        void Line(float x0, float z0, float x1, float z1)
        {
            v.AddRange(new[] { x0, 0f, z0, color.r, color.g, color.b });
            v.AddRange(new[] { x1, 0f, z1, color.r, color.g, color.b });
        }

        for (float c = -halfExtent; c <= halfExtent + 0.001f; c += step)
        {
            Line(c, -halfExtent, c, halfExtent);   // lines parallel to Z
            Line(-halfExtent, c, halfExtent, c);   // lines parallel to X
        }

        return new Mesh(gl, v.ToArray(), PrimitiveType.Lines);
    }

    /// <summary>RGB world axes (X=red, Y=green, Z=blue) of the given length.</summary>
    public static Mesh BuildAxes(GL gl, float length)
    {
        float[] v =
        {
            0,0,0, 1,0,0,  length,0,0, 1,0,0,   // X red
            0,0,0, 0,1,0,  0,length,0, 0,1,0,   // Y green
            0,0,0, 0,0,1,  0,0,length, 0,0,1,   // Z blue
        };
        return new Mesh(gl, v, PrimitiveType.Lines);
    }

    /// <summary>A solid colored cube centered at the local origin.</summary>
    public static Mesh BuildCube(GL gl, float size, (float r, float g, float b) color)
    {
        float h = size * 0.5f;
        (float r, float g, float b) c = color;

        // 36 vertices (12 triangles). Slight per-face shading so faces are distinguishable.
        var v = new List<float>();
        void Quad((float x, float y, float z) a, (float x, float y, float z) b2,
                  (float x, float y, float z) d, (float x, float y, float z) e, float shade)
        {
            (float, float, float) col = (c.r * shade, c.g * shade, c.b * shade);
            void P((float x, float y, float z) p)
            {
                var (cr, cg, cb) = col;
                v.AddRange(new[] { p.x, p.y, p.z, cr, cg, cb });
            }
            P(a); P(b2); P(d);
            P(a); P(d); P(e);
        }

        // +X, -X, +Y, -Y, +Z, -Z
        Quad(( h,-h,-h), ( h, h,-h), ( h, h, h), ( h,-h, h), 0.9f);
        Quad((-h,-h, h), (-h, h, h), (-h, h,-h), (-h,-h,-h), 0.6f);
        Quad((-h, h,-h), (-h, h, h), ( h, h, h), ( h, h,-h), 1.0f);
        Quad((-h,-h, h), (-h,-h,-h), ( h,-h,-h), ( h,-h, h), 0.5f);
        Quad((-h,-h, h), ( h,-h, h), ( h, h, h), (-h, h, h), 0.8f);
        Quad(( h,-h,-h), (-h,-h,-h), (-h, h,-h), ( h, h,-h), 0.7f);

        return new Mesh(gl, v.ToArray(), PrimitiveType.Triangles);
    }

    /// <summary>A unit-radius UV sphere (position + outward normal per vertex), triangles.</summary>
    public static Mesh BuildSphere(GL gl, int stacks, int slices)
    {
        var v = new List<float>();
        (float x, float y, float z) P(int i, int j)
        {
            float phi = MathF.PI * (i / (float)stacks) - MathF.PI / 2f;
            float theta = 2f * MathF.PI * (j / (float)slices);
            float cp = MathF.Cos(phi);
            return (cp * MathF.Cos(theta), MathF.Sin(phi), cp * MathF.Sin(theta));
        }
        void Vert((float x, float y, float z) p)
        {
            // position + normal (identical for a unit sphere)
            v.AddRange(new[] { p.x, p.y, p.z, p.x, p.y, p.z });
        }

        for (int i = 0; i < stacks; i++)
        for (int j = 0; j < slices; j++)
        {
            var a = P(i, j); var b = P(i + 1, j); var c = P(i + 1, j + 1); var d = P(i, j + 1);
            Vert(a); Vert(b); Vert(c);
            Vert(a); Vert(c); Vert(d);
        }
        return new Mesh(gl, v.ToArray(), PrimitiveType.Triangles);
    }

    /// <summary>A unit-radius circle (line loop) in the XZ plane — used for orbit rings.</summary>
    public static Mesh BuildCircleLine(GL gl, int segments)
    {
        var v = new List<float>();
        for (int i = 0; i < segments; i++)
        {
            float t = 2f * MathF.PI * (i / (float)segments);
            v.AddRange(new[] { MathF.Cos(t), 0f, MathF.Sin(t), 0f, 0f, 0f });
        }
        return new Mesh(gl, v.ToArray(), PrimitiveType.LineLoop);
    }
}
