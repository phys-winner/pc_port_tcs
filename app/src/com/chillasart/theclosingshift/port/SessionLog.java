package com.chillasart.theclosingshift.port;

import android.app.ActivityManager;
import android.app.ApplicationExitInfo;
import android.content.Context;
import android.os.Build;
import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.util.List;

public final class SessionLog {
    private static boolean begun;
    private static File current;
    private SessionLog() { }

    static File rotate(File root) throws IOException {
        File directory = new File(root, "logs");
        if (!directory.isDirectory() && !directory.mkdirs()) throw new IOException("Cannot create session log directory");
        File log = new File(directory, "closing-shift.log");
        File previous = new File(directory, "closing-shift.1.log"), older = new File(directory, "closing-shift.2.log");
        if (previous.exists()) {
            if (older.exists() && !older.delete()) throw new IOException("Cannot rotate oldest session log");
            if (!previous.renameTo(older)) throw new IOException("Cannot rotate previous session log");
        }
        if (log.exists() && !log.renameTo(previous)) throw new IOException("Cannot preserve previous session log");
        return log;
    }

    public static synchronized void begin(Context context) {
        if (begun) return;
        begun = true;
        File root = context.getExternalFilesDir(null);
        if (root == null) return;
        try {
            current = rotate(root);
            File previous = new File(root, "logs/closing-shift.1.log");
            String exit = previousExit(context);
            if (exit != null && previous.exists()) append(previous, exit);
            append(current, "[session] The Closing Shift; model=" + Build.MODEL + "; Android=" + Build.VERSION.RELEASE +
                "; API=" + Build.VERSION.SDK_INT + "; startedUTC=" + System.currentTimeMillis());
            if (exit != null) append(current, exit);
            Thread.UncaughtExceptionHandler fallback = Thread.getDefaultUncaughtExceptionHandler();
            Thread.setDefaultUncaughtExceptionHandler((thread, error) -> {
                write("[java] " + android.util.Log.getStackTraceString(error));
                if (fallback != null) fallback.uncaughtException(thread, error);
                else { android.os.Process.killProcess(android.os.Process.myPid()); System.exit(10); }
            });
        } catch (IOException | RuntimeException error) {
            android.util.Log.w("Unity", "PORT_LOG session log unavailable", error);
        }
    }

    private static String previousExit(Context context) {
        if (Build.VERSION.SDK_INT < 30) return null;
        try {
            ActivityManager manager = (ActivityManager)context.getSystemService(Context.ACTIVITY_SERVICE);
            if (manager == null) return null;
            List<ApplicationExitInfo> exits = manager.getHistoricalProcessExitReasons(context.getPackageName(), 0, 16);
            for (ApplicationExitInfo exit : exits)
                if (context.getPackageName().equals(exit.getProcessName()))
                    return "[exit] reason=" + exit.getReason() + "; status=" + exit.getStatus() + "; timestamp=" + exit.getTimestamp() +
                        "; pssKiB=" + exit.getPss() + "; rssKiB=" + exit.getRss();
        } catch (RuntimeException error) {
            return "[exit] Android exit information unavailable: " + error.getClass().getSimpleName();
        }
        return null;
    }

    private static void append(File file, String text) throws IOException {
        try (FileOutputStream output = new FileOutputStream(file, true)) {
            output.write((text + "\n").getBytes(StandardCharsets.UTF_8));
        }
    }

    public static synchronized void write(String text) {
        if (current == null) return;
        try { append(current, text); }
        catch (IOException error) { android.util.Log.w("Unity", "PORT_LOG cannot append session event"); }
    }
}
