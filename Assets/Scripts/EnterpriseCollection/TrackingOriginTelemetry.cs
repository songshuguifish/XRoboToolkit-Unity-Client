using System;
using LitJson;
using Unity.XR.CoreUtils;
using Unity.XR.PICO.TOBSupport;
using Unity.XR.PXR;
using UnityEngine;
using UnityEngine.XR;

namespace Robot
{
    /// <summary>
    /// Samples the coordinate-frame state that gives raw PICO poses their meaning.
    ///
    /// The PICO runtime tracking origin, Unity XROrigin camera offset, and the Enterprise
    /// configured floor height are independent settings. Keep all of them as metadata; do
    /// not apply floor_height_m as an implicit pose translation.
    /// </summary>
    public static class TrackingOriginTelemetry
    {
        private const int SchemaVersion = 1;
        private const float FloorSampleIntervalSeconds = 5.0f;
        private const float EnterpriseFailureSentinel = -1.0f;
        private static readonly object LatestLock = new object();

        private static Snapshot s_latest = Snapshot.Empty;
        private static float s_nextFloorSampleTime;
        private static XROrigin s_xrOrigin;

        public static void SampleOnMainThread(bool enterpriseServiceBound, bool force = false)
        {
            float now = Time.realtimeSinceStartup;
            Snapshot sample = Snapshot.Empty;
            sample.SampleTimestampNs = Utils.GetCurrentTimestamp();
            sample.PoseFrame = "pico_tracking_origin";

            bool queryFloor = enterpriseServiceBound &&
                (force || now >= s_nextFloorSampleTime);
            if (!queryFloor && enterpriseServiceBound)
            {
                lock (LatestLock)
                {
                    sample.FloorHeightValid = s_latest.FloorHeightValid;
                    sample.FloorHeightMeters = s_latest.FloorHeightMeters;
                    sample.FloorHeightSampleTimestampNs =
                        s_latest.FloorHeightSampleTimestampNs;
                    sample.FloorHeightQueryError = s_latest.FloorHeightQueryError;
                }
            }

            if (s_xrOrigin == null)
            {
                s_xrOrigin = UnityEngine.Object.FindObjectOfType<XROrigin>();
            }

            if (s_xrOrigin != null)
            {
                sample.UnityRequestedMode =
                    s_xrOrigin.RequestedTrackingOriginMode.ToString().ToLowerInvariant();
                sample.UnityCurrentMode = NormalizeUnityMode(s_xrOrigin.CurrentTrackingOriginMode);
                sample.CameraYOffsetMeters = s_xrOrigin.CameraYOffset;
                if (s_xrOrigin.Camera != null)
                {
                    sample.CameraInOriginHeightMeters = s_xrOrigin.CameraInOriginSpaceHeight;
                    sample.CameraInOriginHeightValid = true;
                }
                if (s_xrOrigin.CameraFloorOffsetObject != null)
                {
                    sample.CameraFloorOffsetLocalYMeters =
                        s_xrOrigin.CameraFloorOffsetObject.transform.localPosition.y;
                    sample.CameraFloorOffsetValid = true;
                }
                sample.UnityOriginValid = true;
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                PxrTrackingOrigin runtimeMode = PxrTrackingOrigin.Eye;
                // Call the outer native declaration so we retain the PICO return code.
                // PXR_Plugin.System.UPxr_GetTrackingOrigin is a void convenience wrapper.
                int result = PXR_Plugin.Pxr_GetTrackingOrigin(ref runtimeMode);
                sample.RuntimeQueryResult = result;
                sample.RuntimeModeRaw = (int)runtimeMode;
                sample.RuntimeMode = NormalizeRuntimeMode(runtimeMode);
                sample.RuntimeModeValid = result == 0 && sample.RuntimeMode != "unknown";
            }
            catch (Exception exception)
            {
                sample.RuntimeQueryError = exception.GetType().Name + ": " + exception.Message;
            }
#else
            // Editor and non-Android runs have no libpxr_api. The Unity subsystem value is
            // still useful for tests, but it is explicitly not marked as a PICO runtime query.
            if (s_xrOrigin != null)
            {
                sample.RuntimeMode = NormalizeUnityMode(s_xrOrigin.CurrentTrackingOriginMode);
                sample.RuntimeModeRaw = (int)s_xrOrigin.CurrentTrackingOriginMode;
            }
#endif

            if (queryFloor)
            {
                s_nextFloorSampleTime = now + FloorSampleIntervalSeconds;
                sample.FloorHeightSampleTimestampNs = Utils.GetCurrentTimestamp();
                try
                {
                    float floorHeight = PXR_Enterprise.GetFloorHeight();
                    sample.FloorHeightMeters = floorHeight;
                    sample.FloorHeightValid = IsFinite(floorHeight) &&
                        Math.Abs(floorHeight - EnterpriseFailureSentinel) > 0.000001f;
                }
                catch (Exception exception)
                {
                    sample.FloorHeightQueryError =
                        exception.GetType().Name + ": " + exception.Message;
                }
            }

            sample.PosesAreFloorRelative =
                sample.RuntimeModeValid && sample.RuntimeMode == "floor";
            lock (LatestLock)
            {
                s_latest = sample;
            }
        }

        public static void Reset()
        {
            lock (LatestLock)
            {
                s_latest = Snapshot.Empty;
            }
            s_nextFloorSampleTime = 0.0f;
            s_xrOrigin = null;
        }

        public static void AppendTo(JsonData root)
        {
            if (root == null)
            {
                return;
            }

            Snapshot sample;
            lock (LatestLock)
            {
                sample = s_latest;
            }

            JsonData origin = new JsonData();
            origin["schema_version"] = SchemaVersion;
            origin["sample_timestamp_ns"] = sample.SampleTimestampNs;
            origin["pose_frame"] = sample.PoseFrame ?? "pico_tracking_origin";
            origin["runtime_mode_valid"] = sample.RuntimeModeValid;
            origin["runtime_mode"] = sample.RuntimeMode ?? "unknown";
            origin["runtime_mode_raw"] = sample.RuntimeModeRaw;
            origin["runtime_mode_enum_domain"] = "PICO.PxrTrackingOrigin";
            origin["runtime_query_result"] = sample.RuntimeQueryResult;
            origin["poses_are_floor_relative"] = sample.PosesAreFloorRelative;
            origin["floor_height_valid"] = sample.FloorHeightValid;
            origin["floor_height_sample_timestamp_ns"] =
                sample.FloorHeightSampleTimestampNs;
            origin["floor_height_m"] = (double)sample.FloorHeightMeters;
            origin["floor_height_source"] = "PXR_Enterprise.GetFloorHeight";
            origin["unity_origin_valid"] = sample.UnityOriginValid;
            origin["unity_requested_mode"] = sample.UnityRequestedMode ?? "unknown";
            origin["unity_current_mode"] = sample.UnityCurrentMode ?? "unknown";
            origin["unity_mode_enum_domain"] = "Unity.XR.CoreUtils.XROrigin";
            origin["camera_y_offset_m"] = (double)sample.CameraYOffsetMeters;
            origin["camera_floor_offset_valid"] = sample.CameraFloorOffsetValid;
            origin["camera_floor_offset_local_y_m"] =
                (double)sample.CameraFloorOffsetLocalYMeters;
            origin["camera_in_origin_height_valid"] = sample.CameraInOriginHeightValid;
            origin["camera_in_origin_height_m"] =
                (double)sample.CameraInOriginHeightMeters;
            if (!string.IsNullOrEmpty(sample.RuntimeQueryError))
            {
                origin["runtime_query_error"] = sample.RuntimeQueryError;
            }
            if (!string.IsNullOrEmpty(sample.FloorHeightQueryError))
            {
                origin["floor_height_query_error"] = sample.FloorHeightQueryError;
            }
            root["tracking_origin_v1"] = origin;
        }

        public static bool TryGetMarkerRegistrationSettings(
            out TrackingOriginModeFlags trackingMode,
            out float cameraYOffsetMeters,
            out string runtimeMode,
            out string unityMode,
            out string reason)
        {
            Snapshot sample;
            lock (LatestLock)
            {
                sample = s_latest;
            }

            trackingMode = TrackingOriginModeFlags.Unknown;
            cameraYOffsetMeters = 0.0f;
            runtimeMode = sample.RuntimeMode ?? "unknown";
            unityMode = sample.UnityCurrentMode ?? "unknown";
            reason = "";

            if (!sample.RuntimeModeValid || !sample.UnityOriginValid ||
                runtimeMode == "unknown" || unityMode == "unknown")
            {
                reason = "origin_not_ready";
                return false;
            }
            if (runtimeMode != unityMode)
            {
                reason = "origin_mode_mismatch";
                return false;
            }
            if (runtimeMode == "floor")
            {
                trackingMode = TrackingOriginModeFlags.Floor;
                cameraYOffsetMeters = 0.0f;
                return true;
            }
            if (runtimeMode == "device" && IsFinite(sample.CameraYOffsetMeters))
            {
                trackingMode = TrackingOriginModeFlags.Device;
                cameraYOffsetMeters = sample.CameraYOffsetMeters;
                return true;
            }

            reason = "unsupported_origin_mode";
            return false;
        }

        private static string NormalizeRuntimeMode(PxrTrackingOrigin mode)
        {
            switch (mode)
            {
                case PxrTrackingOrigin.Eye:
                    return "device";
                case PxrTrackingOrigin.Floor:
                    return "floor";
                case PxrTrackingOrigin.Stage:
                    return "stage";
                default:
                    return "unknown";
            }
        }

        private static string NormalizeUnityMode(TrackingOriginModeFlags mode)
        {
            if ((mode & TrackingOriginModeFlags.Floor) != 0)
            {
                return "floor";
            }
            if ((mode & TrackingOriginModeFlags.Device) != 0)
            {
                return "device";
            }
            return "unknown";
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private struct Snapshot
        {
            public static Snapshot Empty => new Snapshot
            {
                RuntimeMode = "unknown",
                RuntimeModeRaw = -1,
                RuntimeQueryResult = int.MinValue,
                UnityRequestedMode = "unknown",
                UnityCurrentMode = "unknown",
                PoseFrame = "pico_tracking_origin"
            };

            public long SampleTimestampNs;
            public bool RuntimeModeValid;
            public string RuntimeMode;
            public int RuntimeModeRaw;
            public int RuntimeQueryResult;
            public bool PosesAreFloorRelative;
            public bool FloorHeightValid;
            public long FloorHeightSampleTimestampNs;
            public float FloorHeightMeters;
            public bool UnityOriginValid;
            public string UnityRequestedMode;
            public string UnityCurrentMode;
            public float CameraYOffsetMeters;
            public bool CameraFloorOffsetValid;
            public float CameraFloorOffsetLocalYMeters;
            public bool CameraInOriginHeightValid;
            public float CameraInOriginHeightMeters;
            public string PoseFrame;
            public string RuntimeQueryError;
            public string FloorHeightQueryError;
        }
    }
}
