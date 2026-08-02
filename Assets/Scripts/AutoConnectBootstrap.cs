using System.Collections;
using UnityEngine;

namespace Robot
{
    /// <summary>
    /// Keeps the dedicated data-collection client connected without VR UI input.
    /// </summary>
    public sealed class AutoConnectBootstrap : MonoBehaviour
    {
        private const string TargetIp = "127.0.0.1";
        private const float RetryIntervalS = 2.0f;
        private const float MonitorIntervalS = 0.5f;

        private static AutoConnectBootstrap _instance;
        private TcpHandler _tcpHandler;
        private UIOperate _uiOperate;
        private float _nextConnectAttempt;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null)
                return;
            GameObject autoConnectObject = new GameObject("PicoAutoConnectBootstrap");
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
            // Let scene Awake methods attach the normal UI listeners first.
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
            if (_tcpHandler.State == SocketState.WORKING ||
                _tcpHandler.State == SocketState.CONNECTING)
                return;
            if (Time.realtimeSinceStartup < _nextConnectAttempt)
                return;

            _nextConnectAttempt = Time.realtimeSinceStartup + RetryIntervalS;
            Debug.Log($"PICO auto-connect: connecting to {TargetIp}:{TcpHandler.TCP_PORT}");
            _tcpHandler.Connect(TargetIp);
        }

        private void OnApplicationPause(bool paused)
        {
            if (!paused)
                _nextConnectAttempt = 0.0f;
        }
    }
}
