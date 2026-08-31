using System;
using System.Security.Cryptography;
using System.Text;
using LitJson;
using Unity.XR.PICO.TOBSupport;
using Unity.XR.PXR;
using UnityEngine;

namespace Robot
{
    /// <summary>
    /// Read-only snapshot of the Enterprise large-space state commonly used for LBE.
    /// Large-space, runtime tracking origin, and Unity XROrigin are intentionally kept
    /// separate because the SDK does not establish an automatic relationship between them.
    /// </summary>
    public static class LargeSpaceTelemetry
    {
        private const int SchemaVersion = 1;
        private const float SampleIntervalSeconds = 5.0f;
        private const int MaxMapInfoRawCharacters = 1024;
        private static readonly object LatestLock = new object();

        private static Snapshot s_latest = Snapshot.Empty;
        private static float s_nextSampleTime;
        private static long s_requestSequence;

        public static void SampleOnMainThread(bool enterpriseServiceBound, bool force = false)
        {
            float now = Time.realtimeSinceStartup;
            if (!force && now < s_nextSampleTime)
            {
                return;
            }

            s_nextSampleTime = now + SampleIntervalSeconds;
            Snapshot sample = Snapshot.Empty;
            Snapshot previous;
            lock (LatestLock)
            {
                previous = s_latest;
            }
            sample.SampleTimestampNs = Utils.GetCurrentTimestamp();
            sample.EnterpriseServiceBound = enterpriseServiceBound;
            sample.RequestSequence = ++s_requestSequence;
            sample.AppIdentifier = Application.identifier;
            sample.AppVersion = Application.version;
            sample.AppBuildGuid = Application.buildGUID;
            sample.UnityVersion = Application.unityVersion;
            sample.DeviceModel = SystemInfo.deviceModel;
            sample.OperatingSystem = SystemInfo.operatingSystem;

            // GetSwitchLargeSpaceStatus is asynchronous. Keep the last confirmed value
            // visible while a refresh is pending so the telemetry does not manufacture an
            // on -> unknown -> on transition every five seconds. The request/callback
            // timestamps still make the age and pending state explicit.
            if (enterpriseServiceBound && previous.EnterpriseServiceBound &&
                previous.SceneStatusValid)
            {
                sample.SceneStatusValid = true;
                sample.SceneStatus = previous.SceneStatus;
                sample.SceneStatusRaw = previous.SceneStatusRaw;
                sample.SceneStatusCallbackTimestampNs =
                    previous.SceneStatusCallbackTimestampNs;
            }

            if (!enterpriseServiceBound)
            {
                Publish(sample);
                return;
            }

            try
            {
                string mapInfo =
                    PXR_Enterprise.StateGetDeviceInfo(SystemInfoEnum.LARGESPACE_MAP_INFO);
                sample.MapInfoValid = !string.IsNullOrEmpty(mapInfo);
                sample.MapInfoRawLength = mapInfo == null ? 0 : mapInfo.Length;
                sample.MapInfoTruncated =
                    sample.MapInfoRawLength > MaxMapInfoRawCharacters;
                sample.MapInfoRaw = sample.MapInfoTruncated
                    ? mapInfo.Substring(0, MaxMapInfoRawCharacters)
                    : mapInfo;
                sample.MapInfoSha256 = Sha256Hex(mapInfo);
            }
            catch (Exception exception)
            {
                sample.MapInfoQueryError = FormatException(exception);
            }

            try
            {
                sample.MapScaleRaw =
                    PXR_Enterprise.StateGetDeviceInfo(SystemInfoEnum.LARGE_SPACE_MAP_SCALE);
                sample.MapScaleValid = !string.IsNullOrEmpty(sample.MapScaleRaw);
            }
            catch (Exception exception)
            {
                sample.MapScaleQueryError = FormatException(exception);
            }

            try
            {
                LargeSpaceQuickModeInfo quick = PXR_Enterprise.GetLargeSpaceQuickModeInfo();
                if (quick != null)
                {
                    // The SDK exposes no native result code here. query_ok only means that
                    // the wrapper returned and parsed a value; an all-zero response remains
                    // ambiguous and must not be treated as proof that quick mode is disabled.
                    sample.QuickModeQueryOk = true;
                    sample.QuickModeStatus = quick.status;
                    sample.QuickModeLength = quick.length;
                    sample.QuickModeWidth = quick.width;
                    sample.QuickModeOriginType = quick.originType;
                }
            }
            catch (Exception exception)
            {
                sample.QuickModeQueryError = FormatException(exception);
            }

            try
            {
                sample.BoundaryConfigured = PXR_Boundary.GetConfigured();
                sample.BoundaryEnabled = PXR_Boundary.GetEnabled();
                sample.BoundaryVisible = PXR_Boundary.GetVisible();
                sample.BoundaryQueryOk = true;
            }
            catch (Exception exception)
            {
                sample.BoundaryQueryError = FormatException(exception);
            }

            sample.SceneStatusQueryRequested = true;
            sample.SceneStatusQuerySentTimestampNs = Utils.GetCurrentTimestamp();
            long requestSequence = sample.RequestSequence;
            // Publish before issuing the asynchronous request so even a synchronous test
            // callback cannot be lost or overwritten by the pre-callback snapshot.
            Publish(sample);
            try
            {
                PXR_Enterprise.GetSwitchLargeSpaceStatus(
                    raw => UpdateSceneStatus(requestSequence, raw));
            }
            catch (Exception exception)
            {
                UpdateSceneQueryError(requestSequence, FormatException(exception));
            }
        }

        public static void Reset()
        {
            lock (LatestLock)
            {
                s_latest = Snapshot.Empty;
                s_requestSequence++;
            }
            s_nextSampleTime = 0.0f;
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

            JsonData value = new JsonData();
            value["schema_version"] = SchemaVersion;
            value["sample_timestamp_ns"] = sample.SampleTimestampNs;
            value["enterprise_service_bound"] = sample.EnterpriseServiceBound;
            value["app_identifier"] = sample.AppIdentifier ?? "";
            value["app_version"] = sample.AppVersion ?? "";
            value["app_build_guid"] = sample.AppBuildGuid ?? "";
            value["unity_version"] = sample.UnityVersion ?? "";
            value["device_model"] = sample.DeviceModel ?? "";
            value["operating_system"] = sample.OperatingSystem ?? "";
            value["large_space_scene_status_valid"] = sample.SceneStatusValid;
            value["large_space_scene_status"] = sample.SceneStatus ?? "unknown";
            value["large_space_scene_status_raw"] = sample.SceneStatusRaw ?? "";
            value["scene_status_query_requested"] = sample.SceneStatusQueryRequested;
            value["scene_status_callback_received"] = sample.SceneStatusCallbackReceived;
            value["scene_status_query_sent_timestamp_ns"] =
                sample.SceneStatusQuerySentTimestampNs;
            value["scene_status_callback_timestamp_ns"] =
                sample.SceneStatusCallbackTimestampNs;
            value["map_info_valid"] = sample.MapInfoValid;
            value["map_info_raw"] = sample.MapInfoRaw ?? "";
            value["map_info_raw_length"] = sample.MapInfoRawLength;
            value["map_info_truncated"] = sample.MapInfoTruncated;
            value["map_info_sha256"] = sample.MapInfoSha256 ?? "";
            value["map_scale_valid"] = sample.MapScaleValid;
            value["map_scale_raw"] = sample.MapScaleRaw ?? "";
            value["quick_mode_query_ok"] = sample.QuickModeQueryOk;
            value["quick_mode_status"] = sample.QuickModeStatus;
            value["quick_mode_length"] = sample.QuickModeLength;
            value["quick_mode_width"] = sample.QuickModeWidth;
            value["quick_mode_origin_type"] = sample.QuickModeOriginType;
            value["boundary_query_ok"] = sample.BoundaryQueryOk;
            value["boundary_configured"] = sample.BoundaryConfigured;
            value["boundary_enabled"] = sample.BoundaryEnabled;
            value["boundary_visible"] = sample.BoundaryVisible;
            AppendError(value, "scene_status_query_error", sample.SceneStatusQueryError);
            AppendError(value, "map_info_query_error", sample.MapInfoQueryError);
            AppendError(value, "map_scale_query_error", sample.MapScaleQueryError);
            AppendError(value, "quick_mode_query_error", sample.QuickModeQueryError);
            AppendError(value, "boundary_query_error", sample.BoundaryQueryError);
            root["large_space_v1"] = value;
        }

        private static void UpdateSceneStatus(long requestSequence, string raw)
        {
            lock (LatestLock)
            {
                if (s_latest.RequestSequence != requestSequence)
                {
                    return;
                }

                Snapshot updated = s_latest;
                updated.SceneStatusCallbackReceived = true;
                updated.SceneStatusCallbackTimestampNs = Utils.GetCurrentTimestamp();
                updated.SceneStatusRaw = raw ?? "";
                if (raw == "0")
                {
                    updated.SceneStatus = "off";
                    updated.SceneStatusValid = true;
                }
                else if (raw == "1")
                {
                    updated.SceneStatus = "on";
                    updated.SceneStatusValid = true;
                }
                else
                {
                    updated.SceneStatus = "unknown";
                    updated.SceneStatusValid = false;
                }
                s_latest = updated;
            }
        }

        private static void UpdateSceneQueryError(long requestSequence, string error)
        {
            lock (LatestLock)
            {
                if (s_latest.RequestSequence != requestSequence)
                {
                    return;
                }

                Snapshot updated = s_latest;
                updated.SceneStatusQueryError = error;
                s_latest = updated;
            }
        }

        private static void Publish(Snapshot sample)
        {
            lock (LatestLock)
            {
                s_latest = sample;
            }
        }

        private static string FormatException(Exception exception)
        {
            return exception.GetType().Name + ": " + exception.Message;
        }

        private static string Sha256Hex(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }

            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] digest = sha256.ComputeHash(Encoding.UTF8.GetBytes(value));
                StringBuilder result = new StringBuilder(digest.Length * 2);
                foreach (byte item in digest)
                {
                    result.Append(item.ToString("x2"));
                }
                return result.ToString();
            }
        }

        private static void AppendError(JsonData value, string key, string error)
        {
            if (!string.IsNullOrEmpty(error))
            {
                value[key] = error;
            }
        }

        private struct Snapshot
        {
            public static Snapshot Empty => new Snapshot
            {
                SceneStatus = "unknown",
                QuickModeOriginType = -1
            };

            public long RequestSequence;
            public long SampleTimestampNs;
            public bool EnterpriseServiceBound;
            public string AppIdentifier;
            public string AppVersion;
            public string AppBuildGuid;
            public string UnityVersion;
            public string DeviceModel;
            public string OperatingSystem;
            public bool SceneStatusValid;
            public string SceneStatus;
            public string SceneStatusRaw;
            public bool SceneStatusQueryRequested;
            public bool SceneStatusCallbackReceived;
            public long SceneStatusQuerySentTimestampNs;
            public long SceneStatusCallbackTimestampNs;
            public bool MapInfoValid;
            public string MapInfoRaw;
            public int MapInfoRawLength;
            public bool MapInfoTruncated;
            public string MapInfoSha256;
            public bool MapScaleValid;
            public string MapScaleRaw;
            public bool QuickModeQueryOk;
            public bool QuickModeStatus;
            public int QuickModeLength;
            public int QuickModeWidth;
            public int QuickModeOriginType;
            public bool BoundaryQueryOk;
            public bool BoundaryConfigured;
            public bool BoundaryEnabled;
            public bool BoundaryVisible;
            public string SceneStatusQueryError;
            public string MapInfoQueryError;
            public string MapScaleQueryError;
            public string QuickModeQueryError;
            public string BoundaryQueryError;
        }
    }
}
