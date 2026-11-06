package com.chillasart.theclosingshift.port;

import android.content.Context;
import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;

/** Game saves use the app-specific external files directory without storage permissions. */
public final class PortStorage {
    private static String root;
    private PortStorage() { }

    public static boolean resumeAfterCreation(boolean resumed, boolean started, boolean multiWindow) {
        return resumed || (started && multiWindow);
    }

    static File prepareDirectory(File directory) throws IOException {
        if (directory == null) throw new IOException("App-specific external storage is unavailable");
        if (!directory.isDirectory() && !directory.mkdirs()) throw new IOException("Cannot create save directory");
        File probe = File.createTempFile(".write-check-", ".tmp", directory);
        try (FileOutputStream output = new FileOutputStream(probe)) { output.write(1); output.getFD().sync(); }
        finally { if (!probe.delete()) throw new IOException("Cannot finish save directory write check"); }
        return directory;
    }

    public static void prepare(Context context) throws IOException {
        root = prepareDirectory(context.getExternalFilesDir(null)).getAbsolutePath();
    }

    public static String saveRoot() {
        if (root == null) throw new IllegalStateException("Save storage must be prepared before Unity starts");
        return root;
    }
}
