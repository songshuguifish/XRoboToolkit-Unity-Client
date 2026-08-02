using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Robot
{
    /// <summary>
    /// Keeps the dedicated data-collection client connected without VR UI input.
    /// Only native PICO Enterprise USB Ethernet endpoints are considered.
    /// </summary>
    public sealed class AutoConnectBootstrap : MonoBehaviour
    {
        private const float RetryCycleIntervalS = 2.0f;
        private const float CandidateIntervalS = 0.25f;
        private const float MonitorIntervalS = 0.25f;

        private static AutoConnectBootstrap _instance;
        private readonly List<string> _candidates = new List<string>();
        private TcpHandler _tcpHandler;
        private UIOperate _uiOperate;
        private int _candidateIndex;
        private float _nextConnectAttempt;
        private string _automaticAttemptAddress;
        private string _lastConnectedAddress;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null)
                return;
            GameObject autoConnectObject = new GameObject("PicoEnterpriseAutoConnect");
            DontDestroyOnLoad(autoConnectObject);
            _instance = autoConnectObject.AddComponent<AutoConnectBootstrap>();
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);
            Application.runInBackground = true;
            TrackingData.SetHeadOn(true);
            TrackingData.SetControllerOn(true);
            TcpHandler.SendTrackingData = true;
        }

        private IEnumerator Start()
        {
            // Let scene Awake methods attach the normal UI and enterprise-service listeners first.
            yield return null;
            while (true)
            {
                FindSceneComponents();
                EnableCollectionStreams();
                EnsureConnected();
                yield return new WaitForSecondsRealtime(MonitorIntervalS);
            }
        }

        private void FindSceneComponents()
        {
            if (_tcpHandler == null)
                _tcpHandler = FindObjectOfType<TcpHandler>(true);
            if (_uiOperate == null)
                _uiOperate = FindObjectOfType<UIOperate>(true);
        }

        private void EnableCollectionStreams()
        {
            TrackingData.SetHeadOn(true);
            TrackingData.SetControllerOn(true);
            TcpHandler.SendTrackingData = true;

            if (_uiOperate == null)
                return;
            if (_uiOperate.HeadTog != null)
                _uiOperate.HeadTog.SetIsOnWithoutNotify(true);
            if (_uiOperate.ControllerTog != null)
                _uiOperate.ControllerTog.SetIsOnWithoutNotify(true);
            if (_uiOperate.SendTog != null)
                _uiOperate.SendTog.SetIsOnWithoutNotify(true);
        }

        private void EnsureConnected()
        {
            if (_tcpHandler == null)
                return;

            string currentTarget = TcpHandler.GetTargetIP;
            if (!string.IsNullOrEmpty(_automaticAttemptAddress) &&
                !string.Equals(currentTarget, _automaticAttemptAddress))
            {
                // A UI/manual connection superseded our current automatic attempt.
                _automaticAttemptAddress = null;
            }

            if (_tcpHandler.State == SocketState.WORKING)
            {
                if (!string.Equals(_lastConnectedAddress, currentTarget))
                {
                    _lastConnectedAddress = currentTarget;
                    EnterpriseConnectionSettings.RememberSuccessfulHost(currentTarget);
                    Debug.Log($"PICO enterprise connection established via " +
                              $"{EnterpriseConnectionSettings.DescribeTransport(currentTarget)}: " +
                              $"{currentTarget}:{TcpHandler.TCP_PORT}");
                    _uiOperate?.ConnectSuccess();
                }

                _automaticAttemptAddress = null;
                _candidateIndex = 0;
                return;
            }

            _lastConnectedAddress = null;
            if (_tcpHandler.State == SocketState.CONNECTING ||
                _tcpHandler.State == SocketState.CREATE)
            {
                return;
            }

            if (!string.IsNullOrEmpty(_automaticAttemptAddress))
            {
                AdvanceCandidate();
                _automaticAttemptAddress = null;
            }

            if (Time.realtimeSinceStartup < _nextConnectAttempt)
                return;

            if (_candidates.Count == 0 || _candidateIndex >= _candidates.Count)
                ReloadCandidates();
            if (_candidates.Count == 0)
                return;

            string address = _candidates[_candidateIndex];
            _automaticAttemptAddress = address;
            string transport = EnterpriseConnectionSettings.DescribeTransport(address);
            Debug.Log($"PICO enterprise auto-connect: trying {transport} " +
                      $"{address}:{TcpHandler.TCP_PORT}");
            _uiOperate?.ShowConnectionAttempt(address);
            _tcpHandler.Connect(address);
        }

        private void ReloadCandidates()
        {
            _candidates.Clear();
            _candidates.AddRange(EnterpriseConnectionSettings.GetAutoConnectCandidates());
            _candidateIndex = 0;
        }

        private void AdvanceCandidate()
        {
            _candidateIndex++;
            if (_candidateIndex >= _candidates.Count)
            {
                ReloadCandidates();
                _nextConnectAttempt = Time.realtimeSinceStartup + RetryCycleIntervalS;
            }
            else
            {
                _nextConnectAttempt = Time.realtimeSinceStartup + CandidateIntervalS;
            }
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused)
                return;

            _automaticAttemptAddress = null;
            _lastConnectedAddress = null;
            ReloadCandidates();
            _nextConnectAttempt = 0.0f;
        }
    }
}
