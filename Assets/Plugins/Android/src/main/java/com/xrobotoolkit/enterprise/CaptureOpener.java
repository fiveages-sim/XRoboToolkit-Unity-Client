package com.xrobotoolkit.enterprise;

import android.util.Log;

import com.picovr.robotassistantlib.CameraHandle;
import com.pxr.capturelib.PXRCamera;
import com.pxr.capturelib.PXRCameraCallBack;

import java.lang.reflect.Field;
import java.lang.reflect.Method;

/**
 * Registers the capture callback before openCameraAsync.
 *
 * robotassistant CameraHandle.OpenCamera does this in the opposite order and logs
 * openCameraAsync's return value with inverted success rules. In libpvrcapturelib,
 * Java_com_pxr_capturelib_PXRCamera_openCameraAsync returns 1 after
 * "PXRCamera--open--Success" and 0 after "PXRCamera--open--fail".
 * setCallback returns 1 on its normal path.
 */
public final class CaptureOpener {
    private static final String TAG = "CaptureOpener";
    public static final int NOT_CALLED = Integer.MIN_VALUE;

    private CaptureOpener() {
    }

    public static int[] openWithCallbackFirst(CameraHandle cameraHandle, PXRCameraCallBack callback) {
        int callbackResult = NOT_CALLED;
        int openResult = NOT_CALLED;
        try {
            if (cameraHandle == null || callback == null) {
                Log.e(TAG, "openWithCallbackFirst missing handle or callback");
                return new int[]{-2, openResult};
            }

            Field cameraField = CameraHandle.class.getDeclaredField("mPXRCamera");
            cameraField.setAccessible(true);
            Object cameraObject = cameraField.get(cameraHandle);
            if (!(cameraObject instanceof PXRCamera)) {
                Log.e(TAG, "mPXRCamera is null or unexpected type");
                return new int[]{-2, openResult};
            }

            PXRCamera camera = (PXRCamera) cameraObject;
            callbackResult = camera.setCallback(callback);
            Log.i(TAG, "setCallback result=" + callbackResult + " (native success is 1)");
            if (callbackResult != 1) {
                return new int[]{callbackResult, openResult};
            }

            openResult = camera.openCameraAsync();
            Log.i(TAG, "openCameraAsync result=" + openResult
                    + " (1 is PXRCamera--open--Success, 0 is PXRCamera--open--fail)");
            return new int[]{callbackResult, openResult};
        } catch (Throwable error) {
            Log.e(TAG, "openWithCallbackFirst failed", error);
            return new int[]{callbackResult == NOT_CALLED ? -3 : callbackResult, -3};
        }
    }

    public static boolean markUseStateIdle(CameraHandle cameraHandle) {
        try {
            if (cameraHandle == null) {
                return false;
            }

            Class<?> useStateClass = Class.forName("com.picovr.robotassistantlib.CameraHandle$UseState");
            Method valueOf = Enum.class.getMethod("valueOf", Class.class, String.class);
            Object idle = valueOf.invoke(null, useStateClass, "IDLE");
            Field useState = CameraHandle.class.getDeclaredField("useState");
            useState.setAccessible(true);
            useState.set(cameraHandle, idle);
            Log.i(TAG, "useState set to IDLE so CloseCamera does not StopPreview again");
            return true;
        } catch (Throwable error) {
            Log.e(TAG, "markUseStateIdle failed", error);
            return false;
        }
    }
}
