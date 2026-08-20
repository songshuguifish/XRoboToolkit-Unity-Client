package com.xrobotoolkit.enterprise;

import android.os.Bundle;
import android.os.Parcelable;
import android.util.Log;

import org.json.JSONArray;
import org.json.JSONObject;

import java.lang.reflect.Array;
import java.lang.reflect.Field;
import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;
import java.util.List;

public final class EnterprisePoseBridge {
    private static final String TAG = "EnterprisePoseBridge";
    private static final String TOB_SERVICE_UTILS = "com.pvr.tobservice.ToBServiceHelper";
    private static final String POSE_CLASS = "com.pvr.tobservice.model.Pose";
    private static final String VALUE_TAG = "value";
    private static final String RESULT_CODE_TAG = "key_result_code";
    private static final String METHOD_GET_HEAD_POSE = "get_head_pose";
    private static final String METHOD_GET_CONTROLLER_POSE = "get_controller_pose";
    private static final long SLOW_CALL_THRESHOLD_NS = 15L * 1000L * 1000L;
    private static final long PROFILE_LOG_INTERVAL = 1000L;
    private static final int CONTROLLER_IMU_COUNT = 2;
    private static final int CONTROLLER_IMU_PACKED_FIELDS = 14;
    // Reused bridge storage: [count, (valid,timestamp,vx,vy,vz,ax,ay,az,wx,wy,wz,w_ax,w_ay,w_az) * 2].
    // Unity copies primitive Java arrays into managed memory before the JNI local frame is
    // popped, so returning this synchronized scratch array does not expose mutable state.
    private static final double[] controllerImuPacked =
        new double[1 + CONTROLLER_IMU_COUNT * CONTROLLER_IMU_PACKED_FIELDS];

    private static long headProfileCount;
    private static long controllerProfileCount;
    private static long controllerImuProfileCount;
    private static Class<?> cachedUtilsClass;
    private static Method cachedGetInstanceMethod;
    private static Class<?> cachedGetServiceBinderClass;
    private static Method cachedGetServiceBinderMethod;
    private static Class<?> cachedCommonMessageClass;
    private static Method cachedCommonMessageMethod;
    private static Class<?> cachedPoseClass;
    private static ClassLoader cachedPoseClassLoader;
    private static Class<?> cachedHeadDirectClass;
    private static Method cachedHeadDirectMethod;
    private static boolean cachedHeadDirectUnavailable;
    private static Class<?> cachedControllerDirectClass;
    private static Method cachedControllerDirectMethod;
    private static boolean cachedControllerDirectUnavailable;
    private static Class<?> cachedControllerImuDirectClass;
    private static Method cachedControllerImuDirectMethod;
    private static boolean cachedControllerImuDirectUnavailable;
    private static Class<?> cachedPoseFieldClass;
    private static Field timestampField;
    private static Field xField;
    private static Field yField;
    private static Field zField;
    private static Field rwField;
    private static Field rxField;
    private static Field ryField;
    private static Field rzField;
    private static Field typeField;
    private static Field confidenceField;
    private static Field poseErrorField;
    private static Class<?> cachedImuFieldClass;
    private static Field imuTimestampField;
    private static Field vxField;
    private static Field vyField;
    private static Field vzField;
    private static Field axField;
    private static Field ayField;
    private static Field azField;
    private static Field wxField;
    private static Field wyField;
    private static Field wzField;
    private static Field waxField;
    private static Field wayField;
    private static Field wazField;

    private EnterprisePoseBridge() {
    }

    public static String getHeadPoseJson(long predictTime) {
        JSONObject result = new JSONObject();
        long callStartNs = System.nanoTime();
        long binderNs = 0L;
        long diagnosticsNs = 0L;
        long directNs = 0L;
        long commonMessageNs = 0L;
        long poseJsonNs = 0L;
        long stringifyNs = 0L;
        String path = "none";
        try {
            long stepStartNs = System.nanoTime();
            Object binder = getServiceBinder();
            binderNs = System.nanoTime() - stepStartNs;

            if (binder == null) {
                stepStartNs = System.nanoTime();
                putDiagnostics(result, binder);
                diagnosticsNs = System.nanoTime() - stepStartNs;

                stepStartNs = System.nanoTime();
                String json = failure(result, "binder=null");
                stringifyNs = System.nanoTime() - stepStartNs;
                logProfile("head", path, callStartNs, binderNs, diagnosticsNs, directNs, commonMessageNs,
                    poseJsonNs, stringifyNs, false);
                return json;
            }

            StringBuilder errors = new StringBuilder();
            stepStartNs = System.nanoTime();
            Object pose = invokeDirectLongMethod(binder, "getHeadPose", predictTime, errors);
            directNs = System.nanoTime() - stepStartNs;
            if (pose != null) {
                path = "direct";
                result.put("success", true);
                result.put("path", "direct");

                stepStartNs = System.nanoTime();
                result.put("pose", poseToJson(pose));
                poseJsonNs = System.nanoTime() - stepStartNs;

                stepStartNs = System.nanoTime();
                String json = result.toString();
                stringifyNs = System.nanoTime() - stepStartNs;
                logProfile("head", path, callStartNs, binderNs, diagnosticsNs, directNs, commonMessageNs,
                    poseJsonNs, stringifyNs, true);
                return json;
            }

            stepStartNs = System.nanoTime();
            pose = invokeCommonMessage(binder, METHOD_GET_HEAD_POSE, predictTime, false, errors);
            commonMessageNs = System.nanoTime() - stepStartNs;
            if (pose != null) {
                path = "pbsCommonMessageLocked";
                result.put("success", true);
                result.put("path", "pbsCommonMessageLocked");

                stepStartNs = System.nanoTime();
                result.put("pose", poseToJson(pose));
                poseJsonNs = System.nanoTime() - stepStartNs;

                stepStartNs = System.nanoTime();
                String json = result.toString();
                stringifyNs = System.nanoTime() - stepStartNs;
                logProfile("head", path, callStartNs, binderNs, diagnosticsNs, directNs, commonMessageNs,
                    poseJsonNs, stringifyNs, true);
                return json;
            }

            stepStartNs = System.nanoTime();
            putDiagnostics(result, binder);
            diagnosticsNs = System.nanoTime() - stepStartNs;
            result.put("errors", errors.toString());
            stepStartNs = System.nanoTime();
            String json = failure(result, "head pose unavailable");
            stringifyNs = System.nanoTime() - stepStartNs;
            logProfile("head", path, callStartNs, binderNs, diagnosticsNs, directNs, commonMessageNs,
                poseJsonNs, stringifyNs, false);
            return json;
        } catch (Throwable t) {
            String json = exceptionJson("getHeadPoseJson", t);
            logProfile("head", path, callStartNs, binderNs, diagnosticsNs, directNs, commonMessageNs,
                poseJsonNs, stringifyNs, false);
            return json;
        }
    }

    public static String getControllerPoseJson(long predictTime) {
        JSONObject result = new JSONObject();
        long callStartNs = System.nanoTime();
        long binderNs = 0L;
        long diagnosticsNs = 0L;
        long directNs = 0L;
        long commonMessageNs = 0L;
        long poseJsonNs = 0L;
        long stringifyNs = 0L;
        String path = "none";
        try {
            long stepStartNs = System.nanoTime();
            Object binder = getServiceBinder();
            binderNs = System.nanoTime() - stepStartNs;

            if (binder == null) {
                stepStartNs = System.nanoTime();
                putDiagnostics(result, binder);
                diagnosticsNs = System.nanoTime() - stepStartNs;

                stepStartNs = System.nanoTime();
                String json = failure(result, "binder=null");
                stringifyNs = System.nanoTime() - stepStartNs;
                logProfile("controller", path, callStartNs, binderNs, diagnosticsNs, directNs, commonMessageNs,
                    poseJsonNs, stringifyNs, false);
                return json;
            }

            StringBuilder errors = new StringBuilder();
            stepStartNs = System.nanoTime();
            Object poses = invokeDirectLongMethod(binder, "getControllerPose", predictTime, errors);
            directNs = System.nanoTime() - stepStartNs;

            stepStartNs = System.nanoTime();
            JSONArray poseArray = posesToJsonArray(poses);
            poseJsonNs = System.nanoTime() - stepStartNs;
            if (poseArray != null) {
                path = "direct";
                result.put("success", true);
                result.put("path", "direct");
                result.put("poses", poseArray);

                stepStartNs = System.nanoTime();
                String json = result.toString();
                stringifyNs = System.nanoTime() - stepStartNs;
                logProfile("controller", path, callStartNs, binderNs, diagnosticsNs, directNs, commonMessageNs,
                    poseJsonNs, stringifyNs, true);
                return json;
            }

            stepStartNs = System.nanoTime();
            poses = invokeCommonMessage(binder, METHOD_GET_CONTROLLER_POSE, predictTime, true, errors);
            commonMessageNs = System.nanoTime() - stepStartNs;

            stepStartNs = System.nanoTime();
            poseArray = posesToJsonArray(poses);
            poseJsonNs += System.nanoTime() - stepStartNs;
            if (poseArray != null) {
                path = "pbsCommonMessageLocked";
                result.put("success", true);
                result.put("path", "pbsCommonMessageLocked");
                result.put("poses", poseArray);

                stepStartNs = System.nanoTime();
                String json = result.toString();
                stringifyNs = System.nanoTime() - stepStartNs;
                logProfile("controller", path, callStartNs, binderNs, diagnosticsNs, directNs, commonMessageNs,
                    poseJsonNs, stringifyNs, true);
                return json;
            }

            stepStartNs = System.nanoTime();
            putDiagnostics(result, binder);
            diagnosticsNs = System.nanoTime() - stepStartNs;
            result.put("errors", errors.toString());
            stepStartNs = System.nanoTime();
            String json = failure(result, "controller pose unavailable");
            stringifyNs = System.nanoTime() - stepStartNs;
            logProfile("controller", path, callStartNs, binderNs, diagnosticsNs, directNs, commonMessageNs,
                poseJsonNs, stringifyNs, false);
            return json;
        } catch (Throwable t) {
            String json = exceptionJson("getControllerPoseJson", t);
            logProfile("controller", path, callStartNs, binderNs, diagnosticsNs, directNs, commonMessageNs,
                poseJsonNs, stringifyNs, false);
            return json;
        }
    }

    public static String getControllerImuJson(long predictTime) {
        JSONObject result = new JSONObject();
        long callStartNs = System.nanoTime();
        long binderNs = 0L;
        long directNs = 0L;
        long jsonNs = 0L;
        long stringifyNs = 0L;
        try {
            long stepStartNs = System.nanoTime();
            Object binder = getServiceBinder();
            binderNs = System.nanoTime() - stepStartNs;
            if (binder == null) {
                String json = failure(result, "binder=null");
                logProfile("controller_imu", "none", callStartNs, binderNs, 0L, directNs,
                    0L, jsonNs, stringifyNs, false);
                return json;
            }

            StringBuilder errors = new StringBuilder();
            stepStartNs = System.nanoTime();
            Object imus = invokeDirectLongMethod(binder, "getControllerIMUData", predictTime, errors);
            directNs = System.nanoTime() - stepStartNs;

            stepStartNs = System.nanoTime();
            JSONArray imuArray = imusToJsonArray(imus);
            jsonNs = System.nanoTime() - stepStartNs;
            if (imuArray != null) {
                result.put("success", true);
                result.put("path", "direct");
                result.put("imus", imuArray);
                stepStartNs = System.nanoTime();
                String json = result.toString();
                stringifyNs = System.nanoTime() - stepStartNs;
                logProfile("controller_imu", "direct", callStartNs, binderNs, 0L, directNs,
                    0L, jsonNs, stringifyNs, true);
                return json;
            }

            putDiagnostics(result, binder);
            result.put("errors", errors.toString());
            String json = failure(result, "controller IMU unavailable");
            logProfile("controller_imu", "none", callStartNs, binderNs, 0L, directNs,
                0L, jsonNs, stringifyNs, false);
            return json;
        } catch (Throwable t) {
            String json = exceptionJson("getControllerImuJson", t);
            logProfile("controller_imu", "none", callStartNs, binderNs, 0L, directNs,
                0L, jsonNs, stringifyNs, false);
            return json;
        }
    }

    /**
     * Allocation-light production path for getControllerIMUData. The former JSON bridge built
     * dozens of JSONObject/LitJson objects on every 250 Hz poll, which made Unity IL2CPP's
     * native managed heap expand continuously. Timestamps remain exact because current PICO
     * boot-clock nanoseconds are well below double's 2^53 exact-integer limit.
     */
    public static synchronized double[] getControllerImuPacked(long predictTime) {
        try {
            Object binder = getServiceBinder();
            if (binder == null) {
                return null;
            }

            Object imus = invokeDirectLongMethod(
                binder, "getControllerIMUData", predictTime, new StringBuilder());
            int count;
            if (imus instanceof List) {
                count = Math.min(CONTROLLER_IMU_COUNT, ((List<?>) imus).size());
            } else if (imus != null && imus.getClass().isArray()) {
                count = Math.min(CONTROLLER_IMU_COUNT, Array.getLength(imus));
            } else {
                return null;
            }

            for (int i = 0; i < controllerImuPacked.length; i++) {
                controllerImuPacked[i] = 0.0;
            }
            controllerImuPacked[0] = count;
            for (int i = 0; i < count; i++) {
                Object imu = imus instanceof List
                    ? ((List<?>) imus).get(i)
                    : Array.get(imus, i);
                if (imu == null) {
                    continue;
                }

                ensureImuFields(imu.getClass());
                int base = 1 + i * CONTROLLER_IMU_PACKED_FIELDS;
                controllerImuPacked[base] = 1.0;
                controllerImuPacked[base + 1] = imuTimestampField.getLong(imu);
                controllerImuPacked[base + 2] = vxField.getDouble(imu);
                controllerImuPacked[base + 3] = vyField.getDouble(imu);
                controllerImuPacked[base + 4] = vzField.getDouble(imu);
                controllerImuPacked[base + 5] = axField.getDouble(imu);
                controllerImuPacked[base + 6] = ayField.getDouble(imu);
                controllerImuPacked[base + 7] = azField.getDouble(imu);
                controllerImuPacked[base + 8] = wxField.getDouble(imu);
                controllerImuPacked[base + 9] = wyField.getDouble(imu);
                controllerImuPacked[base + 10] = wzField.getDouble(imu);
                controllerImuPacked[base + 11] = waxField.getDouble(imu);
                controllerImuPacked[base + 12] = wayField.getDouble(imu);
                controllerImuPacked[base + 13] = wazField.getDouble(imu);
            }
            return controllerImuPacked;
        } catch (Throwable t) {
            long count = nextProfileCount("controller_imu");
            if (count % PROFILE_LOG_INTERVAL == 0L) {
                Log.w(TAG, "packed controller IMU failed: " + describeThrowable(t));
            }
            return null;
        }
    }

    public static String getDiagnosticsJson() {
        JSONObject result = new JSONObject();
        try {
            Object binder = getServiceBinder();
            putDiagnostics(result, binder);
            result.put("success", binder != null);
        } catch (Throwable t) {
            putException(result, "getDiagnosticsJson", t);
        }
        return result.toString();
    }

    private static Object getServiceBinder() throws Exception {
        Class<?> utilsClass = getTobServiceUtilsClass();
        Object utils = getGetInstanceMethod(utilsClass).invoke(null);
        if (utils == null) {
            return null;
        }

        Method getServiceBinder = getGetServiceBinderMethod(utils.getClass());
        return getServiceBinder.invoke(utils);
    }

    private static Object invokeDirectLongMethod(Object target, String methodName, long arg, StringBuilder errors) {
        try {
            Method method = getDirectLongMethod(target.getClass(), methodName);
            if (method == null) {
                appendError(errors, methodName, "method unavailable");
                return null;
            }

            return method.invoke(target, arg);
        } catch (Throwable t) {
            appendError(errors, methodName, t);
            return null;
        }
    }

    private static Object invokeCommonMessage(Object binder, String methodName, long predictTime,
                                             boolean arrayResult, StringBuilder errors) {
        try {
            Method commonMessage = getCommonMessageMethod(binder.getClass());
            Bundle args = new Bundle();
            args.putLong(VALUE_TAG, predictTime);
            Bundle result = (Bundle) commonMessage.invoke(binder, methodName, args);
            if (result == null) {
                appendError(errors, "pbsCommonMessageLocked", "result bundle is null");
                return null;
            }

            try {
                result.setClassLoader(getPoseClassLoader());
            } catch (Throwable ignored) {
                result.setClassLoader(EnterprisePoseBridge.class.getClassLoader());
            }

            if (arrayResult) {
                return result.getParcelableArrayList(RESULT_CODE_TAG);
            }

            Parcelable pose = result.getParcelable(RESULT_CODE_TAG);
            return pose;
        } catch (Throwable t) {
            appendError(errors, "pbsCommonMessageLocked(" + methodName + ")", t);
            return null;
        }
    }

    private static Class<?> getTobServiceUtilsClass() throws ClassNotFoundException {
        if (cachedUtilsClass == null) {
            cachedUtilsClass = Class.forName(TOB_SERVICE_UTILS);
        }
        return cachedUtilsClass;
    }

    private static Method getGetInstanceMethod(Class<?> utilsClass) throws NoSuchMethodException {
        if (cachedGetInstanceMethod == null) {
            cachedGetInstanceMethod = utilsClass.getMethod("getInstance");
            cachedGetInstanceMethod.setAccessible(true);
        }
        return cachedGetInstanceMethod;
    }

    private static Method getGetServiceBinderMethod(Class<?> utilsInstanceClass) throws NoSuchMethodException {
        if (cachedGetServiceBinderMethod == null || cachedGetServiceBinderClass != utilsInstanceClass) {
            cachedGetServiceBinderMethod = findMethod(utilsInstanceClass, "getServiceBinder");
            cachedGetServiceBinderClass = utilsInstanceClass;
        }
        return cachedGetServiceBinderMethod;
    }

    private static Method getCommonMessageMethod(Class<?> binderClass) throws NoSuchMethodException {
        if (cachedCommonMessageMethod == null || cachedCommonMessageClass != binderClass) {
            cachedCommonMessageMethod = findMethod(binderClass, "pbsCommonMessageLocked", String.class, Bundle.class);
            cachedCommonMessageClass = binderClass;
        }
        return cachedCommonMessageMethod;
    }

    private static ClassLoader getPoseClassLoader() throws ClassNotFoundException {
        if (cachedPoseClassLoader == null) {
            if (cachedPoseClass == null) {
                cachedPoseClass = Class.forName(POSE_CLASS);
            }
            cachedPoseClassLoader = cachedPoseClass.getClassLoader();
        }
        return cachedPoseClassLoader;
    }

    private static Method getDirectLongMethod(Class<?> targetClass, String methodName) {
        if ("getHeadPose".equals(methodName)) {
            if (cachedHeadDirectClass == targetClass) {
                return cachedHeadDirectUnavailable ? null : cachedHeadDirectMethod;
            }

            cachedHeadDirectClass = targetClass;
            try {
                cachedHeadDirectMethod = findMethod(targetClass, methodName, long.class);
                cachedHeadDirectUnavailable = false;
                return cachedHeadDirectMethod;
            } catch (Throwable ignored) {
                cachedHeadDirectMethod = null;
                cachedHeadDirectUnavailable = true;
                return null;
            }
        }

        if ("getControllerPose".equals(methodName)) {
            if (cachedControllerDirectClass == targetClass) {
                return cachedControllerDirectUnavailable ? null : cachedControllerDirectMethod;
            }

            cachedControllerDirectClass = targetClass;
            try {
                cachedControllerDirectMethod = findMethod(targetClass, methodName, long.class);
                cachedControllerDirectUnavailable = false;
                return cachedControllerDirectMethod;
            } catch (Throwable ignored) {
                cachedControllerDirectMethod = null;
                cachedControllerDirectUnavailable = true;
                return null;
            }
        }

        if ("getControllerIMUData".equals(methodName)) {
            if (cachedControllerImuDirectClass == targetClass) {
                return cachedControllerImuDirectUnavailable ? null : cachedControllerImuDirectMethod;
            }

            cachedControllerImuDirectClass = targetClass;
            try {
                cachedControllerImuDirectMethod = findMethod(targetClass, methodName, long.class);
                cachedControllerImuDirectUnavailable = false;
                return cachedControllerImuDirectMethod;
            } catch (Throwable ignored) {
                cachedControllerImuDirectMethod = null;
                cachedControllerImuDirectUnavailable = true;
                return null;
            }
        }

        try {
            return findMethod(targetClass, methodName, long.class);
        } catch (Throwable ignored) {
            return null;
        }
    }

    private static Method findMethod(Class<?> cls, String name, Class<?>... parameterTypes) throws NoSuchMethodException {
        try {
            Method method = cls.getMethod(name, parameterTypes);
            method.setAccessible(true);
            return method;
        } catch (NoSuchMethodException ignored) {
            Class<?> current = cls;
            while (current != null) {
                try {
                    Method method = current.getDeclaredMethod(name, parameterTypes);
                    method.setAccessible(true);
                    return method;
                } catch (NoSuchMethodException ignoredDeclared) {
                    current = current.getSuperclass();
                }
            }
            throw new NoSuchMethodException(cls.getName() + "." + name);
        }
    }

    private static JSONObject poseToJson(Object pose) throws Exception {
        ensurePoseFields(pose.getClass());
        JSONObject json = new JSONObject();
        json.put("timestamp", timestampField.getLong(pose));
        json.put("x", xField.getDouble(pose));
        json.put("y", yField.getDouble(pose));
        json.put("z", zField.getDouble(pose));
        json.put("rw", rwField.getDouble(pose));
        json.put("rx", rxField.getDouble(pose));
        json.put("ry", ryField.getDouble(pose));
        json.put("rz", rzField.getDouble(pose));
        json.put("type", typeField.getInt(pose));
        json.put("confidence", confidenceField.getInt(pose));
        json.put("poseError", poseErrorField.getInt(pose));
        return json;
    }

    private static JSONArray posesToJsonArray(Object poses) throws Exception {
        if (poses == null) {
            return null;
        }

        JSONArray array = new JSONArray();
        if (poses instanceof List) {
            List<?> list = (List<?>) poses;
            for (int i = 0; i < list.size(); i++) {
                Object pose = list.get(i);
                array.put(pose == null ? JSONObject.NULL : poseToJson(pose));
            }
            return array;
        }

        if (poses.getClass().isArray()) {
            int length = Array.getLength(poses);
            for (int i = 0; i < length; i++) {
                Object pose = Array.get(poses, i);
                array.put(pose == null ? JSONObject.NULL : poseToJson(pose));
            }
            return array;
        }

        return null;
    }

    private static JSONObject imuToJson(Object imu) throws Exception {
        ensureImuFields(imu.getClass());
        JSONObject json = new JSONObject();
        json.put("timestamp", imuTimestampField.getLong(imu));
        json.put("vx", vxField.getDouble(imu));
        json.put("vy", vyField.getDouble(imu));
        json.put("vz", vzField.getDouble(imu));
        json.put("ax", axField.getDouble(imu));
        json.put("ay", ayField.getDouble(imu));
        json.put("az", azField.getDouble(imu));
        json.put("wx", wxField.getDouble(imu));
        json.put("wy", wyField.getDouble(imu));
        json.put("wz", wzField.getDouble(imu));
        json.put("w_ax", waxField.getDouble(imu));
        json.put("w_ay", wayField.getDouble(imu));
        json.put("w_az", wazField.getDouble(imu));
        return json;
    }

    private static JSONArray imusToJsonArray(Object imus) throws Exception {
        if (imus == null) {
            return null;
        }

        JSONArray array = new JSONArray();
        if (imus instanceof List) {
            List<?> list = (List<?>) imus;
            for (int i = 0; i < list.size(); i++) {
                Object imu = list.get(i);
                array.put(imu == null ? JSONObject.NULL : imuToJson(imu));
            }
            return array;
        }

        if (imus.getClass().isArray()) {
            int length = Array.getLength(imus);
            for (int i = 0; i < length; i++) {
                Object imu = Array.get(imus, i);
                array.put(imu == null ? JSONObject.NULL : imuToJson(imu));
            }
            return array;
        }
        return null;
    }

    private static void ensurePoseFields(Class<?> poseClass) throws NoSuchFieldException {
        if (cachedPoseFieldClass == poseClass) {
            return;
        }

        cachedPoseFieldClass = poseClass;
        timestampField = getField(poseClass, "timestamp");
        xField = getField(poseClass, "x");
        yField = getField(poseClass, "y");
        zField = getField(poseClass, "z");
        rwField = getField(poseClass, "rw");
        rxField = getField(poseClass, "rx");
        ryField = getField(poseClass, "ry");
        rzField = getField(poseClass, "rz");
        typeField = getField(poseClass, "type");
        confidenceField = getField(poseClass, "confidence");
        poseErrorField = getField(poseClass, "poseError");
    }

    private static void ensureImuFields(Class<?> imuClass) throws NoSuchFieldException {
        if (cachedImuFieldClass == imuClass) {
            return;
        }

        cachedImuFieldClass = imuClass;
        imuTimestampField = getField(imuClass, "timestamp");
        vxField = getField(imuClass, "vx");
        vyField = getField(imuClass, "vy");
        vzField = getField(imuClass, "vz");
        axField = getField(imuClass, "ax");
        ayField = getField(imuClass, "ay");
        azField = getField(imuClass, "az");
        wxField = getField(imuClass, "wx");
        wyField = getField(imuClass, "wy");
        wzField = getField(imuClass, "wz");
        waxField = getField(imuClass, "w_ax");
        wayField = getField(imuClass, "w_ay");
        wazField = getField(imuClass, "w_az");
    }

    private static Field getField(Class<?> targetClass, String fieldName) throws NoSuchFieldException {
        Class<?> current = targetClass;
        while (current != null) {
            try {
                Field field = current.getDeclaredField(fieldName);
                field.setAccessible(true);
                return field;
            } catch (NoSuchFieldException ignored) {
                current = current.getSuperclass();
            }
        }
        throw new NoSuchFieldException(targetClass.getName() + "." + fieldName);
    }

    private static void putDiagnostics(JSONObject result, Object binder) {
        try {
            if (binder == null) {
                result.put("binderClass", JSONObject.NULL);
                return;
            }

            Class<?> binderClass = binder.getClass();
            result.put("binderClass", binderClass.getName());
            result.put("methods", getMethodSummary(binderClass));
        } catch (Throwable t) {
            putException(result, "putDiagnostics", t);
        }
    }

    private static JSONArray getMethodSummary(Class<?> cls) {
        JSONArray methods = new JSONArray();
        Method[] publicMethods = cls.getMethods();
        for (int i = 0; i < publicMethods.length && methods.length() < 80; i++) {
            addUsefulMethod(methods, publicMethods[i]);
        }

        Class<?> current = cls;
        while (current != null && methods.length() < 120) {
            Method[] declaredMethods = current.getDeclaredMethods();
            for (int i = 0; i < declaredMethods.length && methods.length() < 120; i++) {
                addUsefulMethod(methods, declaredMethods[i]);
            }
            current = current.getSuperclass();
        }
        return methods;
    }

    private static void addUsefulMethod(JSONArray methods, Method method) {
        String value = method.toString();
        if (value.contains("Pose") || value.contains("pose") || value.contains("IMU")
            || value.contains("pbsCommonMessageLocked")) {
            methods.put(value);
        }
    }

    private static String failure(JSONObject result, String error) throws Exception {
        result.put("success", false);
        result.put("error", error);
        return result.toString();
    }

    private static void logProfile(String kind, String path, long callStartNs, long binderNs,
                                   long diagnosticsNs, long directNs, long commonMessageNs,
                                   long poseJsonNs, long stringifyNs, boolean success) {
        long totalNs = System.nanoTime() - callStartNs;
        long count = nextProfileCount(kind);
        if (totalNs < SLOW_CALL_THRESHOLD_NS && count % PROFILE_LOG_INTERVAL != 0L) {
            return;
        }

        Log.i(TAG, "profile kind=" + kind
            + " count=" + count
            + " success=" + success
            + " path=" + path
            + " totalMs=" + nsToMs(totalNs)
            + " binderMs=" + nsToMs(binderNs)
            + " diagnosticsMs=" + nsToMs(diagnosticsNs)
            + " directMs=" + nsToMs(directNs)
            + " commonMessageMs=" + nsToMs(commonMessageNs)
            + " poseJsonMs=" + nsToMs(poseJsonNs)
            + " stringifyMs=" + nsToMs(stringifyNs));
    }

    private static synchronized long nextProfileCount(String kind) {
        if ("head".equals(kind)) {
            headProfileCount++;
            return headProfileCount;
        }

        if ("controller_imu".equals(kind)) {
            controllerImuProfileCount++;
            return controllerImuProfileCount;
        }

        controllerProfileCount++;
        return controllerProfileCount;
    }

    private static String nsToMs(long ns) {
        return String.format(java.util.Locale.US, "%.3f", ns / 1000000.0);
    }

    private static String exceptionJson(String context, Throwable throwable) {
        JSONObject result = new JSONObject();
        putException(result, context, throwable);
        return result.toString();
    }

    private static void putException(JSONObject result, String context, Throwable throwable) {
        try {
            result.put("success", false);
            result.put("error", context + ": " + describeThrowable(throwable));
        } catch (Throwable ignored) {
        }
    }

    private static void appendError(StringBuilder builder, String context, Throwable throwable) {
        appendError(builder, context, describeThrowable(throwable));
    }

    private static void appendError(StringBuilder builder, String context, String message) {
        if (builder.length() > 0) {
            builder.append(" | ");
        }
        builder.append(context).append(": ").append(message);
    }

    private static String describeThrowable(Throwable throwable) {
        Throwable actual = throwable;
        if (throwable instanceof InvocationTargetException
            && ((InvocationTargetException) throwable).getTargetException() != null) {
            actual = ((InvocationTargetException) throwable).getTargetException();
        }

        String message = actual.getMessage();
        return actual.getClass().getSimpleName() + (message == null ? "" : ": " + message);
    }
}
