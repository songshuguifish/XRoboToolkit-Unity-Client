using Robot;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class IpInputDialog : MonoBehaviour
{
    public TMP_InputField TmpInput;
    public TextMeshProUGUI Remind;
    public Button ConnectBtn;
    public Button CloseBtn;
    public TcpHandler TcpHandler;

    public UIOperate uiRobot;

    private bool _connecting = false;

    private void Awake()
    {
        ConnectBtn.onClick.AddListener(OnConnectBtn);
        CloseBtn.onClick.AddListener(OnCloseBtn);
    }

    private void OnEnable()
    {
        Remind.text = "";
        ConnectBtn.gameObject.SetActive(true);
        _connecting = false;

        string currentAddress = TcpHandler != null &&
                                (TcpHandler.State == SocketState.CONNECTING ||
                                 TcpHandler.State == SocketState.WORKING)
            ? TcpHandler.GetTargetIP
            : EnterpriseConnectionSettings.LastSuccessfulHostIp;

        if (string.IsNullOrEmpty(currentAddress))
            currentAddress = TcpHandler.GetTargetIP;

        if (!EnterpriseConnectionSettings.TryNormalizeIpv4(currentAddress, out string normalized) ||
            !EnterpriseConnectionSettings.IsConnectionAddressAllowed(normalized))
        {
            normalized = EnterpriseConnectionSettings.UsbHostIp;
        }

        TmpInput.SetTextWithoutNotify(normalized);
    }

    private void OnCloseBtn()
    {
        gameObject.SetActive(false);
    }

    public void OnConnectBtn()
    {
        if (!EnterpriseConnectionSettings.TryNormalizeIpv4(TmpInput.text, out string ip))
        {
            SetRemind(LogType.Error, "Enter a valid IPv4 address.");
            return;
        }

        if (!EnterpriseConnectionSettings.IsConnectionAddressAllowed(ip))
        {
            SetRemind(LogType.Error,
                "Use the PICO USB host address (192.168.245.x). Wi-Fi is disabled.");
            return;
        }

        if (uiRobot == null || TcpHandler == null)
        {
            SetRemind(LogType.Error, "Connection components are not available.");
            return;
        }

        SetRemind(LogType.Log, "Connecting...");
        _connecting = true;
        TmpInput.SetTextWithoutNotify(ip);
        uiRobot.TcpConnect(ip);
        ConnectBtn.gameObject.SetActive(false);
    }

    private void Update()
    {
        if (TcpHandler != null && _connecting)
        {
            if (TcpHandler.State == SocketState.WORKING)
            {
                uiRobot.ConnectSuccess();
                gameObject.SetActive(false);
            }
            else if (TcpHandler.State == SocketState.CONNECT_ERROR || TcpHandler.State == SocketState.CLOSE)
            {
                if (!string.IsNullOrEmpty(TcpHandler.ConnectErrorInfo))
                {
                    SetRemind(LogType.Error, TcpHandler.ConnectErrorInfo);
                }
                else
                {
                    SetRemind(LogType.Error, "Connect fail!");
                }

                ConnectBtn.gameObject.SetActive(true);
                _connecting = false;
            }
        }
    }

    public void SetRemind(LogType type, string content)
    {
        if (type == LogType.Error)
        {
            Remind.color = Color.red;
        }
        else
        {
            Remind.color = Color.white;
        }

        Remind.text = content;
    }
}
