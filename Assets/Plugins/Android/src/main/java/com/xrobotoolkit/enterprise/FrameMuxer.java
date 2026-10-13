package com.xrobotoolkit.enterprise;

import android.media.MediaCodec;
import android.media.MediaCodecInfo;
import android.media.MediaFormat;
import android.media.MediaMuxer;
import android.util.Log;

import java.io.IOException;
import java.nio.ByteBuffer;

/**
 * Encodes full RGBA frames into an MP4 without cropping.
 * Used by the experimental for4U image path so CameraRecord_*.mp4 keeps the
 * same naming and stop/finalize flow as the capturelib recorder.
 *
 * 2048x1536 RGBA→NV12 on CPU cannot sustain 30fps; start() therefore encodes at
 * half resolution by default (still side-by-side 3D layout).
 */
public final class FrameMuxer {
    private static final String TAG = "FrameMuxer";
    private static final String MIME = "video/avc";
    private static final int DEFAULT_SCALE_NUM = 1;
    private static final int DEFAULT_SCALE_DEN = 2;

    private MediaCodec codec;
    private MediaMuxer muxer;
    private byte[] yuvScratch;
    private int sourceWidth;
    private int sourceHeight;
    private int encodeWidth;
    private int encodeHeight;
    private int scaleNum = DEFAULT_SCALE_NUM;
    private int scaleDen = DEFAULT_SCALE_DEN;
    private int trackIndex = -1;
    private boolean muxerStarted;
    private boolean released;
    private final MediaCodec.BufferInfo bufferInfo = new MediaCodec.BufferInfo();

    public synchronized int start(String path, int width, int height, int bitrate, int fps) {
        return startScaled(path, width, height, bitrate, fps, DEFAULT_SCALE_NUM, DEFAULT_SCALE_DEN);
    }

    /**
     * @param scaleNum / scaleDen downsample factors (e.g. 1/2 = half resolution).
     */
    public synchronized int startScaled(String path, int width, int height, int bitrate, int fps,
                                        int scaleNum, int scaleDen) {
        releaseInternal();
        if (path == null || path.length() == 0 || width <= 0 || height <= 0) {
            return -1;
        }
        if (scaleNum <= 0 || scaleDen <= 0) {
            scaleNum = DEFAULT_SCALE_NUM;
            scaleDen = DEFAULT_SCALE_DEN;
        }

        this.scaleNum = scaleNum;
        this.scaleDen = scaleDen;
        sourceWidth = width;
        sourceHeight = height;
        int targetW = Math.max(2, (width * scaleNum) / scaleDen);
        int targetH = Math.max(2, (height * scaleNum) / scaleDen);
        encodeWidth = (targetW + 1) & ~1;
        encodeHeight = (targetH + 1) & ~1;

        int safeFps = fps > 0 ? fps : 30;
        int safeBitrate = bitrate > 0 ? bitrate : Math.max(4 * 1024 * 1024, encodeWidth * encodeHeight * 4);
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
                    + " scale=" + scaleNum + "/" + scaleDen
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
            // Allow enough time for the previous frame to free an input slot.
            int inputIndex = codec.dequeueInputBuffer(50_000);
            if (inputIndex < 0) {
                return -4;
            }

            rgbaToNv12Scaled(rgba, length, yuvScratch,
                    sourceWidth, sourceHeight, encodeWidth, encodeHeight, scaleNum, scaleDen);
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

    /**
     * Nearest-neighbor downsample + BT.601 RGBA→NV12 in one pass.
     */
    private static void rgbaToNv12Scaled(byte[] rgba, int length, byte[] nv12,
                                         int sourceWidth, int sourceHeight,
                                         int encodeWidth, int encodeHeight,
                                         int scaleNum, int scaleDen) {
        int framePixels = encodeWidth * encodeHeight;
        int yIndex = 0;
        int uvIndex = framePixels;
        for (int row = 0; row < encodeHeight; row++) {
            int srcRow = Math.min(sourceHeight - 1, (row * scaleDen) / scaleNum);
            int srcRowBase = srcRow * sourceWidth;
            for (int col = 0; col < encodeWidth; col++) {
                int srcCol = Math.min(sourceWidth - 1, (col * scaleDen) / scaleNum);
                int i = (srcRowBase + srcCol) * 4;
                int r = 0;
                int g = 0;
                int b = 0;
                if (i + 2 < length) {
                    r = rgba[i] & 0xff;
                    g = rgba[i + 1] & 0xff;
                    b = rgba[i + 2] & 0xff;
                }
                int y = ((66 * r + 129 * g + 25 * b + 128) >> 8) + 16;
                if (y < 0) {
                    y = 0;
                } else if (y > 255) {
                    y = 255;
                }
                nv12[yIndex++] = (byte) y;
                if ((row & 1) == 0 && (col & 1) == 0 && uvIndex + 1 < nv12.length) {
                    int u = ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128;
                    int v = ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128;
                    if (u < 0) {
                        u = 0;
                    } else if (u > 255) {
                        u = 255;
                    }
                    if (v < 0) {
                        v = 0;
                    } else if (v > 255) {
                        v = 255;
                    }
                    nv12[uvIndex++] = (byte) u;
                    nv12[uvIndex++] = (byte) v;
                }
            }
        }
    }
}
