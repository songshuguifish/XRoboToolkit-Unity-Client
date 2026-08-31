using System;
using System.Diagnostics;
using System.Threading;
using LitJson;
using Unity.XR.PICO.TOBSupport;
using Unity.XR.PXR;
using UnityEngine;
using UnityEngine.XR;
using CommonUsages = UnityEngine.XR.CommonUsages;
using Debug = UnityEngine.Debug;
using InputDevice = UnityEngine.XR.InputDevice;

namespace Robot
{
    public class EnterpriseCollectionRecorder : MonoBehaviour
    {
        private const string Tag = "EnterpriseCollectionRecorder";
        private const string EnterpriseHeadFile = "enterprise_head.jsonl";
        private const string EnterpriseControllerFile = "enterprise_controller_pose.jsonl";
        private const string UnityHeadFile = "unity_head.jsonl";
        private const string UnityControllerFile = "unity_controller_pose.jsonl";
        private const double MaxValidEnterpriseControllerPositionMeters = 10.0;
        private const int ProductionEnterpriseSampleHz = 800;
        // This Binder API returns a latest/predicted kinematics snapshot rather than a raw
        // sensor callback. Poll above the controller tracking rate without starving the
        // production 800 Hz raw pose path; source timestamp deduplication preserves truth.
        private const int TobControllerImuSampleHz = 250;
        // PICO SDK 3.4 Enterprise pose/IMU calls allocate JSON-backed managed objects. Poll
        // them near the physical tracking rate instead of the 800 Hz legacy transport rate.
        private const int OfficialHeadTelemetrySampleHz = 120;
        private const long OfficialHeadTelemetryPredictTimeNs = 0L;
        // v2 guarantees that every serialized derivative uses the SI unit named by its field.
        private const int NativeKinematicsSchemaVersion = 2;
        // Shadow local/global poses expose the two coordinate spaces returned by the same
        // native PICO query without changing the established primary pose.
        private const int NativePosePairSchemaVersion = 1;
        // TobService does not document units for IMUData scalar fields. Schema v1 therefore
        // preserves the exact vendor values under explicit *_native field names.
        private const int TobControllerImuSchemaVersion = 1;
        // v2 records the configured prediction horizon explicitly; production uses 0 ns.
        private const int OfficialHeadPoseSchemaVersion = 2;
        private const int OfficialHeadImuSchemaVersion = 1;
        public const string InvalidControllerPose = "0,0,0,0,0,0,1";
        private static bool s_enterpriseServiceBound;
        private static readonly object s_latestEnterpriseHeadLock = new object();
        private static bool s_hasLatestEnterpriseHead;
        private static EnterpriseHeadTcpPose s_latestEnterpriseHead;
        private static long s_latestEnterpriseHeadSampleSeq;
        private static readonly object s_latestEnterpriseControllerLock = new object();
        private static bool s_hasLatestEnterpriseController;
        private static EnterpriseControllerTcpPose s_latestEnterpriseLeftController;
        private static EnterpriseControllerTcpPose s_latestEnterpriseRightController;
        private static long s_latestEnterpriseControllerSampleSeq;
        private static readonly object s_latestTobControllerImuLock = new object();
        private static ControllerImuData[] s_latestTobControllerImu;
        private static readonly object s_latestOfficialHeadTelemetryLock = new object();
        private static OfficialHeadTelemetry s_latestOfficialHeadTelemetry;

        [SerializeField] private bool autoStart = true;
        // MCAP over TCP is the production recording path. The local JSONL stream is only a
        // diagnostic option; serializing two extra documents at 800 Hz makes IL2CPP's native
        // managed heap grow aggressively even after the writer has reached its time limit.
        [SerializeField] private bool enableFileWrite = false;
        [SerializeField] private bool collectHeadPose = true;
        [SerializeField] private bool collectControllerPose = true;
        [SerializeField] private bool collectOfficialHeadTelemetry = true;
        // Shadow mode is the rollout default: publish the new SDK pose/IMU alongside the
        // legacy pose without changing the pose used by teleoperation.
        [SerializeField] private bool preferOfficialHeadPose = false;
        // Applied again during Awake so a stale serialized or manually adjusted value cannot
        // carry into the next production app launch.
        [SerializeField] private int enterpriseSampleHz = ProductionEnterpriseSampleHz;
        [SerializeField] private int unitySampleHz = 90;
        [SerializeField] private int maxRecordSeconds = 100;
        [SerializeField] private bool useDynamicPredictedDisplayTimeForEnterpriseHead = false;
        [SerializeField] private bool outputSampleRateToLogWindow = false;
        [SerializeField] private float sampleRateLogIntervalSeconds = 1f;

        private readonly Stopwatch _stopwatch = new Stopwatch();
        private readonly ControllerLogState _unityLeftControllerLog = new ControllerLogState("unity", "left");
        private readonly ControllerLogState _unityRightControllerLog = new ControllerLogState("unity", "right");
        private readonly ControllerLogState _enterpriseLeftControllerLog = new ControllerLogState("enterprise", "left");
        private readonly ControllerLogState _enterpriseRightControllerLog = new ControllerLogState("enterprise", "right");

        private Thread _enterpriseThread;
        private Thread _controllerImuThread;
        private Thread _officialHeadTelemetryThread;
        private EnterpriseCollectionFileWriter _fileWriter;
        private volatile bool _recording;
        private long _enterpriseHeadSeq;
        private long _enterpriseControllerSeq;
        private long _unityHeadSeq;
        private long _unityControllerSeq;
        private double _nextUnitySampleTimeSeconds;
        private long _enterpriseHeadSamplesInWindow;
        private long _enterpriseControllerSamplesInWindow;
        private long _lastEnterpriseRateLogTicks;
        private bool _recordTimeLimitReached;

        public static void EnsureCreated()
        {
            if (FindObjectOfType<EnterpriseCollectionRecorder>() != null)
            {
                return;
            }

            GameObject recorder = new GameObject(nameof(EnterpriseCollectionRecorder));
            DontDestroyOnLoad(recorder);
            recorder.AddComponent<EnterpriseCollectionRecorder>();
        }

        private void Awake()
        {
            Volatile.Write(ref enterpriseSampleHz, ProductionEnterpriseSampleHz);
            Debug.Log($"{Tag} production enterprise sample hz initialized to {ProductionEnterpriseSampleHz}");
        }

        private void Start()
        {
            if (autoStart)
            {
                TryAutoStart();
            }
        }

        private void OnDestroy()
        {
            StopRecording();
        }

        private void OnApplicationQuit()
        {
            StopRecording();
        }

        public static void NotifyEnterpriseServiceBound(bool bind)
        {
            s_enterpriseServiceBound = bind;
            ArucoMarkerTelemetry.NotifyEnterpriseServiceBound(bind);
            if (!bind)
            {
                ClearLatestEnterpriseHead();
                ClearLatestEnterpriseController();
                ClearLatestTobControllerImu();
                ClearLatestOfficialHeadTelemetry();
                TrackingOriginTelemetry.Reset();
                LargeSpaceTelemetry.Reset();
            }

            EnterpriseCollectionRecorder recorder = FindObjectOfType<EnterpriseCollectionRecorder>();
            if (recorder == null)
            {
                return;
            }

            if (bind)
            {
                recorder.TryAutoStart();
            }
        }

        private void Update()
        {
            TrackingOriginTelemetry.SampleOnMainThread(s_enterpriseServiceBound);
            ArucoMarkerTelemetry.UpdateOnMainThread();
            if (!_recording)
            {
                return;
            }

            LargeSpaceTelemetry.SampleOnMainThread(s_enterpriseServiceBound);

            if (!_recordTimeLimitReached && maxRecordSeconds > 0 && _stopwatch.Elapsed.TotalSeconds >= maxRecordSeconds)
            {
                _recordTimeLimitReached = true;
                StopFileWriter("record time limit reached");
            }

            double nowSeconds = Time.realtimeSinceStartup;
            if (nowSeconds < _nextUnitySampleTimeSeconds)
            {
                return;
            }

            double intervalSeconds = GetIntervalSeconds(unitySampleHz);
            _nextUnitySampleTimeSeconds = nowSeconds + intervalSeconds;

            if (collectHeadPose)
            {
                if (IsFileWriterAccepting)
                {
                    Enqueue(UnityHeadFile, BuildUnityHeadLine());
                }
            }
        }

        public void StartRecording()
        {
            if (_recording)
            {
                return;
            }

            if (!CanStartRecording())
            {
                Debug.Log($"{Tag} waiting for enterprise service bind before start recording.");
                return;
            }

            _fileWriter = new EnterpriseCollectionFileWriter(enableFileWrite, Application.persistentDataPath);
            _fileWriter.Start(new EnterpriseCollectionFileWriter.Meta
            {
                EnableFileWrite = enableFileWrite,
                CollectHeadPose = collectHeadPose,
                CollectControllerPose = collectControllerPose,
                EnterpriseSampleHz = enterpriseSampleHz,
                UnitySampleHz = unitySampleHz,
                MaxRecordSeconds = maxRecordSeconds,
                UseDynamicPredictedDisplayTimeForEnterpriseHead = useDynamicPredictedDisplayTimeForEnterpriseHead,
                EnterpriseHeadPredictTimeMode = useDynamicPredictedDisplayTimeForEnterpriseHead
                    ? "dynamic"
                    : "latest",
                EnterpriseControllerPredictTimeMode = "latest"
            });

            _recording = true;
            _stopwatch.Restart();
            _nextUnitySampleTimeSeconds = 0;
            _enterpriseHeadSamplesInWindow = 0;
            _enterpriseControllerSamplesInWindow = 0;
            _lastEnterpriseRateLogTicks = Stopwatch.GetTimestamp();
            _recordTimeLimitReached = false;
            ClearLatestTobControllerImu();
            ClearLatestOfficialHeadTelemetry();
            TrackingOriginTelemetry.SampleOnMainThread(s_enterpriseServiceBound, true);
            ArucoMarkerTelemetry.UpdateOnMainThread();
            LargeSpaceTelemetry.SampleOnMainThread(s_enterpriseServiceBound, true);

            _enterpriseThread = new Thread(EnterpriseLoop)
            {
                IsBackground = true,
                Name = "EnterpriseCollection"
            };
            _enterpriseThread.Start();

            _controllerImuThread = new Thread(ControllerImuLoop)
            {
                IsBackground = true,
                Name = "TobControllerImu"
            };
            _controllerImuThread.Start();

            _officialHeadTelemetryThread = new Thread(OfficialHeadTelemetryLoop)
            {
                IsBackground = true,
                Name = "PicoSdk34HeadTelemetry"
            };
            _officialHeadTelemetryThread.Start();

            Debug.Log($"{Tag} started: {_fileWriter.RecordDir}, enableFileWrite={enableFileWrite}, " +
                $"collectHeadPose={collectHeadPose}, collectControllerPose={collectControllerPose}");
        }

        private void TryAutoStart()
        {
            if (!autoStart || _recording)
            {
                return;
            }

            StartRecording();
        }

        private static bool CanStartRecording()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return s_enterpriseServiceBound;
#else
            return true;
#endif
        }

        public void StopRecording()
        {
            if (!_recording)
            {
                return;
            }

            Stopwatch stopProfile = Stopwatch.StartNew();
            int pendingBeforeStop = _fileWriter != null ? _fileWriter.PendingCount : 0;
            _recording = false;

            Stopwatch stageProfile = Stopwatch.StartNew();
            _enterpriseThread?.Join(1000);
            long enterpriseJoinMs = stageProfile.ElapsedMilliseconds;
            stageProfile.Restart();
            _controllerImuThread?.Join(1000);
            long controllerImuJoinMs = stageProfile.ElapsedMilliseconds;
            stageProfile.Restart();
            _officialHeadTelemetryThread?.Join(1000);
            long officialHeadJoinMs = stageProfile.ElapsedMilliseconds;

            EnterpriseCollectionFileWriter.StopStats writerStats = _fileWriter != null
                ? _fileWriter.Stop(2000)
                : new EnterpriseCollectionFileWriter.StopStats();
            string recordDir = _fileWriter != null ? _fileWriter.RecordDir : "none";
            _fileWriter = null;

            _stopwatch.Stop();
            ClearLatestEnterpriseHead();
            ClearLatestEnterpriseController();
            ClearLatestTobControllerImu();
            ClearLatestOfficialHeadTelemetry();
            stopProfile.Stop();
            Debug.Log($"{Tag} stopped: {recordDir}");
            Debug.Log($"{Tag} stop profile: totalMs={stopProfile.ElapsedMilliseconds}, " +
                $"enterpriseJoinMs={enterpriseJoinMs}, writerJoinMs={writerStats.WriterJoinMs}, " +
                $"controllerImuJoinMs={controllerImuJoinMs}, " +
                $"officialHeadJoinMs={officialHeadJoinMs}, " +
                $"closeWritersMs={writerStats.CloseWritersMs}, " +
                $"pendingBeforeStop={pendingBeforeStop}, pendingAfterStop={writerStats.PendingAfterStop}, " +
                $"enterpriseThreadAlive={(_enterpriseThread != null && _enterpriseThread.IsAlive)}, " +
                $"writerThreadAlive={writerStats.WriterThreadAlive}");
        }

        private void StopFileWriter(string reason)
        {
            if (_fileWriter == null)
            {
                return;
            }

            int pendingBeforeStop = _fileWriter.PendingCount;
            string recordDir = _fileWriter.RecordDir;
            EnterpriseCollectionFileWriter.StopStats writerStats = _fileWriter.Stop(2000);
            _fileWriter = null;
            Debug.Log($"{Tag} file writer stopped: {recordDir}, reason={reason}");
            Debug.Log($"{Tag} file writer stop profile: writerJoinMs={writerStats.WriterJoinMs}, " +
                $"closeWritersMs={writerStats.CloseWritersMs}, " +
                $"pendingBeforeStop={pendingBeforeStop}, pendingAfterStop={writerStats.PendingAfterStop}, " +
                $"writerThreadAlive={writerStats.WriterThreadAlive}");
        }

        private void EnterpriseLoop()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            AndroidJNI.AttachCurrentThread();
#endif
            try
            {
                long nextSampleTicks = Stopwatch.GetTimestamp();
                while (_recording)
                {
                    int sampleHz = EnterpriseSampleHz;
                    if (sampleHz <= 0)
                    {
                        Thread.Sleep(100);
                        nextSampleTicks = Stopwatch.GetTimestamp();
                        continue;
                    }

                    bool serializeDiagnostics = IsFileWriterAccepting;
                    if (collectHeadPose)
                    {
                        Enqueue(EnterpriseHeadFile, BuildEnterpriseHeadLine(serializeDiagnostics));
                    }

                    if (collectControllerPose)
                    {
                        Enqueue(EnterpriseControllerFile,
                            BuildEnterpriseControllerLine(serializeDiagnostics));
                    }

                    LogEnterpriseSampleRateIfNeeded();

                    long intervalTicks = Math.Max(1, (long)Math.Round(Stopwatch.Frequency / (double)sampleHz));
                    nextSampleTicks += intervalTicks;
                    long remainingTicks = nextSampleTicks - Stopwatch.GetTimestamp();
                    if (remainingTicks > 0)
                    {
                        int sleepMs = (int)(remainingTicks * 1000 / Stopwatch.Frequency);
                        if (sleepMs > 0)
                        {
                            Thread.Sleep(sleepMs);
                        }
                        else
                        {
                            Thread.Yield();
                        }
                    }
                    else
                    {
                        nextSampleTicks = Stopwatch.GetTimestamp();
                        Thread.Yield();
                    }
                }
            }
            finally
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                AndroidJNI.DetachCurrentThread();
#endif
            }
        }

        private void ControllerImuLoop()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            AndroidJNI.AttachCurrentThread();
#endif
            try
            {
                long nextSampleTicks = Stopwatch.GetTimestamp();
                while (_recording)
                {
                    const int sampleHz = TobControllerImuSampleHz;
                    if (!collectControllerPose)
                    {
                        Thread.Sleep(100);
                        nextSampleTicks = Stopwatch.GetTimestamp();
                        continue;
                    }

                    ControllerImuData[] controllerImu =
                        PicoEnterpriseTelemetryProvider.GetControllerImuData(0L);
                    if (controllerImu != null)
                    {
                        lock (s_latestTobControllerImuLock)
                        {
                            s_latestTobControllerImu = controllerImu;
                        }
                    }

                    long intervalTicks = Math.Max(1, (long)Math.Round(Stopwatch.Frequency / (double)sampleHz));
                    nextSampleTicks += intervalTicks;
                    long remainingTicks = nextSampleTicks - Stopwatch.GetTimestamp();
                    if (remainingTicks > 0)
                    {
                        int sleepMs = (int)(remainingTicks * 1000 / Stopwatch.Frequency);
                        if (sleepMs > 0)
                        {
                            Thread.Sleep(sleepMs);
                        }
                        else
                        {
                            Thread.Yield();
                        }
                    }
                    else
                    {
                        nextSampleTicks = Stopwatch.GetTimestamp();
                        Thread.Yield();
                    }
                }
            }
            finally
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                // This is our own long-lived worker. Detaching at shutdown prevents a service
                // rebind/restart from retaining the thread's JNI state and local-reference table.
                AndroidJNI.DetachCurrentThread();
#endif
            }
        }

        private void OfficialHeadTelemetryLoop()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            AndroidJNI.AttachCurrentThread();
#endif
            int consecutiveFailures = 0;
            long successfulSamples = 0;
            long attemptedSamples = 0;
            try
            {
                long nextSampleTicks = Stopwatch.GetTimestamp();
                while (_recording)
                {
                    if (!collectHeadPose || !collectOfficialHeadTelemetry)
                    {
                        Thread.Sleep(100);
                        nextSampleTicks = Stopwatch.GetTimestamp();
                        continue;
                    }

                    try
                    {
                        attemptedSamples++;
                        if (attemptedSamples == 1)
                        {
                            Debug.Log(
                                $"{Tag} PICO SDK {PXR_Constants.SDKVersion} Head telemetry " +
                                "first call started on attached JNI thread " +
                                $"predictTimeNs={OfficialHeadTelemetryPredictTimeNs}");
                        }
                        PicoEnterpriseTelemetryProvider.GetOfficialHeadTelemetry(
                            OfficialHeadTelemetryPredictTimeNs,
                            out Unity.XR.PICO.TOBSupport.Pose pose,
                            out IMUData imu,
                            out string telemetryTransport);
                        OfficialHeadTelemetry telemetry = CreateOfficialHeadTelemetry(pose, imu);
                        if (telemetry.PoseAvailable || telemetry.ImuAvailable)
                        {
                            lock (s_latestOfficialHeadTelemetryLock)
                            {
                                MergeOfficialHeadTelemetry(
                                    ref s_latestOfficialHeadTelemetry,
                                    telemetry);
                            }
                            successfulSamples++;
                            if (successfulSamples == 1 ||
                                successfulSamples % (OfficialHeadTelemetrySampleHz * 10) == 0)
                            {
                                Debug.Log(
                                    $"{Tag} PICO SDK {PXR_Constants.SDKVersion} Head telemetry ok " +
                                    $"samples={successfulSamples}, " +
                                    $"poseAvailable={telemetry.PoseAvailable}, " +
                                    $"poseTimestampNs={telemetry.PoseTimestampNs}, " +
                                    $"predictTimeNs={OfficialHeadTelemetryPredictTimeNs}, " +
                                    $"poseConfidence={telemetry.PoseConfidence}, " +
                                    $"poseError={telemetry.PoseError}, " +
                                    $"imuAvailable={telemetry.ImuAvailable}, " +
                                    $"imuTimestampNs={telemetry.ImuTimestampNs}, " +
                                    $"transport={telemetryTransport}, " +
                                    $"imuAccelerationNative=" +
                                    $"({telemetry.LinearAccelerationNative.X:F6}," +
                                    $"{telemetry.LinearAccelerationNative.Y:F6}," +
                                    $"{telemetry.LinearAccelerationNative.Z:F6})");
                            }
                            consecutiveFailures = 0;
                        }
                        else
                        {
                            consecutiveFailures++;
                            if (consecutiveFailures == 1 ||
                                consecutiveFailures % (OfficialHeadTelemetrySampleHz * 10) == 0)
                            {
                                Debug.LogWarning(
                                    $"{Tag} PICO SDK {PXR_Constants.SDKVersion} Head telemetry " +
                                    $"returned empty count={consecutiveFailures}, " +
                                    $"attempts={attemptedSamples}");
                            }
                        }
                    }
                    catch (Exception exception)
                    {
                        consecutiveFailures++;
                        if (consecutiveFailures == 1 || consecutiveFailures % 600 == 0)
                        {
                            Debug.LogWarning(
                                $"{Tag} PICO SDK 3.4 Head telemetry failed " +
                                $"count={consecutiveFailures}: {exception.GetType().Name}: " +
                                exception.Message);
                        }
                    }

                    long intervalTicks = Math.Max(
                        1,
                        (long)Math.Round(
                            Stopwatch.Frequency / (double)OfficialHeadTelemetrySampleHz));
                    nextSampleTicks += intervalTicks;
                    long remainingTicks = nextSampleTicks - Stopwatch.GetTimestamp();
                    if (remainingTicks > 0)
                    {
                        int sleepMs = (int)(remainingTicks * 1000 / Stopwatch.Frequency);
                        if (sleepMs > 0)
                        {
                            Thread.Sleep(sleepMs);
                        }
                        else
                        {
                            Thread.Yield();
                        }
                    }
                    else
                    {
                        nextSampleTicks = Stopwatch.GetTimestamp();
                        Thread.Yield();
                    }
                }
            }
            finally
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                AndroidJNI.DetachCurrentThread();
#endif
            }
        }

        public int EnterpriseSampleHz
        {
            get => Volatile.Read(ref enterpriseSampleHz);
        }

        public void SetEnterpriseSampleHz(int sampleHz)
        {
            int clampedSampleHz = Mathf.Clamp(sampleHz, 0, 1000);
            Volatile.Write(ref enterpriseSampleHz, clampedSampleHz);
            Debug.Log($"{Tag} enterprise sample hz set to {clampedSampleHz}");
        }

        public int AdjustEnterpriseSampleHz(int delta)
        {
            int sampleHz = EnterpriseSampleHz + delta;
            SetEnterpriseSampleHz(sampleHz);
            return EnterpriseSampleHz;
        }

        public static bool TryGetLatestEnterpriseHeadForTcp(out string pose, out int status)
        {
            return TryGetLatestEnterpriseHeadForTcp(out pose, out status, out _);
        }

        public static bool TryGetLatestEnterpriseHeadForTcp(out string pose, out int status, out long sampleSeq)
        {
            bool success = TryGetLatestEnterpriseHeadForTcp(out EnterpriseHeadTcpPose head, out sampleSeq);
            pose = head.Pose;
            status = head.Status;
            return success;
        }

        public static bool TryGetLatestEnterpriseHeadForTcp(out EnterpriseHeadTcpPose head, out long sampleSeq)
        {
            lock (s_latestEnterpriseHeadLock)
            {
                head = s_latestEnterpriseHead;
                sampleSeq = s_latestEnterpriseHeadSampleSeq;
                return s_hasLatestEnterpriseHead && head.HasPose && !string.IsNullOrEmpty(head.Pose);
            }
        }

        public static bool TryGetLatestEnterpriseControllerForTcp(
            out EnterpriseControllerTcpPose left,
            out EnterpriseControllerTcpPose right,
            out long sampleSeq)
        {
            lock (s_latestEnterpriseControllerLock)
            {
                left = s_latestEnterpriseLeftController;
                right = s_latestEnterpriseRightController;
                sampleSeq = s_latestEnterpriseControllerSampleSeq;
                return s_hasLatestEnterpriseController && (left.HasPose || right.HasPose);
            }
        }

        private string BuildEnterpriseHeadLine(bool serializeDiagnostics)
        {
            try
            {
                double predictTimeMs = GetEnterpriseHeadPredictTimeMs();
                PxrSensorState2 sensorState = new PxrSensorState2();
                int sensorFrameIndex = 0;
                int result = PicoEnterpriseTelemetryProvider.GetLegacyHeadState(
                    predictTimeMs,
                    ref sensorState,
                    ref sensorFrameIndex);

                if (result == 0)
                {
                    Interlocked.Increment(ref _enterpriseHeadSamplesInWindow);
                    // Publish the native sample before any diagnostic JSON work so
                    // a serialization failure cannot stall live TCP tracking.
                    UpdateLatestEnterpriseHead(sensorState, preferOfficialHeadPose);
                }

                if (!serializeDiagnostics)
                {
                    return null;
                }

                JsonData data = BuildUnityHeadJson(sensorState);
                data["sensorFrameIndex"] = sensorFrameIndex;
                data["predictTimeMs"] = predictTimeMs;
                data["predictTimeMode"] = useDynamicPredictedDisplayTimeForEnterpriseHead
                    ? "dynamic"
                    : "latest";
                data["nativeResult"] = result;
                return BuildEnvelope("enterprise", "head", Interlocked.Increment(ref _enterpriseHeadSeq),
                    result == 0, data, null);
            }
            catch (Exception e)
            {
                return serializeDiagnostics
                    ? BuildEnvelope("enterprise", "head", Interlocked.Increment(ref _enterpriseHeadSeq),
                        false, null, e.GetType().Name + ": " + e.Message)
                    : null;
            }
        }

        private double GetEnterpriseHeadPredictTimeMs()
        {
            if (useDynamicPredictedDisplayTimeForEnterpriseHead)
            {
                return PXR_Enterprise.GetPredictedDisplayTime();
            }

            // A prediction horizon is not a polling interval. Production collection uses
            // the latest available sample, so request it explicitly with a zero horizon.
            return 0;
        }

        private string BuildEnterpriseControllerLine(bool serializeDiagnostics)
        {
            try
            {
                double predictTimeMs = GetEnterpriseControllerPredictTimeMs();
                PoseInfo[] poses =
                    PicoEnterpriseTelemetryProvider.GetLegacyControllerPose(predictTimeMs);
                ControllerImuData[] controllerImu = GetLatestTobControllerImu();
                if (poses != null || controllerImu != null)
                {
                    Interlocked.Increment(ref _enterpriseControllerSamplesInWindow);
                    UpdateLatestEnterpriseController(poses, controllerImu);
                }
                if (!serializeDiagnostics)
                {
                    return null;
                }
                JsonData data = new JsonData();
                data["predictTimeMs"] = predictTimeMs;
                data["predictTimeMode"] = "latest";
                data["left"] = BuildEnterpriseControllerSide(
                    poses, controllerImu, 0, _enterpriseLeftControllerLog);
                data["right"] = BuildEnterpriseControllerSide(
                    poses, controllerImu, 1, _enterpriseRightControllerLog);
                return BuildEnvelope("enterprise", "controller_pose",
                    Interlocked.Increment(ref _enterpriseControllerSeq), poses != null || controllerImu != null,
                    data, null);
            }
            catch (Exception e)
            {
                return serializeDiagnostics
                    ? BuildEnvelope("enterprise", "controller_pose",
                        Interlocked.Increment(ref _enterpriseControllerSeq), false, null,
                        e.GetType().Name + ": " + e.Message)
                    : null;
            }
        }

        private double GetEnterpriseControllerPredictTimeMs()
        {
            return 0;
        }

        private string BuildUnityHeadLine()
        {
            try
            {
                PxrSensorState2 sensor = new PxrSensorState2();
                int sensorFrameIndex = 0;
                PXR_System.GetPredictedMainSensorStateNew(ref sensor, ref sensorFrameIndex);
                JsonData data = BuildUnityHeadJson(sensor);
                data["sensorFrameIndex"] = sensorFrameIndex;
                return BuildEnvelope("unity", "head", Interlocked.Increment(ref _unityHeadSeq), true, data, null);
            }
            catch (Exception e)
            {
                return BuildEnvelope("unity", "head", Interlocked.Increment(ref _unityHeadSeq),
                    false, null, e.GetType().Name + ": " + e.Message);
            }
        }

        private string BuildUnityControllerLine()
        {
            try
            {
                double predictTime = PXR_Enterprise.GetPredictedDisplayTime() * 1000;
                JsonData data = new JsonData();
                data["predictTime"] = predictTime;
                data["left"] = BuildUnityControllerSide(PXR_Input.Controller.LeftController, XRNode.LeftHand, predictTime,
                    _unityLeftControllerLog);
                data["right"] = BuildUnityControllerSide(PXR_Input.Controller.RightController, XRNode.RightHand, predictTime,
                    _unityRightControllerLog);
                return BuildEnvelope("unity", "controller_pose",
                    Interlocked.Increment(ref _unityControllerSeq), true, data, null);
            }
            catch (Exception e)
            {
                return BuildEnvelope("unity", "controller_pose",
                    Interlocked.Increment(ref _unityControllerSeq), false, null, e.GetType().Name + ": " + e.Message);
            }
        }

        private JsonData BuildEnterpriseControllerSide(PoseInfo[] poses, ControllerImuData[] controllerImu,
            int index, ControllerLogState logState)
        {
            JsonData json = new JsonData();
            PoseInfo poseInfo = poses != null && index >= 0 && index < poses.Length ? poses[index] : null;
            LogEnterpriseControllerState(logState, poseInfo);
            bool hasPose = IsValidEnterpriseControllerPose(poseInfo);
            json["hasPose"] = hasPose;
            if (hasPose)
            {
                json["pose"] = BuildPoseJson(poseInfo);
            }
            AppendNativePosePair(json, CreateNativePosePair(poseInfo));
            AppendTobControllerImu(json, CreateTobControllerImu(controllerImu, index));

            return json;
        }

        private JsonData BuildUnityControllerSide(PXR_Input.Controller controller, XRNode xrNode, double predictTime,
            ControllerLogState logState)
        {
            Vector3 position = PXR_Input.GetControllerPredictPosition(controller, predictTime);
            Quaternion rotation = PXR_Input.GetControllerPredictRotation(controller, predictTime);
            InputDevice inputDevice = InputDevices.GetDeviceAtXRNode(xrNode);

            JsonData json = BuildInputJson(inputDevice, out ControllerInputSnapshot inputSnapshot);
            json["pose"] = GetPoseStr(position, rotation);
            json["isValid"] = inputDevice.isValid;
            json["deviceName"] = inputDevice.name;
            LogUnityControllerState(logState, inputDevice, inputSnapshot, position, rotation);
            return json;
        }

        private JsonData BuildInputJson(InputDevice inputDevice, out ControllerInputSnapshot inputSnapshot)
        {
            bool axis2DSuccess = inputDevice.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 axis2D);
            bool axisClickSuccess = inputDevice.TryGetFeatureValue(CommonUsages.primary2DAxisClick, out bool axisClick);
            bool gripSuccess = inputDevice.TryGetFeatureValue(CommonUsages.grip, out float grip);
            bool triggerSuccess = inputDevice.TryGetFeatureValue(CommonUsages.trigger, out float trigger);
            bool primaryButtonSuccess = inputDevice.TryGetFeatureValue(CommonUsages.primaryButton, out bool primaryButton);
            bool secondaryButtonSuccess = inputDevice.TryGetFeatureValue(CommonUsages.secondaryButton, out bool secondaryButton);
            bool menuButtonSuccess = inputDevice.TryGetFeatureValue(CommonUsages.menuButton, out bool menuButton);

            inputSnapshot = new ControllerInputSnapshot
            {
                Axis2DSuccess = axis2DSuccess,
                AxisClickSuccess = axisClickSuccess,
                GripSuccess = gripSuccess,
                TriggerSuccess = triggerSuccess,
                PrimaryButtonSuccess = primaryButtonSuccess,
                SecondaryButtonSuccess = secondaryButtonSuccess,
                MenuButtonSuccess = menuButtonSuccess
            };

            JsonData json = new JsonData();
            json["axisX"] = axis2D.x;
            json["axisY"] = axis2D.y;
            json["axisClick"] = axisClick;
            json["grip"] = grip;
            json["trigger"] = trigger;
            json["primaryButton"] = primaryButton;
            json["secondaryButton"] = secondaryButton;
            json["menuButton"] = menuButton;
            return json;
        }

        private void LogUnityControllerState(ControllerLogState state, InputDevice inputDevice,
            ControllerInputSnapshot inputSnapshot, Vector3 position, Quaternion rotation)
        {
            string featureMask = inputSnapshot.GetFeatureMask();
            bool stateChanged = !state.Initialized ||
                state.IsValid != inputDevice.isValid ||
                state.DeviceName != inputDevice.name ||
                state.FeatureMask != featureMask;

            if (stateChanged)
            {
                Debug.Log($"{Tag} controller state source=unity side={state.Side} elapsedMs={_stopwatch.ElapsedMilliseconds} " +
                    $"isValid={inputDevice.isValid} deviceName={inputDevice.name} featureMask={featureMask} " +
                    $"position={GetPosePositionStr(position)} rotation={GetPoseRotationStr(rotation)}");
            }

            UpdatePoseStagnation(state, "unity", position, rotation, GetUnityPoseStagnationThreshold());
            state.Initialized = true;
            state.HasPose = true;
            state.IsValid = inputDevice.isValid;
            state.DeviceName = inputDevice.name;
            state.FeatureMask = featureMask;
            state.Position = position;
            state.Rotation = rotation;
        }

        private void LogEnterpriseControllerState(ControllerLogState state, PoseInfo poseInfo)
        {
            bool hasPose = IsValidEnterpriseControllerPose(poseInfo);
            int confidence = hasPose ? poseInfo.confidence : -1;
            int poseError = hasPose ? poseInfo.poseError : 0;
            long timestamp = hasPose ? poseInfo.timestamp : 0L;
            bool stateChanged = !state.Initialized ||
                state.HasPose != hasPose ||
                state.Confidence != confidence ||
                state.PoseError != poseError;

            if (stateChanged)
            {
                Debug.Log($"{Tag} controller state source=enterprise side={state.Side} elapsedMs={_stopwatch.ElapsedMilliseconds} " +
                    $"hasPose={hasPose} confidence={confidence} poseError={poseError} timeStampNs={timestamp}");
            }

            if (hasPose)
            {
                Vector3 position = new Vector3((float)poseInfo.x, (float)poseInfo.y, (float)poseInfo.z);
                Quaternion rotation = new Quaternion((float)poseInfo.rx, (float)poseInfo.ry, (float)poseInfo.rz, (float)poseInfo.rw);
                UpdatePoseStagnation(state, "enterprise", position, rotation, GetEnterprisePoseStagnationThreshold());
                UpdateTimestampStagnation(state, timestamp, GetEnterprisePoseStagnationThreshold());
                state.Position = position;
                state.Rotation = rotation;
                state.Timestamp = timestamp;
            }
            else
            {
                state.SamePoseCount = 0;
                state.SameTimestampCount = 0;
            }

            state.Initialized = true;
            state.HasPose = hasPose;
            state.Confidence = confidence;
            state.PoseError = poseError;
        }

        private void UpdatePoseStagnation(ControllerLogState state, string source, Vector3 position, Quaternion rotation,
            int threshold)
        {
            if (state.Initialized && state.HasPose && IsSamePose(state.Position, state.Rotation, position, rotation))
            {
                state.SamePoseCount++;
                LogStagnationIfNeeded(state, source, "pose", state.SamePoseCount, threshold,
                    $"position={GetPosePositionStr(position)} rotation={GetPoseRotationStr(rotation)}");
            }
            else
            {
                state.SamePoseCount = 0;
                state.LastPoseStagnationLogMs = 0;
            }
        }

        private void UpdateTimestampStagnation(ControllerLogState state, long timestamp, int threshold)
        {
            if (state.Initialized && state.HasPose && state.Timestamp == timestamp)
            {
                state.SameTimestampCount++;
                LogStagnationIfNeeded(state, "enterprise", "timestamp", state.SameTimestampCount, threshold,
                    $"timeStampNs={timestamp}");
            }
            else
            {
                state.SameTimestampCount = 0;
                state.LastTimestampStagnationLogMs = 0;
            }
        }

        private void LogStagnationIfNeeded(ControllerLogState state, string source, string kind, int count, int threshold,
            string detail)
        {
            if (count < threshold)
            {
                return;
            }

            long elapsedMs = _stopwatch.ElapsedMilliseconds;
            bool firstLog = count == threshold;
            bool periodicLog = elapsedMs - (kind == "pose" ? state.LastPoseStagnationLogMs : state.LastTimestampStagnationLogMs) >= 5000;
            if (!firstLog && !periodicLog)
            {
                return;
            }

            if (kind == "pose")
            {
                state.LastPoseStagnationLogMs = elapsedMs;
            }
            else
            {
                state.LastTimestampStagnationLogMs = elapsedMs;
            }

            Debug.Log($"{Tag} controller stagnant source={source} side={state.Side} kind={kind} elapsedMs={elapsedMs} " +
                $"sameCount={count} threshold={threshold} {detail}");
        }

        private JsonData BuildUnityHeadJson(PxrSensorState2 sensorState)
        {
            JsonData json = new JsonData();
            json["pose"] = GetPoseStr(sensorState.pose.position, sensorState.pose.orientation);
            json["status"] = sensorState.status;
            long poseTimeStampNs = unchecked((long)sensorState.poseTimeStampNs);
            json["timeStampNs"] = poseTimeStampNs;
            json["poseTimeStampNs"] = poseTimeStampNs;
            AppendNativeKinematics(json, CreateNativePoseKinematics(sensorState));
            AppendNativePosePair(json, CreateNativePosePair(sensorState));
            return json;
        }

        private static void UpdateLatestEnterpriseHead(
            PxrSensorState2 sensorState,
            bool preferOfficialPose)
        {
            long poseTimeStampNs = unchecked((long)sensorState.poseTimeStampNs);
            OfficialHeadTelemetry officialTelemetry = GetLatestOfficialHeadTelemetry();
            EnterpriseHeadTcpPose head = new EnterpriseHeadTcpPose
            {
                HasPose = true,
                Pose = GetPoseStr(sensorState.pose.position, sensorState.pose.orientation),
                Status = sensorState.status,
                TimeStampNs = poseTimeStampNs,
                PoseError = 0,
                PoseSource = "pxr_sensor_state2",
                NativePosePair = CreateNativePosePair(sensorState),
                OfficialTelemetry = officialTelemetry,
                NativeKinematics = CreateNativePoseKinematics(sensorState)
            };
            if (preferOfficialPose && officialTelemetry.PoseAvailable)
            {
                head.Pose = officialTelemetry.Pose;
                head.Status = officialTelemetry.PoseConfidence;
                head.TimeStampNs = officialTelemetry.PoseTimestampNs;
                head.PoseError = officialTelemetry.PoseError;
                head.PoseSource = "pico_enterprise_get_head_pose_sdk_3_4_0";
            }
            lock (s_latestEnterpriseHeadLock)
            {
                s_latestEnterpriseHead = head;
                s_latestEnterpriseHeadSampleSeq++;
                s_hasLatestEnterpriseHead = true;
            }
        }

        private static OfficialHeadTelemetry CreateOfficialHeadTelemetry(
            Unity.XR.PICO.TOBSupport.Pose pose,
            IMUData imu)
        {
            OfficialHeadTelemetry telemetry = new OfficialHeadTelemetry();
            if (pose != null &&
                IsFinite(pose.x) && IsFinite(pose.y) && IsFinite(pose.z) &&
                IsFinite(pose.rx) && IsFinite(pose.ry) && IsFinite(pose.rz) &&
                IsFinite(pose.rw) &&
                pose.rx * pose.rx + pose.ry * pose.ry + pose.rz * pose.rz +
                    pose.rw * pose.rw > 1.0e-16)
            {
                telemetry.PoseAvailable = true;
                telemetry.PoseTimestampNs = pose.timestamp;
                telemetry.PoseConfidence = pose.confidence;
                telemetry.PoseError = pose.poseError;
                telemetry.Pose = GetPoseStr(
                    new Vector3((float)pose.x, (float)pose.y, (float)pose.z),
                    new Quaternion(
                        (float)pose.rx,
                        (float)pose.ry,
                        (float)pose.rz,
                        (float)pose.rw));
            }

            if (imu != null &&
                IsFinite(imu.vx) && IsFinite(imu.vy) && IsFinite(imu.vz) &&
                IsFinite(imu.ax) && IsFinite(imu.ay) && IsFinite(imu.az) &&
                IsFinite(imu.wx) && IsFinite(imu.wy) && IsFinite(imu.wz) &&
                IsFinite(imu.w_ax) && IsFinite(imu.w_ay) && IsFinite(imu.w_az))
            {
                telemetry.ImuAvailable = true;
                telemetry.ImuTimestampNs = imu.timestamp;
                telemetry.LinearVelocityNative =
                    new DoubleVector3(imu.vx, imu.vy, imu.vz);
                telemetry.LinearAccelerationNative =
                    new DoubleVector3(imu.ax, imu.ay, imu.az);
                telemetry.AngularVelocityNative =
                    new DoubleVector3(imu.wx, imu.wy, imu.wz);
                telemetry.AngularAccelerationNative =
                    new DoubleVector3(imu.w_ax, imu.w_ay, imu.w_az);
            }

            return telemetry;
        }

        private static void MergeOfficialHeadTelemetry(
            ref OfficialHeadTelemetry destination,
            OfficialHeadTelemetry update)
        {
            if (update.PoseAvailable)
            {
                destination.PoseAvailable = true;
                destination.Pose = update.Pose;
                destination.PoseTimestampNs = update.PoseTimestampNs;
                destination.PoseConfidence = update.PoseConfidence;
                destination.PoseError = update.PoseError;
            }

            if (update.ImuAvailable)
            {
                destination.ImuAvailable = true;
                destination.ImuTimestampNs = update.ImuTimestampNs;
                destination.LinearVelocityNative = update.LinearVelocityNative;
                destination.LinearAccelerationNative = update.LinearAccelerationNative;
                destination.AngularVelocityNative = update.AngularVelocityNative;
                destination.AngularAccelerationNative = update.AngularAccelerationNative;
            }
        }

        private static OfficialHeadTelemetry GetLatestOfficialHeadTelemetry()
        {
            lock (s_latestOfficialHeadTelemetryLock)
            {
                return s_latestOfficialHeadTelemetry;
            }
        }

        private static void ClearLatestOfficialHeadTelemetry()
        {
            lock (s_latestOfficialHeadTelemetryLock)
            {
                s_latestOfficialHeadTelemetry = new OfficialHeadTelemetry();
            }
        }

        private static void ClearLatestEnterpriseHead()
        {
            lock (s_latestEnterpriseHeadLock)
            {
                s_hasLatestEnterpriseHead = false;
                s_latestEnterpriseHead = new EnterpriseHeadTcpPose();
                s_latestEnterpriseHeadSampleSeq = 0;
            }
        }

        private static void UpdateLatestEnterpriseController(PoseInfo[] poses, ControllerImuData[] controllerImu)
        {
            EnterpriseControllerTcpPose left = CreateEnterpriseControllerTcpPose(poses, controllerImu, 0);
            EnterpriseControllerTcpPose right = CreateEnterpriseControllerTcpPose(poses, controllerImu, 1);
            lock (s_latestEnterpriseControllerLock)
            {
                s_latestEnterpriseLeftController = left;
                s_latestEnterpriseRightController = right;
                s_latestEnterpriseControllerSampleSeq++;
                s_hasLatestEnterpriseController = left.HasPose || right.HasPose ||
                    left.TobControllerImu.Available || right.TobControllerImu.Available;
            }
        }

        private static void ClearLatestEnterpriseController()
        {
            lock (s_latestEnterpriseControllerLock)
            {
                s_hasLatestEnterpriseController = false;
                s_latestEnterpriseLeftController = new EnterpriseControllerTcpPose();
                s_latestEnterpriseRightController = new EnterpriseControllerTcpPose();
                s_latestEnterpriseControllerSampleSeq = 0;
            }
        }

        private static ControllerImuData[] GetLatestTobControllerImu()
        {
            lock (s_latestTobControllerImuLock)
            {
                return s_latestTobControllerImu;
            }
        }

        private static void ClearLatestTobControllerImu()
        {
            lock (s_latestTobControllerImuLock)
            {
                s_latestTobControllerImu = null;
            }
        }

        private static EnterpriseControllerTcpPose CreateEnterpriseControllerTcpPose(
            PoseInfo[] poses, ControllerImuData[] controllerImu, int index)
        {
            PoseInfo poseInfo = poses != null && index >= 0 && index < poses.Length ? poses[index] : null;
            TobControllerImu tobImu = CreateTobControllerImu(controllerImu, index);
            if (!IsValidEnterpriseControllerPose(poseInfo))
            {
                EnterpriseControllerTcpPose invalid = CreateInvalidEnterpriseControllerTcpPose();
                invalid.TobControllerImu = tobImu;
                return invalid;
            }

            return new EnterpriseControllerTcpPose
            {
                HasPose = true,
                Pose = GetPoseStr(
                    new Vector3((float)poseInfo.x, (float)poseInfo.y, (float)poseInfo.z),
                    new Quaternion((float)poseInfo.rx, (float)poseInfo.ry, (float)poseInfo.rz, (float)poseInfo.rw)),
                Status = poseInfo.confidence,
                TimeStampNs = poseInfo.timestamp,
                Type = poseInfo.type,
                PoseError = poseInfo.poseError,
                NativePosePair = CreateNativePosePair(poseInfo),
                TobControllerImu = tobImu,
                NativeKinematics = new NativePoseKinematics
                {
                    Available = poseInfo.nativeKinematicsValid,
                    PoseTimeStampNs = poseInfo.timestamp,
                    AngularVelocity = poseInfo.angularVelocity,
                    LinearVelocity = poseInfo.linearVelocity,
                    AngularAcceleration = poseInfo.angularAcceleration,
                    LinearAcceleration = poseInfo.linearAcceleration
                }
            };
        }

        private static TobControllerImu CreateTobControllerImu(ControllerImuData[] values, int index)
        {
            ControllerImuData value = values != null && index >= 0 && index < values.Length
                ? values[index]
                : null;
            if (value == null ||
                !IsFinite(value.vx) || !IsFinite(value.vy) || !IsFinite(value.vz) ||
                !IsFinite(value.ax) || !IsFinite(value.ay) || !IsFinite(value.az) ||
                !IsFinite(value.wx) || !IsFinite(value.wy) || !IsFinite(value.wz) ||
                !IsFinite(value.w_ax) || !IsFinite(value.w_ay) || !IsFinite(value.w_az))
            {
                return new TobControllerImu();
            }

            return new TobControllerImu
            {
                Available = true,
                TimestampNs = value.timestamp,
                LinearVelocityNative = new DoubleVector3(value.vx, value.vy, value.vz),
                LinearAccelerationNative = new DoubleVector3(value.ax, value.ay, value.az),
                AngularVelocityNative = new DoubleVector3(value.wx, value.wy, value.wz),
                AngularAccelerationNative = new DoubleVector3(value.w_ax, value.w_ay, value.w_az)
            };
        }

        public static EnterpriseControllerTcpPose CreateInvalidEnterpriseControllerTcpPose()
        {
            return new EnterpriseControllerTcpPose
            {
                HasPose = false,
                Pose = InvalidControllerPose,
                Status = 0,
                TimeStampNs = 0,
                Type = 0,
                PoseError = 0
            };
        }

        private static bool IsValidEnterpriseControllerPose(PoseInfo poseInfo)
        {
            if (poseInfo == null)
            {
                return false;
            }

            bool isZeroIdentityPose =
                Math.Abs(poseInfo.x) <= 0.000001 &&
                Math.Abs(poseInfo.y) <= 0.000001 &&
                Math.Abs(poseInfo.z) <= 0.000001 &&
                Math.Abs(poseInfo.rx) <= 0.000001 &&
                Math.Abs(poseInfo.ry) <= 0.000001 &&
                Math.Abs(poseInfo.rz) <= 0.000001 &&
                Math.Abs(poseInfo.rw - 1.0) <= 0.000001;
            bool isFinitePose =
                IsFinite(poseInfo.x) &&
                IsFinite(poseInfo.y) &&
                IsFinite(poseInfo.z) &&
                IsFinite(poseInfo.rx) &&
                IsFinite(poseInfo.ry) &&
                IsFinite(poseInfo.rz) &&
                IsFinite(poseInfo.rw);
            bool isPositionInRange =
                Math.Abs(poseInfo.x) <= MaxValidEnterpriseControllerPositionMeters &&
                Math.Abs(poseInfo.y) <= MaxValidEnterpriseControllerPositionMeters &&
                Math.Abs(poseInfo.z) <= MaxValidEnterpriseControllerPositionMeters;
            return (poseInfo.timestamp != 0 || !isZeroIdentityPose) && isFinitePose && isPositionInRange;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private JsonData BuildPoseJson(PoseInfo poseInfo)
        {
            JsonData json = new JsonData();
            json["pose"] = GetPoseStr(
                new Vector3((float)poseInfo.x, (float)poseInfo.y, (float)poseInfo.z),
                new Quaternion((float)poseInfo.rx, (float)poseInfo.ry, (float)poseInfo.rz, (float)poseInfo.rw));
            json["status"] = poseInfo.confidence;
            json["timeStampNs"] = poseInfo.timestamp;
            json["poseTimeStampNs"] = poseInfo.timestamp;
            json["type"] = poseInfo.type;
            json["poseError"] = poseInfo.poseError;
            AppendNativePosePair(json, CreateNativePosePair(poseInfo));
            AppendNativeKinematics(json, new NativePoseKinematics
            {
                Available = poseInfo.nativeKinematicsValid,
                PoseTimeStampNs = poseInfo.timestamp,
                AngularVelocity = poseInfo.angularVelocity,
                LinearVelocity = poseInfo.linearVelocity,
                AngularAcceleration = poseInfo.angularAcceleration,
                LinearAcceleration = poseInfo.linearAcceleration
            });
            return json;
        }

        public static NativePoseKinematics CreateNativePoseKinematics(PxrSensorState2 sensorState)
        {
            return new NativePoseKinematics
            {
                Available = true,
                PoseTimeStampNs = unchecked((long)sensorState.poseTimeStampNs),
                AngularVelocity = ToVector3(sensorState.angularVelocity),
                LinearVelocity = ToVector3(sensorState.linearVelocity),
                AngularAcceleration = ToVector3(sensorState.angularAcceleration),
                LinearAcceleration = ToVector3(sensorState.linearAcceleration)
            };
        }

        public static NativePosePair CreateNativePosePair(PxrSensorState2 sensorState)
        {
            long timestampNs = unchecked((long)sensorState.poseTimeStampNs);
            return new NativePosePair
            {
                Local = new NativePoseSample
                {
                    Available = true,
                    Pose = GetPoseStr(sensorState.pose.position, sensorState.pose.orientation),
                    TimeStampNs = timestampNs,
                    Status = sensorState.status
                },
                Global = new NativePoseSample
                {
                    Available = true,
                    Pose = GetPoseStr(sensorState.globalPose.position, sensorState.globalPose.orientation),
                    TimeStampNs = timestampNs,
                    Status = sensorState.status
                }
            };
        }

        private static NativePosePair CreateNativePosePair(PoseInfo poseInfo)
        {
            if (poseInfo == null)
            {
                return new NativePosePair();
            }

            NativePosePair pair = new NativePosePair
            {
                Local = new NativePoseSample
                {
                    Available = IsValidEnterpriseControllerPose(poseInfo),
                    Pose = GetPoseStr(
                        new Vector3((float)poseInfo.x, (float)poseInfo.y, (float)poseInfo.z),
                        new Quaternion((float)poseInfo.rx, (float)poseInfo.ry,
                            (float)poseInfo.rz, (float)poseInfo.rw)),
                    TimeStampNs = poseInfo.timestamp,
                    Status = poseInfo.confidence
                }
            };
            if (poseInfo.globalPoseValid)
            {
                pair.Global = new NativePoseSample
                {
                    Available = true,
                    Pose = GetPoseStr(
                        new Vector3((float)poseInfo.globalX, (float)poseInfo.globalY,
                            (float)poseInfo.globalZ),
                        new Quaternion((float)poseInfo.globalRx, (float)poseInfo.globalRy,
                            (float)poseInfo.globalRz, (float)poseInfo.globalRw)),
                    TimeStampNs = poseInfo.globalTimestamp,
                    Status = poseInfo.globalConfidence
                };
            }
            return pair;
        }

        public static void AppendNativePosePair(JsonData deviceJson, NativePosePair pair)
        {
            if (deviceJson == null)
            {
                return;
            }

            RemoveJsonKeyIfPresent(deviceJson, "native_pose_pair_v1");
            if (!pair.Local.Available && !pair.Global.Available)
            {
                return;
            }

            JsonData value = new JsonData();
            value["schema_version"] = NativePosePairSchemaVersion;
            value["local"] = BuildNativePoseSampleJson(pair.Local, "pico_in_app");
            value["global"] = BuildNativePoseSampleJson(pair.Global, "pico_global");
            deviceJson["native_pose_pair_v1"] = value;
        }

        private static JsonData BuildNativePoseSampleJson(NativePoseSample sample, string frame)
        {
            JsonData value = new JsonData();
            value["available"] = sample.Available;
            value["frame"] = frame;
            value["pose"] = sample.Pose ?? InvalidControllerPose;
            value["timestamp_ns"] = sample.TimeStampNs;
            value["confidence"] = sample.Status;
            return value;
        }

        public static void AppendNativeKinematics(JsonData deviceJson, NativePoseKinematics kinematics)
        {
            if (deviceJson == null)
            {
                return;
            }

            RemoveJsonKeyIfPresent(deviceJson, "imu_v1");
            if (!kinematics.Available)
            {
                return;
            }

            JsonData imu = new JsonData();
            imu["schema_version"] = NativeKinematicsSchemaVersion;
            imu["pico_pose_timestamp_ns"] = kinematics.PoseTimeStampNs;
            imu["linear_velocity_mps"] = BuildVectorJson(kinematics.LinearVelocity);
            imu["angular_velocity_rad_s"] = BuildVectorJson(kinematics.AngularVelocity);
            imu["linear_acceleration_mps2"] = BuildVectorJson(kinematics.LinearAcceleration);
            imu["angular_acceleration_rad_s2"] = BuildVectorJson(kinematics.AngularAcceleration);
            deviceJson["imu_v1"] = imu;
        }

        public static void AppendTobControllerImu(JsonData deviceJson, TobControllerImu imuData)
        {
            if (deviceJson == null)
            {
                return;
            }

            RemoveJsonKeyIfPresent(deviceJson, "tob_controller_imu_v1");
            if (!imuData.Available)
            {
                return;
            }

            JsonData imu = new JsonData();
            imu["schema_version"] = TobControllerImuSchemaVersion;
            imu["timestamp_ns"] = imuData.TimestampNs;
            imu["linear_velocity_native"] = BuildVectorJson(imuData.LinearVelocityNative);
            imu["linear_acceleration_native"] = BuildVectorJson(imuData.LinearAccelerationNative);
            imu["angular_velocity_native"] = BuildVectorJson(imuData.AngularVelocityNative);
            imu["angular_acceleration_native"] = BuildVectorJson(imuData.AngularAccelerationNative);
            deviceJson["tob_controller_imu_v1"] = imu;
        }

        public static void AppendOfficialHeadTelemetry(
            JsonData deviceJson,
            OfficialHeadTelemetry telemetry)
        {
            if (deviceJson == null)
            {
                return;
            }

            RemoveJsonKeyIfPresent(deviceJson, "sdk_pose_v1");
            RemoveJsonKeyIfPresent(deviceJson, "sdk_imu_v1");

            if (telemetry.PoseAvailable)
            {
                JsonData pose = new JsonData();
                pose["schema_version"] = OfficialHeadPoseSchemaVersion;
                pose["predict_time_ns"] = OfficialHeadTelemetryPredictTimeNs;
                pose["pose"] = telemetry.Pose;
                pose["timestamp_ns"] = telemetry.PoseTimestampNs;
                pose["confidence"] = telemetry.PoseConfidence;
                pose["pose_error"] = telemetry.PoseError;
                deviceJson["sdk_pose_v1"] = pose;
            }

            if (telemetry.ImuAvailable)
            {
                JsonData imu = new JsonData();
                imu["schema_version"] = OfficialHeadImuSchemaVersion;
                imu["timestamp_ns"] = telemetry.ImuTimestampNs;
                imu["linear_velocity_native"] =
                    BuildVectorJson(telemetry.LinearVelocityNative);
                imu["linear_acceleration_native"] =
                    BuildVectorJson(telemetry.LinearAccelerationNative);
                imu["angular_velocity_native"] =
                    BuildVectorJson(telemetry.AngularVelocityNative);
                imu["angular_acceleration_native"] =
                    BuildVectorJson(telemetry.AngularAccelerationNative);
                deviceJson["sdk_imu_v1"] = imu;
            }
        }

        private static void RemoveJsonKeyIfPresent(JsonData value, string key)
        {
            if (value.ContainsKey(key))
            {
                value.Remove(key);
            }
        }

        private static Vector3 ToVector3(PxrVector3f value)
        {
            return new Vector3(value.x, value.y, value.z);
        }

        private static JsonData BuildVectorJson(Vector3 value)
        {
            JsonData vector = new JsonData();
            vector.SetJsonType(JsonType.Array);
            // This project ships an older LitJson whose object wrapper supports
            // Double but not Single. Unity Vector3 components are Single.
            vector.Add((double)value.x);
            vector.Add((double)value.y);
            vector.Add((double)value.z);
            return vector;
        }

        private static JsonData BuildVectorJson(DoubleVector3 value)
        {
            JsonData vector = new JsonData();
            vector.SetJsonType(JsonType.Array);
            vector.Add(value.X);
            vector.Add(value.Y);
            vector.Add(value.Z);
            return vector;
        }

        private string BuildEnvelope(string source, string kind, long seq, bool success, JsonData data, string error)
        {
            JsonData envelope = new JsonData();
            envelope["source"] = source;
            envelope["kind"] = kind;
            envelope["seq"] = seq;
            envelope["captureElapsedMs"] = _stopwatch.ElapsedMilliseconds;
            envelope["captureUnixTimeMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            envelope["threadId"] = Thread.CurrentThread.ManagedThreadId;
            envelope["success"] = success;
            TrackingOriginTelemetry.AppendTo(envelope);
            LargeSpaceTelemetry.AppendTo(envelope);
            if (data != null)
            {
                envelope["data"] = data;
            }

            if (!string.IsNullOrEmpty(error))
            {
                envelope["error"] = error;
            }

            return envelope.ToJson();
        }

        private void Enqueue(string fileName, string line)
        {
            _fileWriter?.Enqueue(fileName, line);
        }

        private bool IsFileWriterAccepting =>
            _fileWriter != null && _fileWriter.IsAcceptingLines;

        private static double GetIntervalSeconds(int sampleHz)
        {
            return sampleHz > 0 ? 1.0 / sampleHz : 0.0;
        }

        private void LogEnterpriseSampleRateIfNeeded()
        {
            float intervalSeconds = Mathf.Max(0.1f, sampleRateLogIntervalSeconds);
            long nowTicks = Stopwatch.GetTimestamp();
            long elapsedTicks = nowTicks - _lastEnterpriseRateLogTicks;
            if (elapsedTicks < intervalSeconds * Stopwatch.Frequency)
            {
                return;
            }

            double elapsedSeconds = elapsedTicks / (double)Stopwatch.Frequency;
            long headCount = Interlocked.Exchange(ref _enterpriseHeadSamplesInWindow, 0);
            long controllerCount = Interlocked.Exchange(ref _enterpriseControllerSamplesInWindow, 0);
            _lastEnterpriseRateLogTicks = nowTicks;

            double headHz = elapsedSeconds > 0 ? headCount / elapsedSeconds : 0;
            double controllerHz = elapsedSeconds > 0 ? controllerCount / elapsedSeconds : 0;
            string rateMessage =
                $"Enterprise sample rate: head={headHz:F1}Hz controller={controllerHz:F1}Hz target={EnterpriseSampleHz}Hz";
            if (outputSampleRateToLogWindow)
            {
                LogWindow.Info(rateMessage);
            }

            Debug.Log($"{Tag} {rateMessage}");
        }

        private static string GetPoseStr(Vector3 position, Quaternion rotation)
        {
            return position.x.ToString("R") + "," + position.y.ToString("R") + "," + position.z.ToString("R") + "," +
                   rotation.x.ToString("R") + "," + rotation.y.ToString("R") + "," + rotation.z.ToString("R") + "," +
                   rotation.w.ToString("R");
        }

        private static string GetPoseStr(PxrVector3f position, PxrVector4f rotation)
        {
            return position.x.ToString("R") + "," + position.y.ToString("R") + "," + position.z.ToString("R") + "," +
                   rotation.x.ToString("R") + "," + rotation.y.ToString("R") + "," + rotation.z.ToString("R") + "," +
                   rotation.w.ToString("R");
        }

        private int GetUnityPoseStagnationThreshold()
        {
            return Math.Max(1, unitySampleHz);
        }

        private int GetEnterprisePoseStagnationThreshold()
        {
            return Math.Max(1, EnterpriseSampleHz);
        }

        private static bool IsSamePose(Vector3 oldPosition, Quaternion oldRotation, Vector3 newPosition,
            Quaternion newRotation)
        {
            return Approximately(oldPosition.x, newPosition.x) &&
                   Approximately(oldPosition.y, newPosition.y) &&
                   Approximately(oldPosition.z, newPosition.z) &&
                   Approximately(oldRotation.x, newRotation.x) &&
                   Approximately(oldRotation.y, newRotation.y) &&
                   Approximately(oldRotation.z, newRotation.z) &&
                   Approximately(oldRotation.w, newRotation.w);
        }

        private static bool Approximately(float left, float right)
        {
            return Math.Abs(left - right) <= 0.000001f;
        }

        private static string GetPosePositionStr(Vector3 position)
        {
            return position.x.ToString("R") + "," + position.y.ToString("R") + "," + position.z.ToString("R");
        }

        private static string GetPoseRotationStr(Quaternion rotation)
        {
            return rotation.x.ToString("R") + "," + rotation.y.ToString("R") + "," + rotation.z.ToString("R") + "," +
                   rotation.w.ToString("R");
        }

        public struct NativePoseKinematics
        {
            public bool Available;
            public long PoseTimeStampNs;
            public Vector3 AngularVelocity;
            public Vector3 LinearVelocity;
            public Vector3 AngularAcceleration;
            public Vector3 LinearAcceleration;
        }

        public struct DoubleVector3
        {
            public double X;
            public double Y;
            public double Z;

            public DoubleVector3(double x, double y, double z)
            {
                X = x;
                Y = y;
                Z = z;
            }
        }

        public struct TobControllerImu
        {
            public bool Available;
            public long TimestampNs;
            public DoubleVector3 LinearVelocityNative;
            public DoubleVector3 LinearAccelerationNative;
            public DoubleVector3 AngularVelocityNative;
            public DoubleVector3 AngularAccelerationNative;
        }

        public struct OfficialHeadTelemetry
        {
            public bool PoseAvailable;
            public string Pose;
            public long PoseTimestampNs;
            public int PoseConfidence;
            public int PoseError;
            public bool ImuAvailable;
            public long ImuTimestampNs;
            public DoubleVector3 LinearVelocityNative;
            public DoubleVector3 LinearAccelerationNative;
            public DoubleVector3 AngularVelocityNative;
            public DoubleVector3 AngularAccelerationNative;
        }

        public struct EnterpriseHeadTcpPose
        {
            public bool HasPose;
            public string Pose;
            public int Status;
            public long TimeStampNs;
            public int PoseError;
            public string PoseSource;
            public NativePosePair NativePosePair;
            public NativePoseKinematics NativeKinematics;
            public OfficialHeadTelemetry OfficialTelemetry;
        }

        public struct EnterpriseControllerTcpPose
        {
            public bool HasPose;
            public string Pose;
            public int Status;
            public long TimeStampNs;
            public int Type;
            public int PoseError;
            public NativePosePair NativePosePair;
            public NativePoseKinematics NativeKinematics;
            public TobControllerImu TobControllerImu;
        }

        public struct NativePoseSample
        {
            public bool Available;
            public string Pose;
            public long TimeStampNs;
            public int Status;
        }

        public struct NativePosePair
        {
            public NativePoseSample Local;
            public NativePoseSample Global;
        }

        private struct ControllerInputSnapshot
        {
            public bool Axis2DSuccess;
            public bool AxisClickSuccess;
            public bool GripSuccess;
            public bool TriggerSuccess;
            public bool PrimaryButtonSuccess;
            public bool SecondaryButtonSuccess;
            public bool MenuButtonSuccess;

            public string GetFeatureMask()
            {
                return "axis2D=" + Axis2DSuccess +
                       ",axisClick=" + AxisClickSuccess +
                       ",grip=" + GripSuccess +
                       ",trigger=" + TriggerSuccess +
                       ",primary=" + PrimaryButtonSuccess +
                       ",secondary=" + SecondaryButtonSuccess +
                       ",menu=" + MenuButtonSuccess;
            }
        }

        private class ControllerLogState
        {
            public readonly string Source;
            public readonly string Side;
            public bool Initialized;
            public bool HasPose;
            public bool IsValid;
            public string DeviceName;
            public string FeatureMask;
            public int Confidence;
            public int PoseError;
            public long Timestamp;
            public Vector3 Position;
            public Quaternion Rotation;
            public int SamePoseCount;
            public int SameTimestampCount;
            public long LastPoseStagnationLogMs;
            public long LastTimestampStagnationLogMs;

            public ControllerLogState(string source, string side)
            {
                Source = source;
                Side = side;
            }
        }

    }
}
