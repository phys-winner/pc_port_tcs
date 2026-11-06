package com.chillasart.tools;

import android.app.Instrumentation;
import android.os.Bundle;
import android.util.Base64;
import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import org.json.JSONArray;
import org.json.JSONObject;

/** Same-signature QA backup/restore; does not start Unity or migrate game files. */
public final class StorageProbe extends Instrumentation {
    private Bundle arguments;
    @Override public void onCreate(Bundle args) { arguments = args; start(); }
    private static byte[] read(File file) throws IOException {
        if (file.length() > 8 * 1024 * 1024) throw new IOException("Preference backup is too large");
        try (FileInputStream input = new FileInputStream(file); ByteArrayOutputStream output = new ByteArrayOutputStream()) {
            byte[] buffer = new byte[8192]; int n;
            while ((n = input.read(buffer)) != -1) {
                output.write(buffer, 0, n);
                if (output.size() > 8 * 1024 * 1024) throw new IOException("Preference backup grew too large");
            }
            return output.toByteArray();
        }
    }
    private static File checked(File directory, String name) throws IOException {
        if (name.length() > 255 || name.contains("..") || !name.matches("[A-Za-z0-9_.-]+\\.xml(\\.bak)?"))
            throw new IOException("Invalid preference filename");
        File file = new File(directory, name);
        if (!file.getCanonicalPath().startsWith(directory.getCanonicalPath() + File.separator)) throw new IOException("Preference file leaves app directory");
        return file;
    }
    public static void main(String[] args) throws Exception {
        if (args.length != 2 || !args[0].equals("--self-test")) throw new IllegalArgumentException("Expected --self-test DIRECTORY");
        File directory = new File(args[1]);
        if (!directory.mkdirs()) throw new IOException("Use a new backup fixture directory");
        int checks = 0;
        for (String name : new String[] { "game.v2.playerprefs.xml", "game.xml.bak" }) {
            if (!checked(directory,name).getParentFile().equals(directory)) throw new AssertionError("Wrong preference parent");
            ++checks;
        }
        for (String name : new String[] { "../game.xml", "sub/game.xml", "..xml", "game.bin" }) {
            try { checked(directory,name); throw new AssertionError("Invalid name accepted: " + name); }
            catch (IOException expected) { ++checks; }
        }
        File file = checked(directory,"roundtrip.xml");
        try (FileOutputStream output = new FileOutputStream(file)) { output.write(new byte[]{1,2,3}); }
        if (!java.util.Arrays.equals(read(file),new byte[]{1,2,3})) throw new AssertionError("Backup bytes changed");
        ++checks;
        File oversized = new File(directory,"oversized.xml") { @Override public long length() { return 8L*1024*1024+1; } };
        try { read(oversized); throw new AssertionError("Oversized backup accepted"); }
        catch (IOException expected) { ++checks; }
        System.out.println("STORAGE_BACKUP_PASS assertions=" + checks);
    }
    @Override public void onStart() {
        Bundle result = new Bundle();
        try {
            File directory = new File(getTargetContext().getApplicationInfo().dataDir, "shared_prefs");
            String operation = arguments.getString("operation", "backup");
            if (operation.equals("backup")) {
                JSONArray entries = new JSONArray();
                File[] files = directory.listFiles();
                if (files != null) for (File file : files) {
                    if (!file.getName().endsWith(".xml") && !file.getName().endsWith(".xml.bak")) continue;
                    file = checked(directory, file.getName());
                    JSONObject item = new JSONObject(); item.put("name", file.getName());
                    item.put("data", Base64.encodeToString(read(file), Base64.NO_WRAP)); entries.put(item);
                }
                result.putString("snapshot", Base64.encodeToString(entries.toString().getBytes(StandardCharsets.UTF_8), Base64.NO_WRAP));
                result.putString("state", "PREFERENCES_BACKED_UP files=" + entries.length());
            } else if (operation.equals("restore")) {
                JSONArray entries = new JSONArray(new String(Base64.decode(arguments.getString("snapshot"), Base64.DEFAULT), StandardCharsets.UTF_8));
                if (entries.length() > 128) throw new IOException("Too many preference files");
                if (!directory.isDirectory() && !directory.mkdirs()) throw new IOException("Cannot create preference directory");
                for (int i = 0; i < entries.length(); ++i) {
                    JSONObject item = entries.getJSONObject(i); File file = checked(directory, item.getString("name"));
                    byte[] data = Base64.decode(item.getString("data"), Base64.DEFAULT);
                    if (data.length > 8 * 1024 * 1024) throw new IOException("Preference file is too large");
                    try (FileOutputStream output = new FileOutputStream(file)) { output.write(data); output.getFD().sync(); }
                }
                result.putString("state", "PREFERENCES_RESTORED files=" + entries.length());
            } else throw new IllegalArgumentException("Unknown storage probe operation");
            finish(-1, result);
        } catch (Throwable error) { result.putString("failure", error.toString()); finish(1, result); }
    }
}
