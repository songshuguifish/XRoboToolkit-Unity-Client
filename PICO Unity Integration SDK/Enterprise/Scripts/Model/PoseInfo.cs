using System.Collections.Generic;
using UnityEngine;

namespace Unity.XR.PICO.TOBSupport
{
    public class PoseInfo
    {
        public long timestamp;
        public double x;
        public double y;
        public double z;
        public double rw;
        public double rx;
        public double ry;
        public double rz;
        public int type;
        public int confidence;
        public int poseError;
        public bool nativeKinematicsValid;
        public Vector3 angularVelocity;
        public Vector3 linearVelocity;
        public Vector3 angularAcceleration;
        public Vector3 linearAcceleration;

        public List<long> reservedInt;
        public List<double> reservedDouble;

        public override string ToString()
        {
            return
                $"PoseInfo position=({x:F6}, {y:F6}, {z:F6}) rotation=({rx:F6}, {ry:F6}, {rz:F6}, {rw:F6})" +
                $" | timestamp={timestamp} | type={type} | confidence={confidence} | poseError={poseError}";
        }
    }
}
