using System;
using LitJson;
using Unity.XR.PXR;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Robot
{
    /// <summary>Additive imu_v1 serializer for XRoboToolkit TrackingData.</summary>
    public static class PicoImuV1
    {
        public const int SchemaVersion = 1;
        private const float TargetSamplingFrequencyHz = 120.0f;

        public static void AppendHead(JsonData headJson, PxrSensorState2 sensor)
        {
            JsonData imu = new JsonData();
            imu["pico_pose_timestamp_ns"] = (long)sensor.poseTimeStampNs;
            imu["linear_velocity_mps"] = Vector(sensor.linearVelocity);
            imu["angular_velocity_rad_s"] = Vector(sensor.angularVelocity);
            imu["linear_acceleration_mps2"] = Vector(sensor.linearAcceleration);
            imu["angular_acceleration_rad_s2"] = Vector(sensor.angularAcceleration);
            AppendAndroidSensors(imu);
            headJson["imu_v1"] = imu;
        }

        public static void AppendController(
            JsonData controllerJson,
            PXR_Input.Controller controller,
            double predictTimeUs)
        {
            PxrControllerTracking tracking = new PxrControllerTracking();
            float[] headSensorData = new float[7] { 0, 0, 0, 0, 0, 0, 0 };
            // TrackingData converts PICO's millisecond display timestamp to
            // microseconds for its legacy wire field. The controller tracking
            // API expects milliseconds, so convert it back for this query.
            double predictTimeMs = predictTimeUs / 1000.0;
            int result = PXR_Plugin.Controller.UPxr_GetControllerTrackingState(
                (uint)controller,
                predictTimeMs,
                headSensorData,
                ref tracking);
            if (result != 0)
            {
                if (controllerJson.ContainsKey("imu_v1"))
                    controllerJson.Remove("imu_v1");
                return;
            }

            PxrSensorState sensor = tracking.localControllerPose;
            JsonData imu = new JsonData();
            imu["pico_pose_timestamp_ns"] = (long)sensor.poseTimeStampNs;
            imu["linear_velocity_mps"] = Vector(sensor.linearVelocity);
            imu["angular_velocity_rad_s"] = Vector(sensor.angularVelocity);
            imu["linear_acceleration_mps2"] = Vector(sensor.linearAcceleration);
            imu["angular_acceleration_rad_s2"] = Vector(sensor.angularAcceleration);
            controllerJson["imu_v1"] = imu;
        }

        private static void AppendAndroidSensors(JsonData imu)
        {
            GravitySensor gravity = GravitySensor.current;
            Accelerometer accelerometer = Accelerometer.current;
            LinearAccelerationSensor linearAcceleration = LinearAccelerationSensor.current;
            UnityEngine.InputSystem.Gyroscope gyroscope =
                UnityEngine.InputSystem.Gyroscope.current;

            bool gravityAvailable = Enable(gravity);
            bool accelerometerAvailable = Enable(accelerometer);
            bool linearAccelerationAvailable = Enable(linearAcceleration);
            bool gyroscopeAvailable = Enable(gyroscope);

            imu["requested_sampling_frequency_hz"] = (double)TargetSamplingFrequencyHz;
            JsonData availability = new JsonData();
            availability["gravity"] = gravityAvailable;
            availability["accelerometer"] = accelerometerAvailable;
            availability["linear_acceleration"] = linearAccelerationAvailable;
            availability["gyroscope"] = gyroscopeAvailable;
            imu["availability"] = availability;

            double latestUpdate = 0.0;
            if (gravityAvailable)
            {
                imu["gravity_g"] = Vector(gravity.gravity.ReadValue());
                latestUpdate = Math.Max(latestUpdate, gravity.lastUpdateTime);
            }
            if (accelerometerAvailable)
            {
                imu["accelerometer_g"] = Vector(accelerometer.acceleration.ReadValue());
                latestUpdate = Math.Max(latestUpdate, accelerometer.lastUpdateTime);
            }
            if (linearAccelerationAvailable)
            {
                imu["linear_acceleration_g"] = Vector(linearAcceleration.acceleration.ReadValue());
                latestUpdate = Math.Max(latestUpdate, linearAcceleration.lastUpdateTime);
            }
            if (gyroscopeAvailable)
            {
                imu["gyroscope_rad_s"] = Vector(gyroscope.angularVelocity.ReadValue());
                latestUpdate = Math.Max(latestUpdate, gyroscope.lastUpdateTime);
            }
            imu["android_sample_time_s"] = latestUpdate;
        }

        private static bool Enable(Sensor sensor)
        {
            if (sensor == null)
                return false;
            try
            {
                if (!sensor.enabled)
                    InputSystem.EnableDevice(sensor);
                if (Math.Abs(sensor.samplingFrequency - TargetSamplingFrequencyHz) > 0.01f)
                    sensor.samplingFrequency = TargetSamplingFrequencyHz;
                return sensor.enabled;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static JsonData Vector(PxrVector3f value)
        {
            JsonData array = new JsonData();
            array.SetJsonType(JsonType.Array);
            array.Add((double)value.x);
            array.Add((double)value.y);
            array.Add((double)value.z);
            return array;
        }

        private static JsonData Vector(Vector3 value)
        {
            JsonData array = new JsonData();
            array.SetJsonType(JsonType.Array);
            array.Add((double)value.x);
            array.Add((double)value.y);
            array.Add((double)value.z);
            return array;
        }
    }
}
