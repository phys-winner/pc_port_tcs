package com.chillasart.theclosingshift.port;

import android.app.Activity;
import android.view.Display;
import android.view.WindowManager;
import java.util.TreeSet;

public final class PortDisplay {
    private static Activity activity;
    private PortDisplay() { }

    public static void install(Activity owner) { activity = owner; }

    public static void haptic() {
        Activity owner = activity;
        if (owner != null) owner.runOnUiThread(() -> owner.getWindow().getDecorView().performHapticFeedback(android.view.HapticFeedbackConstants.KEYBOARD_TAP));
    }

    static int[] rates(int activeId, int width, int height, int[] ids, int[] widths, int[] heights, float[] refresh) {
        // Huawei can scale getMode() dimensions with wm size while supported modes stay physical.
        for (int i = 0; i < ids.length; ++i) if (ids[i] == activeId) {
            width = widths[i]; height = heights[i]; break;
        }
        TreeSet<Integer> values = new TreeSet<>();
        values.add(30);
        for (int i = 0; i < refresh.length; ++i) {
            if (widths[i] != width || heights[i] != height || Float.isNaN(refresh[i]) || Float.isInfinite(refresh[i])) continue;
            int rate = Math.round(refresh[i]);
            if (rate < 30 || rate > 240) continue;
            values.add(rate);
            if (rate % 2 == 0 && rate / 2 >= 30) values.add(rate / 2);
        }
        int[] result = new int[values.size() + 1];
        int index = 0;
        for (int value : values) result[index++] = value;
        return result;
    }

    public static int[] frameRates() {
        Display display = activity.getWindowManager().getDefaultDisplay();
        Display.Mode active = display.getMode();
        Display.Mode[] modes = display.getSupportedModes();
        int[] ids = new int[modes.length], widths = new int[modes.length], heights = new int[modes.length];
        float[] refresh = new float[modes.length];
        for (int i = 0; i < modes.length; ++i) {
            ids[i] = modes[i].getModeId();
            widths[i] = modes[i].getPhysicalWidth(); heights[i] = modes[i].getPhysicalHeight();
            refresh[i] = modes[i].getRefreshRate();
        }
        return rates(active.getModeId(), active.getPhysicalWidth(), active.getPhysicalHeight(), ids, widths, heights, refresh);
    }

    static float score(float rate, int cap) {
        if (Float.isNaN(rate) || Float.isInfinite(rate) || rate < 1 || cap < 1) return Float.POSITIVE_INFINITY;
        float multiple = rate / cap;
        return rate + (multiple >= .99f && Math.abs(multiple - Math.round(multiple)) < .02f ? 0 : 1000);
    }

    public static void setFrameRate(int cap) {
        Activity owner = activity;
        if (owner == null) return;
        owner.runOnUiThread(() -> {
            Display display = owner.getWindowManager().getDefaultDisplay();
            Display.Mode active = display.getMode();
            Display.Mode[] modes = display.getSupportedModes();
            for (Display.Mode mode : modes) if (mode.getModeId() == active.getModeId()) { active = mode; break; }
            Display.Mode best = active;
            float bestScore = Float.POSITIVE_INFINITY;
            for (Display.Mode mode : modes) {
                if (mode.getPhysicalWidth() != active.getPhysicalWidth() || mode.getPhysicalHeight() != active.getPhysicalHeight()) continue;
                float candidate = score(mode.getRefreshRate(), cap);
                if (candidate < bestScore) { best = mode; bestScore = candidate; }
            }
            WindowManager.LayoutParams attributes = owner.getWindow().getAttributes();
            attributes.preferredDisplayModeId = best.getModeId();
            attributes.preferredRefreshRate = best.getRefreshRate();
            owner.getWindow().setAttributes(attributes);
            android.util.Log.i("Unity", "PORT_DISPLAY cap=" + cap + " requestedHz=" + best.getRefreshRate());
        });
    }
}
