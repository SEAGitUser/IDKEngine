﻿using System;
using OpenTK.Mathematics;

namespace IDKEngine.Shapes
{
    public record struct Triangle
    {
        public readonly Vector3 Normal
        {
            get
            {
                Vector3 p0p1 = Position1 - Position0;
                Vector3 p0p2 = Position2 - Position0;
                Vector3 triNormal = Vector3.Normalize(Vector3.Cross(p0p1, p0p2));

                return triNormal;
            }
        }

        public readonly Vector3 Centroid => (Position0 + Position1 + Position2) * (1.0f / 3.0f);

        public Vector3 Position0;
        public Vector3 Position1;
        public Vector3 Position2;

        public Triangle(Vector3 p0, Vector3 p1, Vector3 p2)
        {
            Position0 = p0;
            Position1 = p1;
            Position2 = p2;
        }

        public void Transform(in Matrix4 model)
        {
            Position0 = (new Vector4(Position0, 1.0f) * model).Xyz;
            Position1 = (new Vector4(Position1, 1.0f) * model).Xyz;
            Position2 = (new Vector4(Position2, 1.0f) * model).Xyz;
        }

        public readonly ValueTuple<Box, Box> Split(int axis, float position)
        {
            // Source: https://github.com/madmann91/bvh/blob/2fd0db62022993963a7343669275647cb073e19a/include/bvh/triangle.hpp#L64

            Box lBox = Box.Empty();
            Box rBox = Box.Empty();

            Vector3 p0 = Position0;
            Vector3 p1 = Position1;
            Vector3 p2 = Position2;

            bool q0 = p0[axis] <= position;
            bool q1 = p1[axis] <= position;
            bool q2 = p2[axis] <= position;

            if (q0) lBox.GrowToFit(p0);
            else    rBox.GrowToFit(p0);
            if (q1) lBox.GrowToFit(p1);
            else    rBox.GrowToFit(p1);
            if (q2) lBox.GrowToFit(p2);
            else    rBox.GrowToFit(p2);

            if (q0 ^ q1)
            {
                Vector3 m = SplitEdge(p0, p1);
                lBox.GrowToFit(m);
                rBox.GrowToFit(m);
            }
            if (q1 ^ q2)
            {
                Vector3 m = SplitEdge(p1, p2);
                lBox.GrowToFit(m);
                rBox.GrowToFit(m);
            }
            if (q2 ^ q0)
            {
                Vector3 m = SplitEdge(p2, p0);
                lBox.GrowToFit(m);
                rBox.GrowToFit(m);
            }

            Vector3 SplitEdge(Vector3 a, Vector3 b)
            {
                float t = (position - a[axis]) / (b[axis] - a[axis]);

                return a + t * (b - a);
            }

            return (lBox, rBox);
        }

        public static Triangle Transformed(Triangle triangle, in Matrix4 model)
        {
            triangle.Transform(model);
            return triangle;
        }
    }
}
