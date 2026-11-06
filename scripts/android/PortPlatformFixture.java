package com.chillasart.theclosingshift.port;

import java.io.File;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.Arrays;

public final class PortPlatformFixture {
    private static int checks;
    private static void check(boolean value) {
        if (!value) throw new AssertionError("Platform check " + checks);
        ++checks;
    }
    public static void main(String[] args) throws Exception {
        int[] widths = {1080,1080,1080,720,1080,1080};
        int[] heights = {2400,2400,2400,1600,2400,2400};
        float[] rates = {120.00001f,90,60.000004f,144,Float.NaN,120};
        int[] ids = {1,2,3,4,5,6};
        check(Arrays.equals(PortDisplay.rates(1,1080,2400,ids,widths,heights,rates),new int[]{30,45,60,90,120,0}));
        check(Arrays.equals(PortDisplay.rates(4,720,1600,ids,widths,heights,rates),new int[]{30,72,144,0}));
        check(Arrays.equals(PortDisplay.rates(1,1,1,new int[]{1},new int[]{1},new int[]{1},new float[]{40}),new int[]{30,40,0}));
        check(Arrays.equals(PortDisplay.rates(1,1,1,new int[0],new int[0],new int[0],new float[0]),new int[]{30,0}));
        check(Arrays.equals(PortDisplay.rates(1,720,1560,new int[]{1},new int[]{1080},new int[]{2340},new float[]{60.000004f}),new int[]{30,60,0}));
        check(Arrays.equals(PortDisplay.rates(99,720,1600,ids,widths,heights,rates),new int[]{30,72,144,0}));
        check(PortDisplay.score(90,45)<PortDisplay.score(60,45));
        check(PortDisplay.score(60,30)<PortDisplay.score(120,30));
        check(PortDisplay.score(120,120)<PortDisplay.score(90,120));
        check(PortDisplay.score(Float.NaN,30)==Float.POSITIVE_INFINITY);
        File root = new File(args[0]);
        File current = SessionLog.rotate(root);
        Files.write(current.toPath(),"first crash evidence".getBytes(StandardCharsets.UTF_8));
        current = SessionLog.rotate(root);
        File previous = new File(root,"logs/closing-shift.1.log"), older = new File(root,"logs/closing-shift.2.log");
        check(new String(Files.readAllBytes(previous.toPath()),StandardCharsets.UTF_8).equals("first crash evidence"));
        Files.write(current.toPath(),"second session".getBytes(StandardCharsets.UTF_8));
        current = SessionLog.rotate(root);
        check(new String(Files.readAllBytes(older.toPath()),StandardCharsets.UTF_8).equals("first crash evidence"));
        check(new String(Files.readAllBytes(previous.toPath()),StandardCharsets.UTF_8).equals("second session"));
        Files.write(current.toPath(),"third session".getBytes(StandardCharsets.UTF_8));
        SessionLog.rotate(root);
        check(new String(Files.readAllBytes(older.toPath()),StandardCharsets.UTF_8).equals("second session"));
        System.out.println("PLATFORM_POLICY_PASS assertions=" + checks);
    }
}
