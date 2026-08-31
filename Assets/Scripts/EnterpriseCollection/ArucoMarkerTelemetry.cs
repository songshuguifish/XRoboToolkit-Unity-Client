using System;
using System.Collections.Generic;
using LitJson;
using Unity.XR.PICO.TOBSupport;
using UnityEngine;
using UnityEngine.XR;

namespace Robot
{
    /// <summary>
    /// Registers PICO Enterprise marker tracking once per service bind and exposes a
    /// bounded, sparse stream of ArUco callback events for the production TCP recorder.
    ///
    /// PICO's MarkerInfoCallback has already converted the vendor coordinates for Unity.
    /// The SDK does not document the source timestamp clock, position unit, or exact pose
    /// direction, so this class preserves those values without inventing semantics.
    /// </summary>
    public static class ArucoMarkerTelemetry
    {
        private const int StatusSchemaVersion = 1;
        private const int EventSchemaVersion = 1;
        // The tracking packet currently has a fixed 16,352-byte serialization budget.
        // Eight verbose marker samples leave head/controller telemetry enough headroom.
        private const int MaxMarkersPerCallback = 8;
        private const int MaxQueuedCallbacks = 64;
        private const float RegistrationRetryIntervalSeconds = 0.5f;
        private const int RegistrationNotAttempted = int.MinValue;
        private static readonly object Sync = new object();
        private static readonly List<CallbackEvent> CallbackQueue = new List<CallbackEvent>();

        private static StatusSnapshot s_status = StatusSnapshot.Empty;
        private static long s_bindGeneration;
        private static long s_nextCallbackSequence;
        // This is a bounded replay history for independent consumers (TCP and local camera
        // recording). Evicting an already-consumed entry is not a dropped callback, so keep
        // this diagnostic semantically distinct from the per-consumer sequence gap below.
        private static long s_callbackHistoryEvictionCount;
        private static long s_callbackHistoryClearCount;
        private static float s_nextRegistrationCheckTime;

        public sealed class ConsumerCursor
        {
            internal ConsumerCursor(string name)
            {
                Name = string.IsNullOrEmpty(name) ? "unknown" : name;
            }

            internal long LastEmittedCallbackSequence;
            internal long MissedCallbackCount;
            public string Name { get; private set; }
        }

        public static ConsumerCursor CreateConsumerCursor(string name)
        {
            return new ConsumerCursor(name);
        }

        public static void NotifyEnterpriseServiceBound(bool bound)
        {
            lock (Sync)
            {
                if (s_status.EnterpriseServiceBound == bound)
                {
                    return;
                }

                s_bindGeneration++;
                s_callbackHistoryClearCount += CallbackQueue.Count;
                CallbackQueue.Clear();
                s_status = StatusSnapshot.Empty;
                s_status.EnterpriseServiceBound = bound;
                s_status.BindGeneration = s_bindGeneration;
                s_status.RegistrationState = bound
                    ? "waiting_for_origin"
                    : "service_unbound";
                s_nextRegistrationCheckTime = 0.0f;
            }
        }

        /// <summary>
        /// Must be called on Unity's main thread. Registration is delayed until the PICO
        /// runtime and Unity XROrigin agree on Device/Floor so the SDK's Y adjustment is
        /// explicit and reproducible.
        /// </summary>
        public static void UpdateOnMainThread()
        {
            long generation;
            lock (Sync)
            {
                if (!s_status.EnterpriseServiceBound ||
                    s_status.RegistrationAttempted ||
                    Time.realtimeSinceStartup < s_nextRegistrationCheckTime)
                {
                    return;
                }
                s_nextRegistrationCheckTime =
                    Time.realtimeSinceStartup + RegistrationRetryIntervalSeconds;
                generation = s_bindGeneration;
            }

            if (!TrackingOriginTelemetry.TryGetMarkerRegistrationSettings(
                    out TrackingOriginModeFlags trackingMode,
                    out float cameraYOffsetMeters,
                    out string runtimeMode,
                    out string unityMode,
                    out string reason))
            {
                lock (Sync)
                {
                    if (generation != s_bindGeneration)
                    {
                        return;
                    }
                    s_status.RuntimeOriginMode = runtimeMode;
                    s_status.UnityOriginMode = unityMode;
                    s_status.RegistrationState = reason == "origin_mode_mismatch"
                        ? "blocked_origin_mismatch"
                        : "waiting_for_origin";
                    s_status.Error = reason;
                }
                return;
            }

            lock (Sync)
            {
                if (generation != s_bindGeneration || s_status.RegistrationAttempted)
                {
                    return;
                }
                s_status.RegistrationAttempted = true;
                s_status.RegistrationState = "registering";
                s_status.TrackingModeRequested = NormalizeTrackingMode(trackingMode);
                s_status.RuntimeOriginMode = runtimeMode;
                s_status.UnityOriginMode = unityMode;
                s_status.CameraYOffsetMeters = cameraYOffsetMeters;
                s_status.Error = "";
            }

            int result = RegistrationNotAttempted;
            string error = "";
            try
            {
                result = PXR_Enterprise.SetMarkerInfoCallback(
                    trackingMode,
                    cameraYOffsetMeters,
                    markerInfos => OnMarkerInfos(generation, markerInfos));
            }
            catch (Exception exception)
            {
                error = FormatException(exception);
            }

            lock (Sync)
            {
                if (generation != s_bindGeneration)
                {
                    return;
                }
                s_status.RegistrationResult = result;
                s_status.Registered = string.IsNullOrEmpty(error) && result == 0;
                s_status.RegistrationState = s_status.Registered
                    ? "registered"
                    : "registration_failed";
                s_status.Error = error;
            }

            if (result == 0 && string.IsNullOrEmpty(error))
            {
                Debug.Log(
                    "ArucoMarkerTelemetry registered: mode=" +
                    NormalizeTrackingMode(trackingMode) +
                    ", cameraYOffsetMeters=" + cameraYOffsetMeters);
            }
            else
            {
                Debug.LogWarning(
                    "ArucoMarkerTelemetry registration failed: result=" + result +
                    (string.IsNullOrEmpty(error) ? "" : ", error=" + error));
            }
        }

        public static void AppendStatusTo(JsonData root)
        {
            if (root == null)
            {
                return;
            }

            lock (Sync)
            {
                AppendStatusLocked(root);
            }
        }

        /// <summary>
        /// Atomically snapshots registration status and claims at most one callback for a
        /// named sink. Sharing one cursor between direct and legacy TCP prevents duplicates
        /// when the transport mode changes; local camera recording owns a separate cursor.
        /// </summary>
        public static bool AppendTo(JsonData root, ConsumerCursor cursor)
        {
            if (root == null || cursor == null)
            {
                return false;
            }

            lock (Sync)
            {
                AppendStatusLocked(root);
                return AppendNextCallbackLocked(root, cursor);
            }
        }

        private static void AppendStatusLocked(JsonData root)
        {
            StatusSnapshot status = s_status;

            JsonData value = new JsonData();
            value["schema_version"] = StatusSchemaVersion;
            value["enterprise_service_bound"] = status.EnterpriseServiceBound;
            value["bind_generation"] = status.BindGeneration;
            value["registration_state"] = status.RegistrationState ?? "unknown";
            value["registration_attempted"] = status.RegistrationAttempted;
            value["registered"] = status.Registered;
            value["registration_result"] = status.RegistrationResult;
            value["tracking_mode_requested"] =
                status.TrackingModeRequested ?? "unknown";
            value["runtime_origin_mode"] = status.RuntimeOriginMode ?? "unknown";
            value["unity_origin_mode"] = status.UnityOriginMode ?? "unknown";
            value["camera_y_offset_m"] = (double)status.CameraYOffsetMeters;
            value["latest_callback_sequence"] = status.LatestCallbackSequence;
            value["latest_callback_receive_timestamp_ns"] =
                status.LatestCallbackReceiveTimestampNs;
            value["latest_reported_marker_count"] = status.LatestReportedMarkerCount;
            value["latest_serialized_marker_count"] = status.LatestSerializedMarkerCount;
            value["latest_markers_truncated"] = status.LatestMarkersTruncated;
            value["callback_sequence_scope"] = "process_monotonic";
            value["callback_history_depth"] = CallbackQueue.Count;
            value["callback_history_capacity"] = MaxQueuedCallbacks;
            value["callback_history_oldest_sequence"] =
                CallbackQueue.Count > 0 ? CallbackQueue[0].Sequence : 0L;
            value["callback_history_latest_sequence"] =
                CallbackQueue.Count > 0
                    ? CallbackQueue[CallbackQueue.Count - 1].Sequence
                    : 0L;
            value["callback_history_eviction_count"] = s_callbackHistoryEvictionCount;
            value["callback_history_clear_count"] = s_callbackHistoryClearCount;
            value["max_markers_per_callback"] = MaxMarkersPerCallback;
            value["callback_unregister_supported"] = false;
            value["confidence_available"] = false;
            value["sdk_coordinate_conversion"] =
                "PICO.MarkerInfoCallback_Unity_conversion";
            value["pose_frame"] = "pico_marker_callback_tracking_origin_adjusted";
            value["position_unit"] = "sdk_undocumented";
            value["source_timestamp_unit"] = "sdk_undocumented";
            value["source_timestamp_clock_domain"] = "sdk_undocumented";
            value["pose_direction"] = "sdk_undocumented_unverified";
            if (!string.IsNullOrEmpty(status.Error))
            {
                value["error"] = status.Error;
            }
            root["aruco_marker_status_v1"] = value;
        }

        /// <summary>
        /// Appends at most one previously un-emitted callback for this named sink.
        /// </summary>
        public static bool AppendNextCallbackTo(JsonData root, ConsumerCursor cursor)
        {
            if (root == null || cursor == null)
            {
                return false;
            }

            lock (Sync)
            {
                return AppendNextCallbackLocked(root, cursor);
            }
        }

        private static bool AppendNextCallbackLocked(JsonData root, ConsumerCursor cursor)
        {
            // TrackingData reuses the same JsonData instance. Clear before lookup so an empty
            // queue or service rebind can never repeat a stale callback event.
            if (root.ContainsKey("aruco_markers_v1"))
            {
                root.Remove("aruco_markers_v1");
            }

            CallbackEvent callbackEvent = null;
            for (int i = 0; i < CallbackQueue.Count; i++)
            {
                if (CallbackQueue[i].Sequence > cursor.LastEmittedCallbackSequence &&
                    CallbackQueue[i].BindGeneration == s_bindGeneration &&
                    s_status.EnterpriseServiceBound)
                {
                    callbackEvent = CallbackQueue[i];
                    break;
                }
            }

            if (callbackEvent == null)
            {
                return false;
            }

            long missedCallbackCount = cursor.LastEmittedCallbackSequence > 0
                ? Math.Max(
                    0L,
                    callbackEvent.Sequence - cursor.LastEmittedCallbackSequence - 1L)
                : 0L;
            long consumerMissedCallbackCountTotal =
                cursor.MissedCallbackCount + missedCallbackCount;

            JsonData value = new JsonData();
            value["schema_version"] = EventSchemaVersion;
            value["callback_sequence"] = callbackEvent.Sequence;
            value["callback_receive_timestamp_ns"] = callbackEvent.ReceiveTimestampNs;
            value["bind_generation"] = callbackEvent.BindGeneration;
            value["reported_marker_count"] = callbackEvent.ReportedMarkerCount;
            value["serialized_marker_count"] = callbackEvent.Markers.Length;
            value["markers_truncated"] = callbackEvent.Truncated;
            value["delivery_consumer"] = cursor.Name;
            value["missed_callback_count_before_event"] = missedCallbackCount;
            value["consumer_missed_callback_count_total"] =
                consumerMissedCallbackCountTotal;

            JsonData markers = new JsonData();
            markers.SetJsonType(JsonType.Array);
            for (int i = 0; i < callbackEvent.Markers.Length; i++)
            {
                MarkerSample marker = callbackEvent.Markers[i];
                JsonData item = new JsonData();
                item["id"] = marker.Id;
                item["marker_type"] = marker.MarkerType;
                item["marker_type_name"] = MarkerTypeName(marker.MarkerType);
                item["sdk_valid_flag"] = marker.SdkValidFlag;
                item["numeric_valid"] = marker.NumericValid;
                item["source_timestamp_raw"] = marker.SourceTimestampRaw;
                item["source_timestamp_valid"] = marker.SourceTimestampValid;
                item["quaternion_norm"] = marker.QuaternionNorm;
                item["position"] = BuildArray(
                    marker.PositionX, marker.PositionY, marker.PositionZ);
                item["rotation_xyzw"] = BuildArray(
                    marker.RotationX,
                    marker.RotationY,
                    marker.RotationZ,
                    marker.RotationW);
                markers.Add(item);
            }
            value["markers"] = markers;
            root["aruco_markers_v1"] = value;
            // Claim only after the complete event is attached. This remains inside Sync, so
            // direct and legacy encoders cannot claim the same callback concurrently.
            cursor.MissedCallbackCount = consumerMissedCallbackCountTotal;
            cursor.LastEmittedCallbackSequence = callbackEvent.Sequence;
            return true;
        }

        private static void OnMarkerInfos(long generation, List<MarkerInfo> markerInfos)
        {
            int reportedCount = markerInfos == null ? 0 : markerInfos.Count;
            int serializedCount = Math.Min(reportedCount, MaxMarkersPerCallback);
            MarkerSample[] markers = new MarkerSample[serializedCount];
            for (int i = 0; i < serializedCount; i++)
            {
                markers[i] = CreateMarkerSample(markerInfos[i]);
            }

            long sequence;
            long receiveTimestampNs = Utils.GetCurrentTimestamp();
            lock (Sync)
            {
                if (generation != s_bindGeneration || !s_status.EnterpriseServiceBound)
                {
                    return;
                }

                sequence = ++s_nextCallbackSequence;
                CallbackQueue.Add(new CallbackEvent
                {
                    Sequence = sequence,
                    ReceiveTimestampNs = receiveTimestampNs,
                    BindGeneration = generation,
                    ReportedMarkerCount = reportedCount,
                    Truncated = reportedCount > serializedCount,
                    Markers = markers
                });
                while (CallbackQueue.Count > MaxQueuedCallbacks)
                {
                    CallbackQueue.RemoveAt(0);
                    s_callbackHistoryEvictionCount++;
                }

                s_status.LatestCallbackSequence = sequence;
                s_status.LatestCallbackReceiveTimestampNs = receiveTimestampNs;
                s_status.LatestReportedMarkerCount = reportedCount;
                s_status.LatestSerializedMarkerCount = serializedCount;
                s_status.LatestMarkersTruncated = reportedCount > serializedCount;
            }

            if (sequence == 1 || sequence % 300 == 0)
            {
                Debug.Log(
                    "ArucoMarkerTelemetry callback: sequence=" + sequence +
                    ", markers=" + reportedCount +
                    ", serialized=" + serializedCount);
            }
        }

        private static MarkerSample CreateMarkerSample(MarkerInfo marker)
        {
            if (marker == null)
            {
                return MarkerSample.Invalid;
            }

            bool numericValid =
                IsFinite(marker.posX) && IsFinite(marker.posY) && IsFinite(marker.posZ) &&
                IsFinite(marker.rotationX) && IsFinite(marker.rotationY) &&
                IsFinite(marker.rotationZ) && IsFinite(marker.rotationW);
            double quaternionNorm = numericValid
                ? Math.Sqrt(
                    marker.rotationX * marker.rotationX +
                    marker.rotationY * marker.rotationY +
                    marker.rotationZ * marker.rotationZ +
                    marker.rotationW * marker.rotationW)
                : 0.0;
            bool sourceTimestampValid = IsFinite(marker.dTimestamp);

            return new MarkerSample
            {
                Id = marker.iMarkerId,
                MarkerType = marker.markerType,
                SdkValidFlag = marker.validFlag,
                NumericValid = numericValid,
                SourceTimestampRaw = sourceTimestampValid ? marker.dTimestamp : 0.0,
                SourceTimestampValid = sourceTimestampValid,
                QuaternionNorm = quaternionNorm,
                PositionX = numericValid ? marker.posX : 0.0,
                PositionY = numericValid ? marker.posY : 0.0,
                PositionZ = numericValid ? marker.posZ : 0.0,
                RotationX = numericValid ? marker.rotationX : 0.0,
                RotationY = numericValid ? marker.rotationY : 0.0,
                RotationZ = numericValid ? marker.rotationZ : 0.0,
                RotationW = numericValid ? marker.rotationW : 1.0
            };
        }

        private static JsonData BuildArray(params double[] values)
        {
            JsonData array = new JsonData();
            array.SetJsonType(JsonType.Array);
            for (int i = 0; i < values.Length; i++)
            {
                array.Add(values[i]);
            }
            return array;
        }

        private static string NormalizeTrackingMode(TrackingOriginModeFlags mode)
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

        private static string MarkerTypeName(int markerType)
        {
            if (markerType == 1)
            {
                return "static";
            }
            if (markerType == 0)
            {
                return "dynamic";
            }
            return "unknown";
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static string FormatException(Exception exception)
        {
            return exception.GetType().Name + ": " + exception.Message;
        }

        private struct StatusSnapshot
        {
            public static StatusSnapshot Empty => new StatusSnapshot
            {
                RegistrationState = "service_unbound",
                RegistrationResult = RegistrationNotAttempted,
                TrackingModeRequested = "unknown",
                RuntimeOriginMode = "unknown",
                UnityOriginMode = "unknown"
            };

            public bool EnterpriseServiceBound;
            public long BindGeneration;
            public string RegistrationState;
            public bool RegistrationAttempted;
            public bool Registered;
            public int RegistrationResult;
            public string TrackingModeRequested;
            public string RuntimeOriginMode;
            public string UnityOriginMode;
            public float CameraYOffsetMeters;
            public long LatestCallbackSequence;
            public long LatestCallbackReceiveTimestampNs;
            public int LatestReportedMarkerCount;
            public int LatestSerializedMarkerCount;
            public bool LatestMarkersTruncated;
            public string Error;
        }

        private sealed class CallbackEvent
        {
            public long Sequence;
            public long ReceiveTimestampNs;
            public long BindGeneration;
            public int ReportedMarkerCount;
            public bool Truncated;
            public MarkerSample[] Markers;
        }

        private struct MarkerSample
        {
            public static MarkerSample Invalid => new MarkerSample
            {
                MarkerType = -1,
                NumericValid = false,
                RotationW = 1.0
            };

            public int Id;
            public int MarkerType;
            public int SdkValidFlag;
            public bool NumericValid;
            public double SourceTimestampRaw;
            public bool SourceTimestampValid;
            public double QuaternionNorm;
            public double PositionX;
            public double PositionY;
            public double PositionZ;
            public double RotationX;
            public double RotationY;
            public double RotationZ;
            public double RotationW;
        }
    }
}
