using System;
using System.Collections.Generic;
using Unity.XR.PICO.TOBSupport;
using Unity.XR.PXR;
using UnityEngine;
using PicoPose = Unity.XR.PICO.TOBSupport.Pose;

namespace Robot
{
    /// <summary>
    /// Application-owned compatibility model for the legacy high-rate controller path.
    /// Keeping this outside the vendor SDK makes future PICO SDK upgrades replaceable.
    /// </summary>
    public sealed class PoseInfo
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
        public bool globalPoseValid;
        public long globalTimestamp;
        public double globalX;
        public double globalY;
        public double globalZ;
        public double globalRw;
        public double globalRx;
        public double globalRy;
        public double globalRz;
        public int globalConfidence;
        public bool nativeKinematicsValid;
        public Vector3 angularVelocity;
        public Vector3 linearVelocity;
        public Vector3 angularAcceleration;
        public Vector3 linearAcceleration;

        public List<long> reservedInt;
        public List<double> reservedDouble;
    }

    /// <summary>
    /// Exact scalar payload returned by TobService getControllerIMUData.
    /// Units remain vendor-native until a device-side calibration establishes scaling.
    /// </summary>
    public sealed class ControllerImuData
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
    }

    /// <summary>
    /// Single application boundary around PICO 3.4 telemetry APIs and the allocation-light
    /// bridge retained for the production controller stream.
    /// </summary>
    public static class PicoEnterpriseTelemetryProvider
    {
        // Pxr_GetControllerTrackingState reports controller linear derivatives in millimetre-
        // based units on the deployed runtime. PoseInfo is the established SI boundary.
        private const float RuntimeControllerLinearNativeToSi = 0.001f;
        private const int PackedControllerFields = 14;
        private const int PackedHeadPoseFields = 12;
        private const int PackedHeadImuFields = 14;
        private const int ControllerCount = 2;
        private const string BridgeClassName =
            "com.xrobotoolkit.enterprise.EnterprisePoseBridge";

        public static int GetLegacyHeadState(
            double predictTimeMs,
            ref PxrSensorState2 sensorState,
            ref int sensorFrameIndex)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return PXR_Plugin.Pxr_GetPredictedMainSensorState2(
                predictTimeMs, ref sensorState, ref sensorFrameIndex);
#else
            return 0;
#endif
        }

        public static PoseInfo[] GetLegacyControllerPose(double predictTimeMs)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            PoseInfo[] poses = new PoseInfo[ControllerCount];
            bool anyValid = false;
            for (uint deviceId = 0; deviceId < ControllerCount; deviceId++)
            {
                PxrControllerTracking tracking = new PxrControllerTracking();
                int result = PXR_Plugin.Controller.UPxr_GetControllerTrackingState(
                    deviceId, predictTimeMs, ref tracking);
                if (result != 0)
                {
                    continue;
                }

                PxrSensorState sensorState = tracking.localControllerPose;
                PxrSensorState globalSensorState = tracking.globalControllerPose;
                poses[deviceId] = new PoseInfo
                {
                    timestamp = unchecked((long)sensorState.poseTimeStampNs),
                    x = sensorState.pose.position.x,
                    y = sensorState.pose.position.y,
                    z = sensorState.pose.position.z,
                    rw = sensorState.pose.orientation.w,
                    rx = sensorState.pose.orientation.x,
                    ry = sensorState.pose.orientation.y,
                    rz = sensorState.pose.orientation.z,
                    type = unchecked((int)deviceId),
                    confidence = sensorState.status,
                    poseError = result,
                    globalPoseValid = IsUsablePose(globalSensorState),
                    globalTimestamp = unchecked((long)globalSensorState.poseTimeStampNs),
                    globalX = globalSensorState.pose.position.x,
                    globalY = globalSensorState.pose.position.y,
                    globalZ = globalSensorState.pose.position.z,
                    globalRw = globalSensorState.pose.orientation.w,
                    globalRx = globalSensorState.pose.orientation.x,
                    globalRy = globalSensorState.pose.orientation.y,
                    globalRz = globalSensorState.pose.orientation.z,
                    globalConfidence = globalSensorState.status,
                    nativeKinematicsValid = true,
                    angularVelocity = ToVector3(sensorState.angularVelocity),
                    linearVelocity = ToVector3(sensorState.linearVelocity) *
                                     RuntimeControllerLinearNativeToSi,
                    angularAcceleration = ToVector3(sensorState.angularAcceleration),
                    linearAcceleration = ToVector3(sensorState.linearAcceleration) *
                                         RuntimeControllerLinearNativeToSi
                };
                anyValid = true;
            }

            if (anyValid)
            {
                return poses;
            }

            // Preserve the previous customized SDK's failure path. The native runtime call
            // is allocation-free and remains primary; the official ToB JSON API is only used
            // when neither controller can be read from the runtime.
            try
            {
                List<PicoPose> fallback = PXR_Enterprise.GetControllerPose(0L);
                if (fallback == null || fallback.Count == 0)
                {
                    return null;
                }

                for (int i = 0; i < fallback.Count; i++)
                {
                    PicoPose pose = fallback[i];
                    if (pose == null)
                    {
                        continue;
                    }

                    int index = pose.type >= 0 && pose.type < ControllerCount
                        ? pose.type
                        : i;
                    if (index < 0 || index >= ControllerCount)
                    {
                        continue;
                    }

                    poses[index] = new PoseInfo
                    {
                        timestamp = pose.timestamp,
                        x = pose.x,
                        y = pose.y,
                        z = pose.z,
                        rw = pose.rw,
                        rx = pose.rx,
                        ry = pose.ry,
                        rz = pose.rz,
                        type = index,
                        confidence = pose.confidence,
                        poseError = pose.poseError,
                        nativeKinematicsValid = false
                    };
                    anyValid = true;
                }

                return anyValid ? poses : null;
            }
            catch (Exception)
            {
                ClearPendingJavaException();
                return null;
            }
#else
            return null;
#endif
        }

        public static ControllerImuData[] GetControllerImuData(long predictTimeNs)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            bool pushedLocalFrame = AndroidJNI.PushLocalFrame(32) == 0;
            try
            {
                using (AndroidJavaClass bridgeClass = new AndroidJavaClass(BridgeClassName))
                {
                    double[] packed = bridgeClass.CallStatic<double[]>(
                        "getControllerImuPacked", predictTimeNs);
                    return ConvertPackedControllerImu(packed);
                }
            }
            catch (Exception exception)
            {
                ClearPendingJavaException();
                Debug.LogWarning($"Controller IMU bridge failed: {exception.Message}");
                return null;
            }
            finally
            {
                if (pushedLocalFrame)
                {
                    AndroidJNI.PopLocalFrame(IntPtr.Zero);
                }
            }
#else
            return null;
#endif
        }

        public static PicoPose GetOfficialHeadPose(long predictTimeNs)
        {
            return PXR_Enterprise.GetHeadPose(predictTimeNs);
        }

        public static IMUData GetOfficialHeadImuData(long predictTimeNs)
        {
            return PXR_Enterprise.GetHeadIMUData(predictTimeNs);
        }

        public static void GetOfficialHeadTelemetry(
            long predictTimeNs,
            out PicoPose pose,
            out IMUData imu,
            out string transport)
        {
            pose = null;
            imu = null;
            transport = "sdk_proxy_packed";
#if UNITY_ANDROID && !UNITY_EDITOR
            bool pushedLocalFrame = AndroidJNI.PushLocalFrame(32) == 0;
            try
            {
                using (AndroidJavaClass bridgeClass = new AndroidJavaClass(BridgeClassName))
                {
                    double[] packed = bridgeClass.CallStatic<double[]>(
                        "getHeadTelemetryPacked", predictTimeNs);
                    ConvertPackedHeadTelemetry(packed, out pose, out imu);
                }
            }
            catch (Exception exception)
            {
                ClearPendingJavaException();
                Debug.LogWarning($"Head telemetry packed bridge failed: {exception.Message}");
            }
            finally
            {
                if (pushedLocalFrame)
                {
                    AndroidJNI.PopLocalFrame(IntPtr.Zero);
                }
            }
#endif
            if (pose != null || imu != null)
            {
                return;
            }

            // Compatibility fallback for runtimes where the SDK proxy surface is absent.
            // This is intentionally cold because the SDK 3.4 wrapper serializes through JSON.
            transport = "sdk_json_wrapper";
            pose = GetOfficialHeadPose(predictTimeNs);
            imu = GetOfficialHeadImuData(predictTimeNs);
        }

        private static ControllerImuData[] ConvertPackedControllerImu(double[] packed)
        {
            if (packed == null || packed.Length < 1)
            {
                return null;
            }

            int count = Math.Min(ControllerCount, Math.Max(0, (int)packed[0]));
            if (count == 0 || packed.Length < 1 + count * PackedControllerFields)
            {
                return null;
            }

            ControllerImuData[] result = new ControllerImuData[count];
            bool hasSample = false;
            for (int i = 0; i < count; i++)
            {
                int offset = 1 + i * PackedControllerFields;
                if (packed[offset] < 0.5)
                {
                    continue;
                }

                result[i] = new ControllerImuData
                {
                    timestamp = checked((long)packed[offset + 1]),
                    vx = packed[offset + 2],
                    vy = packed[offset + 3],
                    vz = packed[offset + 4],
                    ax = packed[offset + 5],
                    ay = packed[offset + 6],
                    az = packed[offset + 7],
                    wx = packed[offset + 8],
                    wy = packed[offset + 9],
                    wz = packed[offset + 10],
                    w_ax = packed[offset + 11],
                    w_ay = packed[offset + 12],
                    w_az = packed[offset + 13]
                };
                hasSample = true;
            }

            return hasSample ? result : null;
        }

        private static void ConvertPackedHeadTelemetry(
            double[] packed,
            out PicoPose pose,
            out IMUData imu)
        {
            pose = null;
            imu = null;
            int expectedLength = PackedHeadPoseFields + PackedHeadImuFields;
            if (packed == null || packed.Length < expectedLength)
            {
                return;
            }

            if (packed[0] >= 0.5)
            {
                pose = new PicoPose
                {
                    timestamp = checked((long)packed[1]),
                    x = packed[2],
                    y = packed[3],
                    z = packed[4],
                    rw = packed[5],
                    rx = packed[6],
                    ry = packed[7],
                    rz = packed[8],
                    type = checked((int)packed[9]),
                    confidence = checked((int)packed[10]),
                    poseError = checked((int)packed[11])
                };
            }

            int offset = PackedHeadPoseFields;
            if (packed[offset] >= 0.5)
            {
                imu = new IMUData
                {
                    timestamp = checked((long)packed[offset + 1]),
                    vx = packed[offset + 2],
                    vy = packed[offset + 3],
                    vz = packed[offset + 4],
                    ax = packed[offset + 5],
                    ay = packed[offset + 6],
                    az = packed[offset + 7],
                    wx = packed[offset + 8],
                    wy = packed[offset + 9],
                    wz = packed[offset + 10],
                    w_ax = packed[offset + 11],
                    w_ay = packed[offset + 12],
                    w_az = packed[offset + 13]
                };
            }
        }

        private static Vector3 ToVector3(PxrVector3f value)
        {
            return new Vector3(value.x, value.y, value.z);
        }

        private static bool IsFinitePose(PxrPosef pose)
        {
            return IsFinite(pose.position.x) &&
                   IsFinite(pose.position.y) &&
                   IsFinite(pose.position.z) &&
                   IsFinite(pose.orientation.x) &&
                   IsFinite(pose.orientation.y) &&
                   IsFinite(pose.orientation.z) &&
                   IsFinite(pose.orientation.w);
        }

        private static bool IsUsablePose(PxrSensorState sensorState)
        {
            if (sensorState.poseTimeStampNs == 0 || !IsFinitePose(sensorState.pose))
            {
                return false;
            }

            PxrVector4f orientation = sensorState.pose.orientation;
            double normSquared =
                orientation.x * orientation.x + orientation.y * orientation.y +
                orientation.z * orientation.z + orientation.w * orientation.w;
            // An unavailable global pose is commonly returned as an all-zero struct.
            // Allow normal floating-point drift around unit quaternions, but reject that
            // sentinel so LBE consumers do not treat world origin as a measured pose.
            return normSquared > 0.25 && normSquared < 2.25;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static void ClearPendingJavaException()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            IntPtr exception = AndroidJNI.ExceptionOccurred();
            if (exception == IntPtr.Zero)
            {
                return;
            }

            AndroidJNI.ExceptionClear();
            AndroidJNI.DeleteLocalRef(exception);
#endif
        }
    }
}
