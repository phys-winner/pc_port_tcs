package com.chillasart.theclosingshift.port;

import java.io.File;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.Arrays;

public final class PortStorageFixture {
    private static int checks;
    private static void check(boolean value) {
        if (!value) throw new AssertionError("Storage check " + checks);
        ++checks;
    }
    public static void main(String[] args) throws Exception {
        File test = new File(args[0]);
        check(test.mkdirs());
        check(!PortStorage.resumeAfterCreation(false,false,false));
        check(!PortStorage.resumeAfterCreation(false,false,true));
        check(PortStorage.resumeAfterCreation(true,true,false));
        check(PortStorage.resumeAfterCreation(false,true,true));
        check(!PortStorage.resumeAfterCreation(false,true,false));
        try { PortStorage.saveRoot(); throw new AssertionError("Unprepared storage accepted"); }
        catch (IllegalStateException expected) { ++checks; }
        File old = new File(test, "Documents/TheClosingShift/userdata/SaveFile.es3");
        check(old.getParentFile().mkdirs());
        Files.write(old.toPath(), "old progress".getBytes(StandardCharsets.UTF_8));
        File files = new File(test, "Android/data/game/files");
        File root = PortStorage.prepareDirectory(files);
        check(root.getCanonicalPath().equals(files.getCanonicalPath()));
        check(root.list().length == 0 && old.isFile());
        File save = new File(root, "SaveFile.es3");
        byte[] original = "existing app progress".getBytes(StandardCharsets.UTF_8);
        Files.write(save.toPath(), original);
        check(PortStorage.prepareDirectory(files).equals(root) && Arrays.equals(Files.readAllBytes(save.toPath()), original));
        check(root.list().length == 1);
        File blocked = new File(test,"blocked");
        Files.write(blocked.toPath(),new byte[]{1});
        try { PortStorage.prepareDirectory(blocked); throw new AssertionError("Non-directory storage accepted"); }
        catch (IOException expected) { ++checks; }
        try { PortStorage.prepareDirectory(null); throw new AssertionError("Unavailable external storage accepted"); }
        catch (IOException expected) { ++checks; }
        System.out.println("STORAGE_POLICY_PASS assertions=" + checks);
    }
}
