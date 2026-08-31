using System;
using UnityEngine;

namespace Robot
{
    /// <summary>
    /// App-owned access to deployed ToBService extensions that are not part of the public
    /// PICO Unity SDK. These calls used to be patched into PXR_Enterprise itself.
    /// </summary>
    public static class PicoEnterpriseServiceCompat
    {
        private const string TobHelperClass = "com.pvr.tobservice.ToBServiceHelper";

        public static int SetTrackingDataIncludingPredictions(bool enabled, int ext = 0)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (AndroidJavaObject binder = GetServiceBinder())
                {
                    return binder == null
                        ? -1
                        : binder.Call<int>(
                            "pbsSetTrackingDataIncludingPredictions", enabled ? 1 : 0, ext);
                }
            }
            catch (Exception exception)
            {
                Debug.LogError($"SetTrackingDataIncludingPredictions failed: {exception}");
                return -1;
            }
#else
            return 0;
#endif
        }

        public static int GetTrackingDataIncludingPredictions(int ext = 0)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (AndroidJavaObject binder = GetServiceBinder())
                {
                    return binder == null
                        ? -1
                        : binder.Call<int>("pbsGetTrackingDataIncludingPredictions", ext);
                }
            }
            catch (Exception exception)
            {
                Debug.LogError($"GetTrackingDataIncludingPredictions failed: {exception}");
                return -1;
            }
#else
            return 0;
#endif
        }

        public static void EnableUsbTetheringStaticIp()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (AndroidJavaObject binder = GetServiceBinder())
                using (AndroidJavaObject parameters = new AndroidJavaObject("android.os.Bundle"))
                {
                    if (binder == null)
                    {
                        Debug.LogWarning(
                            "ToBService binder is not ready; cannot enable USB static IP.");
                        return;
                    }

                    // The deployed ToBService 4.3.32 uses enum index 104 for the static-IP
                    // switch. Preserve this device-specific extension outside the vendor SDK.
                    parameters.Call("putInt", "system_function", 104);
                    parameters.Call("putInt", "switch", 0);
                    parameters.Call("putInt", "extension_bit", 0);
                    using (AndroidJavaObject ignored = binder.Call<AndroidJavaObject>(
                               "pbsCommonMessageLocked", "switch_system_function", parameters))
                    {
                    }
                }
            }
            catch (Exception exception)
            {
                Debug.LogError($"EnableUsbTetheringStaticIp failed: {exception}");
            }
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private static AndroidJavaObject GetServiceBinder()
        {
            using (AndroidJavaClass helperClass = new AndroidJavaClass(TobHelperClass))
            using (AndroidJavaObject helper =
                   helperClass.CallStatic<AndroidJavaObject>("getInstance"))
            {
                return helper?.Call<AndroidJavaObject>("getServiceBinder");
            }
        }
#endif
    }
}
