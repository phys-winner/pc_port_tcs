package com.chillasart.tools;

import android.app.Instrumentation;
import android.content.Intent;
import android.os.Bundle;
import android.os.SystemClock;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.nio.charset.StandardCharsets;

/** Same-signature QA instrumentation; changes only the live FPS array, without saving preferences. */
public final class PortAudit extends Instrumentation {
    private int fps, seconds;
    private static native boolean frameRate(int value);
    static boolean hasManagedFrame(File file, long since) throws IOException {
        if (!file.isFile() || file.lastModified() < since) return false;
        byte[] bytes = new byte[65536];
        int length = 0, count;
        try (FileInputStream input = new FileInputStream(file)) {
            while (length < bytes.length && (count = input.read(bytes, length, bytes.length - length)) > 0) length += count;
        }
        return new String(bytes, 0, length, StandardCharsets.UTF_8).contains("PORT_TOUCH_METRICS source=");
    }
    public static void main(String[] args) throws Exception {
        if (args.length != 2 || !args[0].equals("--self-test")) throw new IllegalArgumentException("Expected --self-test DIRECTORY");
        File directory = new File(args[1]);
        if (!directory.mkdirs()) throw new IOException("Use a new readiness fixture directory");
        File log = new File(directory, "ready.log");
        if (hasManagedFrame(log, 0)) throw new AssertionError("Missing log accepted");
        try (FileOutputStream output = new FileOutputStream(log)) { output.write("[engine] library loaded".getBytes(StandardCharsets.UTF_8)); }
        if (hasManagedFrame(log, 0)) throw new AssertionError("Library load accepted before managed frame");
        try (FileOutputStream output = new FileOutputStream(log)) { output.write("PORT_TOUCH_METRICS source=AndroidDisplayMetrics".getBytes(StandardCharsets.UTF_8)); }
        if (!hasManagedFrame(log, 0)) throw new AssertionError("Managed frame refused");
        if (hasManagedFrame(log, System.currentTimeMillis() + 60000)) throw new AssertionError("Stale session accepted");
        System.out.println("QA_READINESS_PASS cases=4");
    }
    @Override public void onCreate(Bundle arguments) {
        fps = Integer.parseInt(arguments.getString("fps", "6"));
        seconds = Integer.parseInt(arguments.getString("seconds", "180"));
        if (fps < 5 || fps > 8 || seconds < 30 || seconds > 300) throw new IllegalArgumentException("QA needs fps 5..8 and seconds 30..300");
        start();
    }
    @Override public void onStart() {
        Bundle result = new Bundle();
        boolean changed = false;
        try {
            System.load(new File(getContext().getApplicationInfo().nativeLibraryDir, "libportaudit.so").getAbsolutePath());
            Intent launch = new Intent().setClassName(getTargetContext(), "com.unity3d.player.UnityPlayerActivity").addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
            long launched = System.currentTimeMillis();
            startActivitySync(launch);
            long deadline = SystemClock.uptimeMillis() + 60000;
            File log = new File(getTargetContext().getExternalFilesDir(null), "logs/closing-shift.log");
            // dlopen can succeed while Unity is still initializing its IL2CPP domain.
            while (!hasManagedFrame(log, launched) || !(changed = frameRate(fps))) {
                if (SystemClock.uptimeMillis() > deadline) throw new IllegalStateException("Unity IL2CPP FPS fields did not initialize");
                SystemClock.sleep(250);
            }
            result.putString("state", "QA_READY fps=" + fps + "; live fields only; preferences unchanged");
            sendStatus(1, result);
            SystemClock.sleep(seconds * 1000L);
            result.putString("state", "QA_RESTORED");
        } catch (Throwable error) {
            result.putString("failure", error.toString());
        } finally {
            if (changed) frameRate(0);
            finish(result.containsKey("failure") ? 1 : -1, result);
        }
    }
}
