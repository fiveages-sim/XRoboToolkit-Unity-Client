package com.xrobotoolkit.enterprise;

import android.media.MediaCodec;
import android.media.MediaCodecInfo;
import android.media.MediaFormat;
import android.media.MediaMuxer;
import android.opengl.EGL14;
import android.opengl.EGLConfig;
import android.opengl.EGLContext;
import android.opengl.EGLDisplay;
import android.opengl.EGLExt;
import android.opengl.EGLSurface;
import android.opengl.GLES20;
import android.os.SystemClock;
import android.util.Log;
import android.view.Surface;

import java.io.BufferedWriter;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.OutputStreamWriter;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicLong;

/**
 * Independent MediaCodec Surface encoder + MediaMuxer for encode-path probes.
 * Not tied to PICO camera; B0 feeds an EGL clear pattern (MediaCodec input Surface
 * does not support Canvas.lockCanvas — must use GLES).
 *
 * createInputSurface() is called after configure() and before start().
 * host_elapsedRealtimeNs_at_output is dequeue time, NOT camera capture time.
 */
public final class SurfaceVideoEncoder {
    private static final String TAG = "SurfaceVideoEncoder";
    private static final String MIME = MediaFormat.MIMETYPE_VIDEO_AVC;

    public static final int STATE_IDLE = 0;
    public static final int STATE_CONFIGURED = 1;
    public static final int STATE_RECORDING = 2;
    public static final int STATE_STOPPING_PRODUCER = 3;
    public static final int STATE_DRAINING_ENCODER = 4;
    public static final int STATE_FINALIZING_MP4 = 5;
    public static final int STATE_COMPLETED = 6;
    public static final int STATE_FAILED_PARTIAL = 7;

    private final Object lock = new Object();
    private MediaCodec codec;
    private MediaMuxer muxer;
    private Surface inputSurface;
    private Thread drainThread;
    private Thread patternThread;

    private int width;
    private int height;
    private int fps;
    private int bitrateBps;
    private String outputPath;
    private String frameMapPath;

    private int videoTrackIndex = -1;
    private boolean muxerStarted;
    private final AtomicInteger state = new AtomicInteger(STATE_IDLE);
    private final AtomicBoolean drainRun = new AtomicBoolean(false);
    private final AtomicBoolean patternRun = new AtomicBoolean(false);
    private final AtomicBoolean eosSeen = new AtomicBoolean(false);
    private final AtomicBoolean muxerStopOk = new AtomicBoolean(false);

    private final AtomicLong outputSampleCount = new AtomicLong(0);
    private final AtomicLong outputBytes = new AtomicLong(0);
    private final AtomicLong firstPtsUs = new AtomicLong(-1);
    private final AtomicLong lastPtsUs = new AtomicLong(-1);
    private final AtomicLong patternSubmitted = new AtomicLong(0);
    private final AtomicInteger encoderErrors = new AtomicInteger(0);
    private volatile String lastError = "";
    private volatile String stateTimeline = "";

    private BufferedWriter frameMapWriter;
    private final MediaCodec.BufferInfo bufferInfo = new MediaCodec.BufferInfo();

    public int getState() {
        return state.get();
    }

    public String getStateName() {
        switch (state.get()) {
            case STATE_CONFIGURED:
                return "CONFIGURED";
            case STATE_RECORDING:
                return "RECORDING";
            case STATE_STOPPING_PRODUCER:
                return "STOPPING_PRODUCER";
            case STATE_DRAINING_ENCODER:
                return "DRAINING_ENCODER";
            case STATE_FINALIZING_MP4:
                return "FINALIZING_MP4";
            case STATE_COMPLETED:
                return "COMPLETED";
            case STATE_FAILED_PARTIAL:
                return "FAILED_PARTIAL";
            default:
                return "IDLE";
        }
    }

    public Surface getInputSurface() {
        return inputSurface;
    }

    /**
     * Native Surface handle for PICO StartPreviewfor4U(IntPtr). Uses reflection;
     * returns 0 if unavailable.
     */
    public long getInputSurfaceNativeHandle() {
        Surface surface = inputSurface;
        if (surface == null) {
            return 0L;
        }
        try {
            Method method = Surface.class.getDeclaredMethod("getNativeObject");
            method.setAccessible(true);
            Object value = method.invoke(surface);
            if (value instanceof Long) {
                return (Long) value;
            }
        } catch (Throwable ignored) {
        }
        try {
            Field field = Surface.class.getDeclaredField("mNativeObject");
            field.setAccessible(true);
            return field.getLong(surface);
        } catch (Throwable error) {
            Log.w(TAG, "getInputSurfaceNativeHandle failed", error);
            return 0L;
        }
    }

    public long getOutputSampleCount() {
        return outputSampleCount.get();
    }

    public long getPatternSubmitted() {
        return patternSubmitted.get();
    }

    public long getFirstPtsUs() {
        return firstPtsUs.get();
    }

    public long getLastPtsUs() {
        return lastPtsUs.get();
    }

    public long getOutputBytes() {
        return outputBytes.get();
    }

    public boolean getEosSeen() {
        return eosSeen.get();
    }

    public boolean getMuxerStopOk() {
        return muxerStopOk.get();
    }

    public String getLastError() {
        return lastError == null ? "" : lastError;
    }

    public String getStateTimeline() {
        return stateTimeline == null ? "" : stateTimeline;
    }

    public String getStatsJson() {
        return "{"
                + "\"state\":\"" + getStateName() + "\","
                + "\"outputSampleCount\":" + outputSampleCount.get() + ","
                + "\"patternSubmitted\":" + patternSubmitted.get() + ","
                + "\"outputBytes\":" + outputBytes.get() + ","
                + "\"firstPtsUs\":" + firstPtsUs.get() + ","
                + "\"lastPtsUs\":" + lastPtsUs.get() + ","
                + "\"eosSeen\":" + eosSeen.get() + ","
                + "\"muxerStopOk\":" + muxerStopOk.get() + ","
                + "\"encoderErrors\":" + encoderErrors.get() + ","
                + "\"lastError\":\"" + escape(lastError) + "\""
                + "}";
    }

    /**
     * @return 0 on success, negative on failure
     */
    public int start(String outputMp4Path, String frameMapJsonlPath, int width, int height,
                     int fps, int bitrateBps) {
        synchronized (lock) {
            if (state.get() != STATE_IDLE && state.get() != STATE_COMPLETED
                    && state.get() != STATE_FAILED_PARTIAL) {
                return -1;
            }
            releaseInternal(false);
            if (outputMp4Path == null || outputMp4Path.length() == 0 || width <= 0 || height <= 0) {
                return -2;
            }
            this.width = width;
            this.height = height;
            this.fps = fps > 0 ? fps : 30;
            this.bitrateBps = bitrateBps > 0 ? bitrateBps : 8_000_000;
            this.outputPath = outputMp4Path;
            this.frameMapPath = frameMapJsonlPath;
            outputSampleCount.set(0);
            outputBytes.set(0);
            firstPtsUs.set(-1);
            lastPtsUs.set(-1);
            patternSubmitted.set(0);
            encoderErrors.set(0);
            eosSeen.set(false);
            muxerStopOk.set(false);
            lastError = "";
            stateTimeline = "";
            noteState(STATE_CONFIGURED);

            try {
                MediaFormat format = MediaFormat.createVideoFormat(MIME, width, height);
                format.setInteger(MediaFormat.KEY_COLOR_FORMAT,
                        MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface);
                format.setInteger(MediaFormat.KEY_BIT_RATE, this.bitrateBps);
                format.setInteger(MediaFormat.KEY_FRAME_RATE, this.fps);
                format.setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 1);

                codec = MediaCodec.createEncoderByType(MIME);
                codec.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE);
                // Must be after configure() and before start().
                inputSurface = codec.createInputSurface();
                muxer = new MediaMuxer(outputMp4Path, MediaMuxer.OutputFormat.MUXER_OUTPUT_MPEG_4);
                muxerStarted = false;
                videoTrackIndex = -1;

                if (frameMapJsonlPath != null && frameMapJsonlPath.length() > 0) {
                    frameMapWriter = new BufferedWriter(new OutputStreamWriter(
                            new FileOutputStream(frameMapJsonlPath), StandardCharsets.UTF_8));
                }

                codec.start();
                drainRun.set(true);
                drainThread = new Thread(this::drainLoop, "SurfaceVideoEncoderDrain");
                drainThread.setDaemon(true);
                drainThread.start();
                noteState(STATE_RECORDING);
                return 0;
            } catch (Throwable error) {
                lastError = "start failed: " + error.getMessage();
                Log.e(TAG, lastError, error);
                noteState(STATE_FAILED_PARTIAL);
                releaseInternal(false);
                return -3;
            }
        }
    }

    /** B0: paint moving test pattern onto the encoder Surface. Does not use PICO camera. */
    public int startPatternProducer(int targetFps) {
        synchronized (lock) {
            if (inputSurface == null || state.get() != STATE_RECORDING) {
                return -1;
            }
            if (patternRun.get()) {
                return 0;
            }
            final int drawFps = targetFps > 0 ? targetFps : fps;
            patternRun.set(true);
            patternThread = new Thread(() -> patternLoop(drawFps), "SurfaceVideoEncoderPattern");
            patternThread.setDaemon(true);
            patternThread.start();
            return 0;
        }
    }

    public void stopPatternProducer() {
        patternRun.set(false);
        Thread thread = patternThread;
        if (thread != null) {
            try {
                thread.join(2000);
            } catch (InterruptedException ignored) {
            }
        }
        patternThread = null;
    }

    /**
     * Stop producer (caller must stop camera/pattern first or pass stopPattern=true),
     * signal EOS, drain encoder, finalize muxer.
     *
     * @return 0 completed, negative failed partial
     */
    public int stop(boolean stopPattern) {
        synchronized (lock) {
            int current = state.get();
            if (current == STATE_IDLE || current == STATE_COMPLETED || current == STATE_FAILED_PARTIAL) {
                return current == STATE_COMPLETED ? 0 : -1;
            }
            noteState(STATE_STOPPING_PRODUCER);
        }

        if (stopPattern) {
            stopPatternProducer();
        }

        synchronized (lock) {
            noteState(STATE_DRAINING_ENCODER);
            try {
                if (codec != null) {
                    codec.signalEndOfInputStream();
                }
            } catch (Throwable error) {
                lastError = "signalEndOfInputStream: " + error.getMessage();
                encoderErrors.incrementAndGet();
                Log.e(TAG, lastError, error);
            }
        }

        // Wait for drain to observe EOS (drain thread still running).
        long waitStart = SystemClock.elapsedRealtime();
        while (!eosSeen.get() && SystemClock.elapsedRealtime() - waitStart < 8000) {
            try {
                Thread.sleep(20);
            } catch (InterruptedException ignored) {
            }
        }

        drainRun.set(false);
        Thread drain = drainThread;
        if (drain != null) {
            try {
                drain.join(5000);
            } catch (InterruptedException ignored) {
            }
        }
        drainThread = null;

        synchronized (lock) {
            noteState(STATE_FINALIZING_MP4);
            boolean ok = true;
            try {
                if (muxer != null && muxerStarted) {
                    muxer.stop();
                    muxerStopOk.set(true);
                } else {
                    lastError = appendError(lastError, "muxer never started");
                    ok = false;
                }
            } catch (Throwable error) {
                ok = false;
                muxerStopOk.set(false);
                lastError = appendError(lastError, "muxer.stop: " + error.getMessage());
                Log.e(TAG, lastError, error);
            }

            releaseInternal(true);
            if (ok && eosSeen.get()) {
                noteState(STATE_COMPLETED);
                return 0;
            }
            noteState(STATE_FAILED_PARTIAL);
            return -4;
        }
    }

    /**
     * B0 producer: GLES clear + eglSwapBuffers into MediaCodec input Surface.
     * Canvas.lockCanvas is unsupported on codec input surfaces and can crash.
     */
    private void patternLoop(int drawFps) {
        long intervalMs = Math.max(1L, 1000L / Math.max(1, drawFps));
        EGLDisplay display = EGL14.EGL_NO_DISPLAY;
        EGLContext context = EGL14.EGL_NO_CONTEXT;
        EGLSurface eglSurface = EGL14.EGL_NO_SURFACE;
        try {
            display = EGL14.eglGetDisplay(EGL14.EGL_DEFAULT_DISPLAY);
            if (display == EGL14.EGL_NO_DISPLAY) {
                throw new RuntimeException("eglGetDisplay failed");
            }
            int[] version = new int[2];
            if (!EGL14.eglInitialize(display, version, 0, version, 1)) {
                throw new RuntimeException("eglInitialize failed");
            }
            int[] attribList = {
                    EGL14.EGL_RED_SIZE, 8,
                    EGL14.EGL_GREEN_SIZE, 8,
                    EGL14.EGL_BLUE_SIZE, 8,
                    EGL14.EGL_ALPHA_SIZE, 8,
                    EGL14.EGL_RENDERABLE_TYPE, EGL14.EGL_OPENGL_ES2_BIT,
                    EGL14.EGL_SURFACE_TYPE, EGL14.EGL_WINDOW_BIT,
                    EGL14.EGL_NONE
            };
            EGLConfig[] configs = new EGLConfig[1];
            int[] numConfigs = new int[1];
            if (!EGL14.eglChooseConfig(display, attribList, 0, configs, 0, 1, numConfigs, 0)
                    || numConfigs[0] <= 0) {
                throw new RuntimeException("eglChooseConfig failed");
            }
            int[] ctxAttrib = {EGL14.EGL_CONTEXT_CLIENT_VERSION, 2, EGL14.EGL_NONE};
            context = EGL14.eglCreateContext(display, configs[0], EGL14.EGL_NO_CONTEXT, ctxAttrib, 0);
            if (context == EGL14.EGL_NO_CONTEXT) {
                throw new RuntimeException("eglCreateContext failed");
            }
            int[] surfaceAttrib = {EGL14.EGL_NONE};
            eglSurface = EGL14.eglCreateWindowSurface(display, configs[0], inputSurface, surfaceAttrib, 0);
            if (eglSurface == EGL14.EGL_NO_SURFACE) {
                throw new RuntimeException("eglCreateWindowSurface failed");
            }
            if (!EGL14.eglMakeCurrent(display, eglSurface, eglSurface, context)) {
                throw new RuntimeException("eglMakeCurrent failed");
            }

            int frame = 0;
            while (patternRun.get()) {
                long t0 = SystemClock.elapsedRealtime();
                float r = ((frame * 17) % 255) / 255f;
                float g = ((frame * 37) % 255) / 255f;
                float b = ((frame * 57) % 255) / 255f;
                GLES20.glViewport(0, 0, width, height);
                GLES20.glClearColor(r, g, b, 1f);
                GLES20.glClear(GLES20.GL_COLOR_BUFFER_BIT);
                // Present with a steady PTS in nanoseconds for the encoder.
                long ptsNs = frame * (1_000_000_000L / Math.max(1, drawFps));
                EGLExt.eglPresentationTimeANDROID(display, eglSurface, ptsNs);
                if (!EGL14.eglSwapBuffers(display, eglSurface)) {
                    lastError = appendError(lastError, "eglSwapBuffers failed");
                    encoderErrors.incrementAndGet();
                    break;
                }
                patternSubmitted.incrementAndGet();
                frame++;
                long spent = SystemClock.elapsedRealtime() - t0;
                long sleep = intervalMs - spent;
                if (sleep > 0) {
                    try {
                        Thread.sleep(sleep);
                    } catch (InterruptedException ignored) {
                    }
                }
            }
        } catch (Throwable error) {
            lastError = appendError(lastError, "egl-pattern: " + error.getMessage());
            encoderErrors.incrementAndGet();
            Log.e(TAG, "EGL pattern failed", error);
        } finally {
            if (display != EGL14.EGL_NO_DISPLAY) {
                EGL14.eglMakeCurrent(display, EGL14.EGL_NO_SURFACE, EGL14.EGL_NO_SURFACE,
                        EGL14.EGL_NO_CONTEXT);
                if (eglSurface != EGL14.EGL_NO_SURFACE) {
                    EGL14.eglDestroySurface(display, eglSurface);
                }
                if (context != EGL14.EGL_NO_CONTEXT) {
                    EGL14.eglDestroyContext(display, context);
                }
                EGL14.eglTerminate(display);
            }
        }
    }

    private void drainLoop() {
        while (drainRun.get() || !eosSeen.get()) {
            MediaCodec localCodec;
            synchronized (lock) {
                localCodec = codec;
            }
            if (localCodec == null) {
                break;
            }
            try {
                int outIndex = localCodec.dequeueOutputBuffer(bufferInfo, 10_000);
                if (outIndex == MediaCodec.INFO_TRY_AGAIN_LATER) {
                    if (!drainRun.get() && eosSeen.get()) {
                        break;
                    }
                    continue;
                }
                if (outIndex == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                    synchronized (lock) {
                        if (muxerStarted) {
                            continue;
                        }
                        MediaFormat newFormat = localCodec.getOutputFormat();
                        videoTrackIndex = muxer.addTrack(newFormat);
                        muxer.start();
                        muxerStarted = true;
                        Log.i(TAG, "muxer started format=" + newFormat);
                    }
                    continue;
                }
                if (outIndex == MediaCodec.INFO_OUTPUT_BUFFERS_CHANGED) {
                    continue;
                }
                if (outIndex < 0) {
                    continue;
                }

                ByteBuffer encoded = localCodec.getOutputBuffer(outIndex);
                if (encoded != null && bufferInfo.size > 0 && (bufferInfo.flags & MediaCodec.BUFFER_FLAG_CODEC_CONFIG) == 0) {
                    if (muxerStarted && videoTrackIndex >= 0) {
                        encoded.position(bufferInfo.offset);
                        encoded.limit(bufferInfo.offset + bufferInfo.size);
                        muxer.writeSampleData(videoTrackIndex, encoded, bufferInfo);
                        long pts = bufferInfo.presentationTimeUs;
                        long hostNs = SystemClock.elapsedRealtimeNanos();
                        long index = outputSampleCount.getAndIncrement();
                        outputBytes.addAndGet(bufferInfo.size);
                        if (firstPtsUs.get() < 0) {
                            firstPtsUs.set(pts);
                        }
                        lastPtsUs.set(pts);
                        writeFrameMapLine(index, pts, hostNs);
                    }
                }
                boolean eos = (bufferInfo.flags & MediaCodec.BUFFER_FLAG_END_OF_STREAM) != 0;
                localCodec.releaseOutputBuffer(outIndex, false);
                if (eos) {
                    eosSeen.set(true);
                    Log.i(TAG, "encoder EOS");
                    break;
                }
            } catch (Throwable error) {
                encoderErrors.incrementAndGet();
                lastError = appendError(lastError, "drain: " + error.getMessage());
                Log.e(TAG, "drain failed", error);
                eosSeen.set(true);
                break;
            }
        }
    }

    private void writeFrameMapLine(long index, long ptsUs, long hostNs) {
        BufferedWriter writer = frameMapWriter;
        if (writer == null) {
            return;
        }
        try {
            // host_elapsedRealtimeNs_at_output is NOT capture time.
            writer.write("{\"frame_index\":");
            writer.write(Long.toString(index));
            writer.write(",\"video_pts_us\":");
            writer.write(Long.toString(ptsUs));
            writer.write(",\"host_elapsedRealtimeNs_at_output\":");
            writer.write(Long.toString(hostNs));
            writer.write(",\"sdk_timestamp_ns\":null");
            writer.write(",\"note\":\"host_at_output_is_dequeue_not_capture\"}");
            writer.write('\n');
        } catch (IOException error) {
            encoderErrors.incrementAndGet();
            lastError = appendError(lastError, "frame_map: " + error.getMessage());
        }
    }

    private void noteState(int next) {
        long now = SystemClock.elapsedRealtime();
        String name;
        switch (next) {
            case STATE_CONFIGURED:
                name = "CONFIGURED";
                break;
            case STATE_RECORDING:
                name = "RECORDING";
                break;
            case STATE_STOPPING_PRODUCER:
                name = "STOPPING_PRODUCER";
                break;
            case STATE_DRAINING_ENCODER:
                name = "DRAINING_ENCODER";
                break;
            case STATE_FINALIZING_MP4:
                name = "FINALIZING_MP4";
                break;
            case STATE_COMPLETED:
                name = "COMPLETED";
                break;
            case STATE_FAILED_PARTIAL:
                name = "FAILED_PARTIAL";
                break;
            default:
                name = "IDLE";
                break;
        }
        state.set(next);
        if (stateTimeline.length() > 0) {
            stateTimeline += ";";
        }
        stateTimeline += name + "@" + now;
    }

    private void releaseInternal(boolean keepState) {
        if (frameMapWriter != null) {
            try {
                frameMapWriter.flush();
                frameMapWriter.close();
            } catch (IOException ignored) {
            }
            frameMapWriter = null;
        }
        if (codec != null) {
            try {
                codec.stop();
            } catch (Throwable ignored) {
            }
            try {
                codec.release();
            } catch (Throwable ignored) {
            }
            codec = null;
        }
        if (inputSurface != null) {
            try {
                inputSurface.release();
            } catch (Throwable ignored) {
            }
            inputSurface = null;
        }
        if (muxer != null) {
            try {
                muxer.release();
            } catch (Throwable ignored) {
            }
            muxer = null;
        }
        muxerStarted = false;
        videoTrackIndex = -1;
        if (!keepState) {
            state.set(STATE_IDLE);
        }
    }

    private static String appendError(String current, String next) {
        if (current == null || current.length() == 0) {
            return next;
        }
        return current + " | " + next;
    }

    private static String escape(String value) {
        if (value == null) {
            return "";
        }
        return value.replace("\\", "\\\\").replace("\"", "\\\"");
    }
}
