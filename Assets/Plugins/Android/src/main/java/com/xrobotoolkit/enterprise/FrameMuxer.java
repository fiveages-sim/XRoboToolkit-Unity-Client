package com.xrobotoolkit.enterprise;

import android.media.MediaCodec;
import android.media.MediaCodecInfo;
import android.media.MediaFormat;
import android.media.MediaMuxer;
import android.util.Log;

import java.io.IOException;
import java.nio.ByteBuffer;

/**
 * Encodes full RGBA frames into an MP4 without cropping or scaling.
 * Used by the experimental for4U image path so CameraRecord_*.mp4 keeps the
 * same naming and stop/finalize flow as the capturelib recorder.
 */
public final class FrameMuxer {
    private static final String TAG = "FrameMuxer";
    private static final String MIME = "video/avc";

    private MediaCodec codec;
    private MediaMuxer muxer;
    private byte[] yuvScratch;
    private int sourceWidth;
    private int sourceHeight;
    private int encodeWidth;
    private int encodeHeight;
    private int trackIndex = -1;
    private boolean muxerStarted;
    private boolean released;
    private final MediaCodec.BufferInfo bufferInfo = new MediaCodec.BufferInfo();

    public synchronized int start(String path, int width, int height, int bitrate, int fps) {
        releaseInternal();
        if (path == null || path.length() == 0 || width <= 0 || height <= 0) {
            return -1;
        }

        sourceWidth = width;
        sourceHeight = height;
        encodeWidth = (width + 1) & ~1;
        encodeHeight = (height + 1) & ~1;
        if (encodeWidth != width || encodeHeight != height) {
            Log.w(TAG, "encoder requires even size; padding to " + encodeWidth + "x" + encodeHeight
                    + " from " + width + "x" + height + " (pad only, no crop of valid pixels)");
        }

        int safeFps = fps > 0 ? fps : 30;
        int safeBitrate = bitrate > 0 ? bitrate : Math.max(8 * 1024 * 1024, encodeWidth * encodeHeight * 2);
        yuvScratch = new byte[encodeWidth * encodeHeight * 3 / 2];

        try {
            MediaFormat format = MediaFormat.createVideoFormat(MIME, encodeWidth, encodeHeight);
            format.setInteger(MediaFormat.KEY_COLOR_FORMAT,
                    MediaCodecInfo.CodecCapabilities.COLOR_FormatYUV420SemiPlanar);
            format.setInteger(MediaFormat.KEY_BIT_RATE, safeBitrate);
            format.setInteger(MediaFormat.KEY_FRAME_RATE, safeFps);
            format.setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 1);

            codec = MediaCodec.createEncoderByType(MIME);
            codec.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE);
            codec.start();
            muxer = new MediaMuxer(path, MediaMuxer.OutputFormat.MUXER_OUTPUT_MPEG_4);
            muxerStarted = false;
            trackIndex = -1;
            released = false;
            Log.i(TAG, "start path=" + path + " source=" + sourceWidth + "x" + sourceHeight
                    + " encode=" + encodeWidth + "x" + encodeHeight
                    + " bitrate=" + safeBitrate + " fps=" + safeFps);
            return 0;
        } catch (IOException error) {
            Log.e(TAG, "start failed", error);
            releaseInternal();
            return -2;
        } catch (Throwable error) {
            Log.e(TAG, "start failed", error);
            releaseInternal();
            return -3;
        }
    }

    public synchronized int encodeRgba(byte[] rgba, int length, long ptsUs) {
        if (released || codec == null || muxer == null || rgba == null || length <= 0) {
            return -1;
        }

        int rgbaBytes = sourceWidth * sourceHeight * 4;
        if (length < rgbaBytes) {
            Log.w(TAG, "encodeRgba short buffer length=" + length + " expected=" + rgbaBytes);
            return -2;
        }

        try {
            drain(false);
            int inputIndex = codec.dequeueInputBuffer(10_000);
            if (inputIndex < 0) {
                return -4;
            }

            rgbaToNv12(rgba, length, yuvScratch, sourceWidth, sourceHeight, encodeWidth, encodeHeight);
            ByteBuffer input = codec.getInputBuffer(inputIndex);
            if (input == null) {
                return -5;
            }
            input.clear();
            input.put(yuvScratch);
            codec.queueInputBuffer(inputIndex, 0, yuvScratch.length, Math.max(0L, ptsUs), 0);
            drain(false);
            return 0;
        } catch (Throwable error) {
            Log.e(TAG, "encodeRgba failed", error);
            return -3;
        }
    }

    public synchronized int finish() {
        if (released) {
            return 0;
        }
        try {
            if (codec != null) {
                int inputIndex = codec.dequeueInputBuffer(5_000);
                if (inputIndex >= 0) {
                    codec.queueInputBuffer(inputIndex, 0, 0, 0, MediaCodec.BUFFER_FLAG_END_OF_STREAM);
                    drain(true);
                } else {
                    Log.w(TAG, "finish: no input buffer for EOS, releasing codec");
                }
            }
            return 0;
        } catch (Throwable error) {
            Log.e(TAG, "finish failed", error);
            return -3;
        } finally {
            releaseInternal();
        }
    }

    public synchronized void release() {
        releaseInternal();
    }

    private void drain(boolean endOfStream) {
        if (codec == null) {
            return;
        }

        // Bound the wait so a missing EOS cannot freeze the Unity main thread / finalize worker.
        int spins = 0;
        while (spins++ < 48) {
            int outputIndex = codec.dequeueOutputBuffer(bufferInfo, endOfStream ? 3_000 : 0);
            if (outputIndex == MediaCodec.INFO_TRY_AGAIN_LATER) {
                if (!endOfStream) {
                    return;
                }
                continue;
            }
            if (outputIndex == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                if (muxerStarted) {
                    throw new IllegalStateException("format changed twice");
                }
                MediaFormat newFormat = codec.getOutputFormat();
                trackIndex = muxer.addTrack(newFormat);
                muxer.start();
                muxerStarted = true;
                continue;
            }
            if (outputIndex < 0) {
                continue;
            }

            ByteBuffer encoded = codec.getOutputBuffer(outputIndex);
            if (encoded != null && bufferInfo.size > 0 && muxerStarted
                    && (bufferInfo.flags & MediaCodec.BUFFER_FLAG_CODEC_CONFIG) == 0) {
                encoded.position(bufferInfo.offset);
                encoded.limit(bufferInfo.offset + bufferInfo.size);
                muxer.writeSampleData(trackIndex, encoded, bufferInfo);
            }
            codec.releaseOutputBuffer(outputIndex, false);
            if ((bufferInfo.flags & MediaCodec.BUFFER_FLAG_END_OF_STREAM) != 0) {
                return;
            }
        }
        Log.w(TAG, "drain ended without EOS after " + spins + " spins");
    }

    private void releaseInternal() {
        released = true;
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
        if (muxer != null) {
            try {
                if (muxerStarted) {
                    muxer.stop();
                }
            } catch (Throwable ignored) {
            }
            try {
                muxer.release();
            } catch (Throwable ignored) {
            }
            muxer = null;
        }
        muxerStarted = false;
        trackIndex = -1;
        yuvScratch = null;
    }

    private static void rgbaToNv12(byte[] rgba, int length, byte[] nv12,
                                   int sourceWidth, int sourceHeight,
                                   int encodeWidth, int encodeHeight) {
        int framePixels = encodeWidth * encodeHeight;
        int yIndex = 0;
        int uvIndex = framePixels;
        for (int row = 0; row < encodeHeight; row++) {
            for (int col = 0; col < encodeWidth; col++) {
                int r = 0;
                int g = 0;
                int b = 0;
                if (row < sourceHeight && col < sourceWidth) {
                    int i = (row * sourceWidth + col) * 4;
                    if (i + 2 < length) {
                        r = rgba[i] & 0xff;
                        g = rgba[i + 1] & 0xff;
                        b = rgba[i + 2] & 0xff;
                    }
                }
                int y = ((66 * r + 129 * g + 25 * b + 128) >> 8) + 16;
                nv12[yIndex++] = (byte) clamp(y);
                if ((row & 1) == 0 && (col & 1) == 0 && uvIndex + 1 < nv12.length) {
                    int u = ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128;
                    int v = ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128;
                    nv12[uvIndex++] = (byte) clamp(u);
                    nv12[uvIndex++] = (byte) clamp(v);
                }
            }
        }
    }

    private static int clamp(int value) {
        if (value < 0) {
            return 0;
        }
        if (value > 255) {
            return 255;
        }
        return value;
    }
}
