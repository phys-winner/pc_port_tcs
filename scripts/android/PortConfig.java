package com.chillasart.theclosingshift.port;

import android.content.Context;
import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;

public final class PortConfig {
    private PortConfig() { }

    public static void seed(Context context) {
        File root = context.getExternalFilesDir(null);
        if (root == null) throw new IllegalStateException("App-specific storage is unavailable");
        for (String name : new String[] { "Data", "TheClosingShift" }) {
            File directory = new File(root, name);
            if (!directory.isDirectory() && !directory.mkdirs())
                throw new IllegalStateException("Cannot create game configuration directory");
            File config = new File(directory, "GameConfig.cfg");
            boolean created = false;
            try {
                created = config.createNewFile();
                if (!created) continue;
                try (InputStream input = context.getAssets().open("port-defaults/GameConfig.cfg");
                     FileOutputStream output = new FileOutputStream(config)) {
                    byte[] buffer = new byte[1024];
                    int size;
                    while ((size = input.read(buffer)) != -1) output.write(buffer, 0, size);
                    output.getFD().sync();
                }
            } catch (IOException error) {
                if (created) config.delete();
                throw new IllegalStateException("Cannot seed original game configuration", error);
            }
        }
    }
}
