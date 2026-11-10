package com.xrobotoolkit.enterprise;

import android.os.Handler;
import android.os.Looper;
import android.util.Log;

import com.picovr.robotassistantlib.CameraHandle;
import com.pxr.capturelib.PXRCamera;
import com.pxr.capturelib.PXRCameraCallBack;

import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.util.concurrent.atomic.AtomicInteger;

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

    static {
        // Vision PXRCamera JNI lives in a renamed .so so it can coexist with
        // capturelib-release's libpvrcapturelib.so (PXRCapture / for4U).
        try {
            System.loadLibrary("pvrcamera07lib");
            Log.i(TAG, "loaded libpvrcamera07lib for PXRCamera JNI");
        } catch (Throwable error) {
            Log.e(TAG, "failed to load libpvrcamera07lib", error);
        }
    }

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

    /**
     * CameraHandle.SetConfig always calls enableEIS(true) before configure.
     * Electronic stabilization keeps a margin and crops the recorded frame.
     * Call this after SetConfig so the MP4 keeps the capture size's full field.
     * Native enableEIS returns 1 on the success log path.
     */
    public static int disableElectronicStabilization(CameraHandle cameraHandle) {
        try {
            if (cameraHandle == null) {
                return -2;
            }

            Field cameraField = CameraHandle.class.getDeclaredField("mPXRCamera");
            cameraField.setAccessible(true);
            Object cameraObject = cameraField.get(cameraHandle);
            if (!(cameraObject instanceof PXRCamera)) {
                Log.e(TAG, "disableElectronicStabilization missing PXRCamera");
                return -2;
            }

            int result = ((PXRCamera) cameraObject).enableEIS(false);
            Log.i(TAG, "enableEIS(false) result=" + result + " (native success is 1)");
            return result;
        } catch (Throwable error) {
            Log.e(TAG, "disableElectronicStabilization failed", error);
            return -3;
        }
    }

    /**
     * Best-effort only: reflects into CameraHandle private useState so a later
     * CloseCamera does not StopPreview again. There is no public SDK API for this.
     * Field/enum name fallbacks reduce breakage across AAR builds, but a PICO
     * SDK upgrade can still make this return false silently — check logcat for
     * "markUseStateIdle failed" after upgrading robotassistant / capturelib.
     */
    public static boolean markUseStateIdle(CameraHandle cameraHandle) {
        if (cameraHandle == null) {
            return false;
        }

        String[] fieldNames = {"useState", "mUseState", "state"};
        String[] idleNames = {"IDLE", "Idle", "NONE"};
        for (String fieldName : fieldNames) {
            try {
                Field useState = CameraHandle.class.getDeclaredField(fieldName);
                useState.setAccessible(true);
                Object current = useState.get(cameraHandle);
                if (current == null) {
                    continue;
                }

                Class<?> useStateClass = current.getClass();
                if (!useStateClass.isEnum()) {
                    useStateClass = Class.forName("com.picovr.robotassistantlib.CameraHandle$UseState");
                }

                Object idle = null;
                for (String idleName : idleNames) {
                    try {
                        Method valueOf = Enum.class.getMethod("valueOf", Class.class, String.class);
                        idle = valueOf.invoke(null, useStateClass, idleName);
                        break;
                    } catch (Throwable ignored) {
                    }
                }
                if (idle == null) {
                    continue;
                }

                useState.set(cameraHandle, idle);
                Log.i(TAG, "useState set to IDLE via field=" + fieldName
                        + " so CloseCamera does not StopPreview again");
                return true;
            } catch (NoSuchFieldException ignored) {
            } catch (Throwable error) {
                Log.w(TAG, "markUseStateIdle try field=" + fieldName + " failed: " + error.getMessage());
            }
        }

        Log.e(TAG, "markUseStateIdle failed: no compatible useState field/enum on this AAR");
        return false;
    }

    /** 0=idle, 1=pending, 2=done. */
    private static final AtomicInteger finalizePoll = new AtomicInteger(0);
    private static volatile int finalizeStopResult = -1;
    private static volatile int finalizeCloseResult = -1;
    private static volatile int finalizeMarkIdleOk = 0;
    private static Handler mainHandler;

    /**
     * Vision stop: StopPreview+CloseCamera on the main looper.
     * StartRecord runs on the Unity main thread; MediaCodec/GL stop must use that same thread.
     * A background worker left 0-byte MP4s (stopPreviewResult=0) and GL_INVALID_OPERATION.
     * Unity polls {@link #pollFinalizeCapture()} so the C# coroutine can yield.
     *
     * @return 0 accepted, -1 already running, -2 null handle
     */
    public static int beginFinalizeCaptureAsync(CameraHandle cameraHandle) {
        if (cameraHandle == null) {
            return -2;
        }
        if (finalizePoll.get() == 1) {
            return -1;
        }
        finalizePoll.set(1);
        finalizeStopResult = -1;
        finalizeCloseResult = -1;
        finalizeMarkIdleOk = 0;
        if (mainHandler == null) {
            mainHandler = new Handler(Looper.getMainLooper());
        }
        final CameraHandle handle = cameraHandle;
        mainHandler.post(() -> {
            int stopResult = -1;
            int closeResult = -1;
            try {
                // StopPreview must run while useState is still RECORDING so the muxer writes moov.
                stopResult = handle.StopPreview();
                Log.i(TAG, "async finalize StopPreview result=" + stopResult);
            } catch (Throwable error) {
                Log.e(TAG, "async finalize StopPreview failed", error);
                stopResult = -3;
            }
            boolean idleOk = markUseStateIdle(handle);
            finalizeMarkIdleOk = idleOk ? 1 : 0;
            try {
                closeResult = handle.CloseCamera();
                Log.i(TAG, "async finalize CloseCamera result=" + closeResult);
            } catch (Throwable error) {
                Log.e(TAG, "async finalize CloseCamera failed", error);
                closeResult = -3;
            }
            finalizeStopResult = stopResult;
            finalizeCloseResult = closeResult;
            finalizePoll.set(2);
        });
        return 0;
    }

    /** @return 1 pending, 2 done, 0 idle */
    public static int pollFinalizeCapture() {
        return finalizePoll.get();
    }

    /**
     * @return int[3]: stopPreviewResult, closeCameraResult, markIdleOk(0/1).
     * Resets poll to idle.
     */
    public static int[] takeFinalizeCaptureResults() {
        int[] out = new int[]{finalizeStopResult, finalizeCloseResult, finalizeMarkIdleOk};
        finalizePoll.set(0);
        return out;
    }

    /** Force-clear a stuck async finalize so a later session can stop again. */
    public static void resetFinalizeCapture() {
        finalizePoll.set(0);
    }
}
