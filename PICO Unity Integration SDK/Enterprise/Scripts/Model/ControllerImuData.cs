using System.Collections.Generic;

namespace Unity.XR.PICO.TOBSupport
{
    /// <summary>
    /// Exact scalar payload returned by TobService getControllerIMUData.
    /// Units are intentionally not inferred here; the recorder preserves the vendor values.
    /// </summary>
    public class ControllerImuData
    {
        public long timestamp;
        public double vx;
        public double vy;
        public double vz;
        public double ax;
        public double ay;
        public double az;
        public double wx;
        public double wy;
        public double wz;
        public double w_ax;
        public double w_ay;
        public double w_az;

        public List<int> reservedInt;
        public List<double> reservedDouble;
    }
}
