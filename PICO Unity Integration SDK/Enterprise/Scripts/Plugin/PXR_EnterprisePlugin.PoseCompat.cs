#if (UNITY_ANDROID && !UNITY_EDITOR)
#define PICO_PLATFORM
#endif
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using LitJson;
using Unity.XR.PXR;
using UnityEngine;

namespace Unity.XR.PICO.TOBSupport
{
    /// <summary>
    /// Local compatibility layer retained across the 3.4.0 upgrade:
    /// PoseInfo + EnterprisePoseBridge-first GetHeadPose/GetControllerPose(double)
    /// used by EnterpriseCollectionRecorder.
    /// </summary>
    public partial class PXR_EnterprisePlugin
    {
        private static long GetJavaLongField(AndroidJavaObject javaObject, string fieldName)
        {
            using (AndroidJavaObject javaClass = javaObject.Call<AndroidJavaObject>("getClass"))
            using (AndroidJavaObject field = javaClass.Call<AndroidJavaObject>("getDeclaredField", fieldName))
            {
                field.Call("setAccessible", true);
                return field.Call<long>("getLong", javaObject);
            }
        }

        private static double GetJavaDoubleField(AndroidJavaObject javaObject, string fieldName)
        {
            using (AndroidJavaObject javaClass = javaObject.Call<AndroidJavaObject>("getClass"))
            using (AndroidJavaObject field = javaClass.Call<AndroidJavaObject>("getDeclaredField", fieldName))
            {
                field.Call("setAccessible", true);
                return field.Call<double>("getDouble", javaObject);
            }
        }

        private static int GetJavaIntField(AndroidJavaObject javaObject, string fieldName)
        {
            using (AndroidJavaObject javaClass = javaObject.Call<AndroidJavaObject>("getClass"))
            using (AndroidJavaObject field = javaClass.Call<AndroidJavaObject>("getDeclaredField", fieldName))
            {
                field.Call("setAccessible", true);
                return field.Call<int>("getInt", javaObject);
            }
        }

        private static PoseInfo ConvertJavaPoseToPoseInfo(AndroidJavaObject javaPose)
        {
            if (javaPose == null)
            {
                return null;
            }

            return new PoseInfo
            {
                timestamp = GetJavaLongField(javaPose, "timestamp"),
                x = GetJavaDoubleField(javaPose, "x"),
                y = GetJavaDoubleField(javaPose, "y"),
                z = GetJavaDoubleField(javaPose, "z"),
                rw = GetJavaDoubleField(javaPose, "rw"),
                rx = GetJavaDoubleField(javaPose, "rx"),
                ry = GetJavaDoubleField(javaPose, "ry"),
                rz = GetJavaDoubleField(javaPose, "rz"),
                type = GetJavaIntField(javaPose, "type"),
                confidence = GetJavaIntField(javaPose, "confidence"),
                poseError = GetJavaIntField(javaPose, "poseError")
            };
        }

        private static PoseInfo[] ConvertJavaPoseListToPoseInfos(AndroidJavaObject javaPoseList)
        {
            if (javaPoseList == null)
            {
                return null;
            }

            int count = javaPoseList.Call<int>("size");
            PoseInfo[] poseInfos = new PoseInfo[count];
            for (int i = 0; i < count; i++)
            {
                using (AndroidJavaObject javaPose = javaPoseList.Call<AndroidJavaObject>("get", i))
                {
                    poseInfos[i] = ConvertJavaPoseToPoseInfo(javaPose);
                }
            }

            return poseInfos;
        }

        private static bool TryGetRuntimeControllerPose(
            double predictTime,
            out PoseInfo[] poseInfos,
            out string error)
        {
            poseInfos = null;
            error = null;

            try
            {
                poseInfos = new PoseInfo[2];
                string lastError = null;

                bool hasLeftPose = TryGetRuntimeControllerPose(0, predictTime, out poseInfos[0], out lastError);
                bool hasRightPose = TryGetRuntimeControllerPose(1, predictTime, out poseInfos[1], out string rightError);
                if (!string.IsNullOrEmpty(rightError))
                {
                    lastError = string.IsNullOrEmpty(lastError) ? rightError : lastError + " | " + rightError;
                }

                if (hasLeftPose || hasRightPose)
                {
                    return true;
                }

                error = string.IsNullOrEmpty(lastError) ? "runtime controller returned no valid pose" : lastError;
                return false;
            }
            catch (Exception e)
            {
                error = "runtime controller exception: " + e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        private static bool TryGetRuntimeControllerPose(
            uint deviceId,
            double predictTime,
            out PoseInfo poseInfo,
            out string error)
        {
            poseInfo = null;
            error = null;

            PxrControllerTracking tracking = new PxrControllerTracking();
            // SDK 3.4.0: (deviceID, predictTime, ref tracking) — headData arg removed.
            int result = PXR_Plugin.Controller.UPxr_GetControllerTrackingState(
                deviceId,
                predictTime,
                ref tracking);
            if (result != 0)
            {
                error = $"runtime controller device={deviceId} result={result}";
                return false;
            }

            PxrSensorState sensorState = tracking.localControllerPose;
            poseInfo = ConvertRuntimeControllerPoseToPoseInfo(deviceId, sensorState, result);
            return true;
        }

        private static PoseInfo ConvertRuntimeControllerPoseToPoseInfo(
            uint deviceId,
            PxrSensorState sensorState,
            int nativeResult)
        {
            return new PoseInfo
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
                poseError = nativeResult
            };
        }

        private static bool TryGetBridgeHeadPose(out PoseInfo poseInfo, out string error)
        {
            poseInfo = null;
            error = null;

            try
            {
                using (AndroidJavaClass bridgeClass =
                       new AndroidJavaClass("com.xrobotoolkit.enterprise.EnterprisePoseBridge"))
                {
                    string json = bridgeClass.CallStatic<string>("getHeadPoseJson", 0L);
                    JsonData root = JsonMapper.ToObject(json);
                    if (IsBridgeSuccess(root) && HasJsonKey(root, "pose"))
                    {
                        poseInfo = ConvertBridgePoseToPoseInfo(root["pose"]);
                        return poseInfo != null;
                    }

                    error = "bridge head failed: " + json;
                    return false;
                }
            }
            catch (Exception e)
            {
                ClearPendingJavaException();
                error = "bridge head exception: " + e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        private static bool TryGetBridgeControllerPose(out PoseInfo[] poseInfos, out string error)
        {
            poseInfos = null;
            error = null;

            try
            {
                using (AndroidJavaClass bridgeClass =
                       new AndroidJavaClass("com.xrobotoolkit.enterprise.EnterprisePoseBridge"))
                {
                    string json = bridgeClass.CallStatic<string>("getControllerPoseJson", 0L);
                    JsonData root = JsonMapper.ToObject(json);
                    if (IsBridgeSuccess(root) && HasJsonKey(root, "poses"))
                    {
                        poseInfos = ConvertBridgePoseArrayToPoseInfos(root["poses"]);
                        return poseInfos != null;
                    }

                    error = "bridge controller failed: " + json;
                    return false;
                }
            }
            catch (Exception e)
            {
                ClearPendingJavaException();
                error = "bridge controller exception: " + e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        private static void ClearPendingJavaException()
        {
            IntPtr exception = AndroidJNI.ExceptionOccurred();
            if (exception == IntPtr.Zero)
            {
                return;
            }

            AndroidJNI.ExceptionClear();
            AndroidJNI.DeleteLocalRef(exception);
        }

        private static bool IsBridgeSuccess(JsonData root)
        {
            return root != null && root.IsObject && HasJsonKey(root, "success") && (bool)root["success"];
        }

        private static bool HasJsonKey(JsonData data, string key)
        {
            return data != null && data.IsObject && ((IDictionary)data).Contains(key);
        }

        private static PoseInfo ConvertBridgePoseToPoseInfo(JsonData pose)
        {
            if (pose == null || !pose.IsObject)
            {
                return null;
            }

            return new PoseInfo
            {
                timestamp = GetJsonLong(pose, "timestamp"),
                x = GetJsonDouble(pose, "x"),
                y = GetJsonDouble(pose, "y"),
                z = GetJsonDouble(pose, "z"),
                rw = GetJsonDouble(pose, "rw"),
                rx = GetJsonDouble(pose, "rx"),
                ry = GetJsonDouble(pose, "ry"),
                rz = GetJsonDouble(pose, "rz"),
                type = GetJsonInt(pose, "type"),
                confidence = GetJsonInt(pose, "confidence"),
                poseError = GetJsonInt(pose, "poseError")
            };
        }

        private static PoseInfo[] ConvertBridgePoseArrayToPoseInfos(JsonData poses)
        {
            if (poses == null || !poses.IsArray)
            {
                return null;
            }

            PoseInfo[] poseInfos = new PoseInfo[poses.Count];
            for (int i = 0; i < poses.Count; i++)
            {
                poseInfos[i] = ConvertBridgePoseToPoseInfo(poses[i]);
            }

            return poseInfos;
        }

        private static long GetJsonLong(JsonData data, string key)
        {
            return HasJsonKey(data, key)
                ? long.Parse(data[key].ToString(), CultureInfo.InvariantCulture)
                : 0L;
        }

        private static double GetJsonDouble(JsonData data, string key)
        {
            return HasJsonKey(data, key)
                ? double.Parse(data[key].ToString(), CultureInfo.InvariantCulture)
                : 0.0;
        }

        private static int GetJsonInt(JsonData data, string key)
        {
            return HasJsonKey(data, key)
                ? int.Parse(data[key].ToString(), CultureInfo.InvariantCulture)
                : 0;
        }

        private static AndroidJavaObject InvokeJavaObjectMethodWithLongArg(AndroidJavaObject javaObject,
            string methodName, long arg)
        {
            IntPtr classClass = IntPtr.Zero;
            IntPtr objectClass = IntPtr.Zero;
            IntPtr methodNameString = IntPtr.Zero;
            IntPtr parameterTypes = IntPtr.Zero;
            IntPtr invokeArgsArray = IntPtr.Zero;

            try
            {
                using (AndroidJavaObject javaClass = javaObject.Call<AndroidJavaObject>("getClass"))
                using (AndroidJavaClass longType = new AndroidJavaClass("java.lang.Long"))
                using (AndroidJavaObject longClass = longType.GetStatic<AndroidJavaObject>("TYPE"))
                using (AndroidJavaObject longObject = new AndroidJavaObject("java.lang.Long", arg))
                {
                    classClass = AndroidJNI.FindClass("java/lang/Class");
                    ThrowIfJavaException("find java.lang.Class");
                    objectClass = AndroidJNI.FindClass("java/lang/Object");
                    ThrowIfJavaException("find java.lang.Object");

                    parameterTypes = AndroidJNI.NewObjectArray(1, classClass, IntPtr.Zero);
                    AndroidJNI.SetObjectArrayElement(parameterTypes, 0, longClass.GetRawObject());
                    ThrowIfJavaException("build parameter type array");

                    IntPtr getMethodId = AndroidJNI.GetMethodID(javaClass.GetRawClass(), "getMethod",
                        "(Ljava/lang/String;[Ljava/lang/Class;)Ljava/lang/reflect/Method;");
                    ThrowIfJavaException("find Class.getMethod");

                    methodNameString = AndroidJNI.NewStringUTF(methodName);
                    jvalue[] getMethodArgs = new jvalue[2];
                    getMethodArgs[0].l = methodNameString;
                    getMethodArgs[1].l = parameterTypes;

                    IntPtr methodObjectPtr = AndroidJNI.CallObjectMethod(javaClass.GetRawObject(), getMethodId,
                        getMethodArgs);
                    ThrowIfJavaException("invoke Class.getMethod for " + methodName);
                    if (methodObjectPtr == IntPtr.Zero)
                    {
                        return null;
                    }

                    using (AndroidJavaObject method = new AndroidJavaObject(methodObjectPtr))
                    {
                        invokeArgsArray = AndroidJNI.NewObjectArray(1, objectClass, IntPtr.Zero);
                        AndroidJNI.SetObjectArrayElement(invokeArgsArray, 0, longObject.GetRawObject());
                        ThrowIfJavaException("build invoke argument array");

                        IntPtr invokeMethodId = AndroidJNI.GetMethodID(method.GetRawClass(), "invoke",
                            "(Ljava/lang/Object;[Ljava/lang/Object;)Ljava/lang/Object;");
                        ThrowIfJavaException("find Method.invoke");

                        jvalue[] invokeArgs = new jvalue[2];
                        invokeArgs[0].l = javaObject.GetRawObject();
                        invokeArgs[1].l = invokeArgsArray;

                        IntPtr result = AndroidJNI.CallObjectMethod(method.GetRawObject(), invokeMethodId, invokeArgs);
                        ThrowIfJavaException("invoke " + methodName);
                        return result == IntPtr.Zero ? null : new AndroidJavaObject(result);
                    }
                }
            }
            finally
            {
                if (invokeArgsArray != IntPtr.Zero)
                {
                    AndroidJNI.DeleteLocalRef(invokeArgsArray);
                }
                if (parameterTypes != IntPtr.Zero)
                {
                    AndroidJNI.DeleteLocalRef(parameterTypes);
                }
                if (methodNameString != IntPtr.Zero)
                {
                    AndroidJNI.DeleteLocalRef(methodNameString);
                }
                if (objectClass != IntPtr.Zero)
                {
                    AndroidJNI.DeleteLocalRef(objectClass);
                }
                if (classClass != IntPtr.Zero)
                {
                    AndroidJNI.DeleteLocalRef(classClass);
                }
            }
        }

        private static void ThrowIfJavaException(string context)
        {
            IntPtr exception = AndroidJNI.ExceptionOccurred();
            if (exception == IntPtr.Zero)
            {
                return;
            }

            AndroidJNI.ExceptionClear();
            AndroidJNI.DeleteLocalRef(exception);
            throw new InvalidOperationException("Java exception while " + context);
        }

        private static string GetJavaObjectDiagnostics(AndroidJavaObject javaObject)
        {
            if (javaObject == null)
            {
                return "binder=null";
            }

            try
            {
                using (AndroidJavaObject javaClass = javaObject.Call<AndroidJavaObject>("getClass"))
                {
                    string className = javaClass.Call<string>("getName");
                    return "binderClass=" + className + ", poseMethods=" + GetJavaMethodSummary(javaClass);
                }
            }
            catch (Exception e)
            {
                return "binderDiagnosticsFailed=" + e.GetType().Name + ": " + e.Message;
            }
        }

        private static string GetJavaMethodSummary(AndroidJavaObject javaClass)
        {
            IntPtr methodsArray = IntPtr.Zero;
            try
            {
                IntPtr getMethodsId = AndroidJNI.GetMethodID(javaClass.GetRawClass(), "getMethods",
                    "()[Ljava/lang/reflect/Method;");
                ThrowIfJavaException("find Class.getMethods");

                methodsArray = AndroidJNI.CallObjectMethod(javaClass.GetRawObject(), getMethodsId, new jvalue[0]);
                ThrowIfJavaException("invoke Class.getMethods");
                if (methodsArray == IntPtr.Zero)
                {
                    return "null";
                }

                int count = AndroidJNI.GetArrayLength(methodsArray);
                int matchedCount = 0;
                string summary = "";
                for (int i = 0; i < count; i++)
                {
                    IntPtr methodPtr = AndroidJNI.GetObjectArrayElement(methodsArray, i);
                    if (methodPtr == IntPtr.Zero)
                    {
                        continue;
                    }

                    try
                    {
                        using (AndroidJavaObject method = new AndroidJavaObject(methodPtr))
                        {
                            string methodString = method.Call<string>("toString");
                            if (methodString.Contains("Pose") || methodString.Contains("pose") ||
                                methodString.Contains("pbsCommonMessageLocked"))
                            {
                                if (summary.Length < 1200)
                                {
                                    summary += methodString + "; ";
                                }

                                matchedCount++;
                            }
                        }
                    }
                    finally
                    {
                        AndroidJNI.DeleteLocalRef(methodPtr);
                    }
                }

                return matchedCount == 0 ? "no pose methods" : "count=" + matchedCount + ", " + summary;
            }
            catch (Exception e)
            {
                return "methodDiagnosticsFailed=" + e.GetType().Name + ": " + e.Message;
            }
            finally
            {
                if (methodsArray != IntPtr.Zero)
                {
                    AndroidJNI.DeleteLocalRef(methodsArray);
                }
            }
        }

        public static PoseInfo GetHeadPose()
        {
#if PICO_PLATFORM
            AndroidJNI.AttachCurrentThread();

            if (TryGetBridgeHeadPose(out PoseInfo bridgePoseInfo, out string bridgeError))
            {
                return bridgePoseInfo;
            }

            using (AndroidJavaClass threadTobHelperClass = new AndroidJavaClass("com.pvr.tobservice.ToBServiceHelper"))
            using (AndroidJavaObject threadTobHelper = threadTobHelperClass.CallStatic<AndroidJavaObject>("getInstance"))
            using (AndroidJavaObject threadServiceBinder = threadTobHelper.Call<AndroidJavaObject>("getServiceBinder"))
            {
                if (threadServiceBinder == null)
                {
                    return null;
                }

                try
                {
                    using (AndroidJavaObject javaPose = InvokeJavaObjectMethodWithLongArg(threadServiceBinder,
                               "getHeadPose", 0L))
                    {
                        return ConvertJavaPoseToPoseInfo(javaPose);
                    }
                }
                catch (Exception e)
                {
                    throw new InvalidOperationException(bridgeError + " | direct fallback: " + e.Message + " | " + GetJavaObjectDiagnostics(threadServiceBinder),
                        e);
                }
            }
#endif
            return null;
        }

        public static PoseInfo[] GetControllerPose(double predictTime)
        {
#if PICO_PLATFORM
            AndroidJNI.AttachCurrentThread();

            if (TryGetRuntimeControllerPose(predictTime, out PoseInfo[] runtimePoseInfos, out string runtimeError))
            {
                return runtimePoseInfos;
            }

            if (TryGetBridgeControllerPose(out PoseInfo[] bridgePoseInfos, out string bridgeError))
            {
                return bridgePoseInfos;
            }

            using (AndroidJavaClass threadTobHelperClass = new AndroidJavaClass("com.pvr.tobservice.ToBServiceHelper"))
            using (AndroidJavaObject threadTobHelper = threadTobHelperClass.CallStatic<AndroidJavaObject>("getInstance"))
            using (AndroidJavaObject threadServiceBinder = threadTobHelper.Call<AndroidJavaObject>("getServiceBinder"))
            {
                if (threadServiceBinder == null)
                {
                    return null;
                }

                try
                {
                    using (AndroidJavaObject javaPoseList = InvokeJavaObjectMethodWithLongArg(threadServiceBinder,
                               "getControllerPose", 0L))
                    {
                        return ConvertJavaPoseListToPoseInfos(javaPoseList);
                    }
                }
                catch (Exception e)
                {
                    throw new InvalidOperationException(runtimeError + " | " + bridgeError +
                        " | direct fallback: " + e.Message + " | " + GetJavaObjectDiagnostics(threadServiceBinder),
                        e);
                }
            }
#endif
            return null;
        }

    }
}
