package com.xrobotoolkit.enterprise;

import android.graphics.Bitmap;
import android.util.Log;

import java.io.FileOutputStream;

/**
 * Compresses an RGBA8888 buffer to a JPEG file on a worker thread.
 * Used by for4U ApiFrameSession so the headset stores frames without MediaCodec.
 */
public final class FrameJpegWriter {
    private static final String TAG = "FrameJpegWriter";

    private FrameJpegWriter() {
    }

    /**
     * @param rgba   packed R,G,B,A bytes, length >= width*height*4
     * @param quality JPEG quality 1..100
     * @return 0 on success, negative on failure
     */
    public static int writeRgbaJpeg(String path, byte[] rgba, int length, int width, int height,
                                    int quality) {
        if (path == null || path.length() == 0 || rgba == null || width <= 0 || height <= 0) {
            return -1;
        }
        int need = width * height * 4;
        if (length < need) {
            Log.w(TAG, "short buffer length=" + length + " need=" + need);
            return -2;
        }
        int q = quality;
        if (q < 1) {
            q = 1;
        } else if (q > 100) {
            q = 100;
        }

        Bitmap bitmap = null;
        FileOutputStream out = null;
        try {
            int[] pixels = new int[width * height];
            int pi = 0;
            for (int i = 0; i < need; i += 4) {
                int r = rgba[i] & 0xff;
                int g = rgba[i + 1] & 0xff;
                int b = rgba[i + 2] & 0xff;
                int a = rgba[i + 3] & 0xff;
                pixels[pi++] = (a << 24) | (r << 16) | (g << 8) | b;
            }
            bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888);
            bitmap.setPixels(pixels, 0, width, 0, 0, width, height);
            out = new FileOutputStream(path);
            if (!bitmap.compress(Bitmap.CompressFormat.JPEG, q, out)) {
                return -3;
            }
            out.flush();
            return 0;
        } catch (Throwable error) {
            Log.e(TAG, "writeRgbaJpeg failed path=" + path, error);
            return -4;
        } finally {
            if (out != null) {
                try {
                    out.close();
                } catch (Throwable ignored) {
                }
            }
            if (bitmap != null) {
                bitmap.recycle();
            }
        }
    }
}
