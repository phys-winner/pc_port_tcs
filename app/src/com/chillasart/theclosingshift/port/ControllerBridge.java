package com.chillasart.theclosingshift.port;

import android.app.Activity;
import android.content.Context;
import android.hardware.input.InputManager;
import android.os.Build;
import android.util.Log;
import android.view.InputDevice;
import android.view.KeyEvent;
import android.view.MotionEvent;
import java.lang.reflect.Proxy;

/** Android semantic controller events, captured once before Unity's legacy joystick mapper. */
public final class ControllerBridge implements InputManager.InputDeviceListener {
    private static final ControllerBridge INSTANCE = new ControllerBridge();
    private static int device = -1, buttons, presses, generation, backs, sequence;
    private static float lx, ly, rx, ry, hx, hy, lt, rt, moveFlat, lookFlat;
    private static boolean leftTriggerHeld, rightTriggerHeld;
    private static Object backCallback;
    private ControllerBridge() {}

    public static void install(Activity activity) {
        InputManager manager = (InputManager)activity.getSystemService(Context.INPUT_SERVICE);
        if (manager != null) manager.registerInputDeviceListener(INSTANCE, null);
        for (int id : InputDevice.getDeviceIds()) {
            InputDevice candidate = InputDevice.getDevice(id);
            if (isPad(candidate)) { select(candidate); break; }
        }
        if (Build.VERSION.SDK_INT >= 33) {
            try {
                Class<?> callback = Class.forName("android.window.OnBackInvokedCallback");
                backCallback = Proxy.newProxyInstance(callback.getClassLoader(), new Class<?>[]{callback},
                    (proxy, method, args) -> {
                        if (method.getName().equals("onBackInvoked")) { back(); return null; }
                        if (method.getName().equals("hashCode")) return System.identityHashCode(proxy);
                        if (method.getName().equals("equals")) return args != null && args.length == 1 && proxy == args[0];
                        if (method.getName().equals("toString")) return "ClosingShiftBackCallback";
                        return null;
                    });
                Object dispatcher = Activity.class.getMethod("getOnBackInvokedDispatcher").invoke(activity);
                Class.forName("android.window.OnBackInvokedDispatcher").getMethod("registerOnBackInvokedCallback", int.class, callback).invoke(dispatcher, 0, backCallback);
            } catch (ReflectiveOperationException | RuntimeException error) {
                Log.i("PortController", "Predictive callback unavailable; legacy Back handler remains", error);
            }
        }
    }

    private static boolean isPad(InputDevice value) {
        return value != null && ((value.getSources() & InputDevice.SOURCE_GAMEPAD) == InputDevice.SOURCE_GAMEPAD ||
            (value.getSources() & InputDevice.SOURCE_JOYSTICK) == InputDevice.SOURCE_JOYSTICK);
    }
    private static synchronized void select(InputDevice value) {
        if (value == null || value.getId() == device) return;
        clear(); device = value.getId(); ++generation;
        moveFlat = Math.max(flat(value, MotionEvent.AXIS_X), flat(value, MotionEvent.AXIS_Y));
        lookFlat = Math.max(flat(value, rightX(value)), flat(value, rightY(value)));
        Log.i("PortController", "Selected id=" + device + " name=" + value.getName() + " descriptor=" + value.getDescriptor() +
            " vendor=" + value.getVendorId() + " product=" + value.getProductId() + " rightAxes=" + rightX(value) + "," + rightY(value));
    }
    private static float flat(InputDevice d, int axis) { InputDevice.MotionRange range = d.getMotionRange(axis, InputDevice.SOURCE_JOYSTICK); return range == null ? 0 : range.getFlat(); }
    private static int rightX(InputDevice d) { return d.getMotionRange(MotionEvent.AXIS_Z) != null && d.getMotionRange(MotionEvent.AXIS_RZ) != null ? MotionEvent.AXIS_Z : MotionEvent.AXIS_RX; }
    private static int rightY(InputDevice d) { return rightX(d) == MotionEvent.AXIS_Z ? MotionEvent.AXIS_RZ : MotionEvent.AXIS_RY; }
    private static void clear() { buttons = presses = 0; lx = ly = rx = ry = hx = hy = lt = rt = 0; leftTriggerHeld = rightTriggerHeld = false; }
    // Keep raw held positions so C# can demand a real release/neutral after focus loss.
    // Pending actions are discarded, and C# releases every virtual source immediately.
    public static synchronized void cancel() { presses = 0; ++generation; }
    public static synchronized void back() { backs = (backs + 1) & 0xfffff; }

    public static synchronized boolean motion(MotionEvent event) {
        InputDevice d = event.getDevice();
        if (!isPad(d) || (event.getSource() & InputDevice.SOURCE_JOYSTICK) != InputDevice.SOURCE_JOYSTICK) return false;
        float x = event.getAxisValue(MotionEvent.AXIS_X), y = event.getAxisValue(MotionEvent.AXIS_Y);
        float z = event.getAxisValue(rightX(d)), r = event.getAxisValue(rightY(d));
        float hatX = event.getAxisValue(MotionEvent.AXIS_HAT_X), hatY = event.getAxisValue(MotionEvent.AXIS_HAT_Y);
        float left = Math.max(trigger(d,event,MotionEvent.AXIS_LTRIGGER),trigger(d,event,MotionEvent.AXIS_BRAKE));
        float right = Math.max(trigger(d,event,MotionEvent.AXIS_RTRIGGER),trigger(d,event,MotionEvent.AXIS_GAS));
        if (device != d.getId() && (device < 0 || Math.abs(x) + Math.abs(y) + Math.abs(z) + Math.abs(r) + Math.abs(hatX) + Math.abs(hatY) + left + right > .25f)) select(d);
        if (device == d.getId()) {
            lx = x; ly = y; rx = z; ry = r;
            for (int sample = 0; sample < event.getHistorySize(); ++sample) {
                sampleHat(event.getHistoricalAxisValue(MotionEvent.AXIS_HAT_X,sample),event.getHistoricalAxisValue(MotionEvent.AXIS_HAT_Y,sample));
                sampleTriggers(Math.max(triggerValue(d,event.getHistoricalAxisValue(MotionEvent.AXIS_LTRIGGER,sample),MotionEvent.AXIS_LTRIGGER),triggerValue(d,event.getHistoricalAxisValue(MotionEvent.AXIS_BRAKE,sample),MotionEvent.AXIS_BRAKE)),
                    Math.max(triggerValue(d,event.getHistoricalAxisValue(MotionEvent.AXIS_RTRIGGER,sample),MotionEvent.AXIS_RTRIGGER),triggerValue(d,event.getHistoricalAxisValue(MotionEvent.AXIS_GAS,sample),MotionEvent.AXIS_GAS)));
            }
            sampleHat(hatX,hatY);
            lt = left; rt = right;
            sampleTriggers(lt,rt);
            ++sequence;
        }
        return true;
    }
    private static float trigger(InputDevice d, MotionEvent event, int axis) {
        return triggerValue(d,event.getAxisValue(axis),axis);
    }
    private static float triggerValue(InputDevice d, float raw, int axis) {
        InputDevice.MotionRange range = d.getMotionRange(axis);
        if (range == null || range.getMax() <= range.getMin()) return 0;
        float value = (raw - range.getMin()) / (range.getMax() - range.getMin());
        return Math.max(0, Math.min(1, value));
    }
    private static void sampleTriggers(float left, float right) {
        boolean leftNow = left >= (leftTriggerHeld ? .35f : .55f);
        boolean rightNow = right >= (rightTriggerHeld ? .35f : .55f);
        if (leftNow && !leftTriggerHeld) presses |= 1 << 15;
        if (rightNow && !rightTriggerHeld) presses |= 1 << 16;
        leftTriggerHeld = leftNow; rightTriggerHeld = rightNow;
    }
    private static int hats(float x,float y) { return (y<-.5f?1<<11:0)|(y>.5f?1<<12:0)|(x<-.5f?1<<13:0)|(x>.5f?1<<14:0); }
    private static void sampleHat(float x,float y) { presses |= hats(x,y) & ~hats(hx,hy); hx=x;hy=y; }
    private static int button(int key) {
        switch (key) {
            case KeyEvent.KEYCODE_BUTTON_A: return 0;
            case KeyEvent.KEYCODE_BUTTON_B: return 1;
            case KeyEvent.KEYCODE_BUTTON_X: return 2;
            case KeyEvent.KEYCODE_BUTTON_Y: return 3;
            case KeyEvent.KEYCODE_BUTTON_L1: return 4;
            case KeyEvent.KEYCODE_BUTTON_R1: return 5;
            case KeyEvent.KEYCODE_BUTTON_THUMBL: return 6;
            case KeyEvent.KEYCODE_BUTTON_THUMBR: return 7;
            case KeyEvent.KEYCODE_BUTTON_START: return 8;
            case KeyEvent.KEYCODE_BUTTON_SELECT: return 9;
            case KeyEvent.KEYCODE_DPAD_UP: return 11;
            case KeyEvent.KEYCODE_DPAD_DOWN: return 12;
            case KeyEvent.KEYCODE_DPAD_LEFT: return 13;
            case KeyEvent.KEYCODE_DPAD_RIGHT: return 14;
            case KeyEvent.KEYCODE_BUTTON_L2: return 15;
            case KeyEvent.KEYCODE_BUTTON_R2: return 16;
            case KeyEvent.KEYCODE_BACK: return 1;
            default: return -1;
        }
    }
    public static synchronized boolean key(KeyEvent event) {
        if (!isPad(event.getDevice())) return false;
        int index = button(event.getKeyCode());
        if (index < 0) return false;
        if (event.getAction() == KeyEvent.ACTION_DOWN) select(event.getDevice());
        if (event.getDeviceId() == device) {
            int bit = 1 << index;
            if (event.getAction() == KeyEvent.ACTION_DOWN) { if ((buttons & bit) == 0) presses |= bit; buttons |= bit; }
            else if (event.getAction() == KeyEvent.ACTION_UP) buttons &= ~bit;
            ++sequence;
        }
        return true;
    }
    public static synchronized float[] poll() {
        float[] result = {sequence & 0xfffff, device, device >= 0 ? 1 : 0, lx, ly, rx, ry, hx, hy, lt, rt,
            buttons, backs, generation & 0xfffff, moveFlat, lookFlat, presses};
        presses = 0;
        return result;
    }
    public void onInputDeviceAdded(int id) { synchronized (ControllerBridge.class) { if (device < 0 && isPad(InputDevice.getDevice(id))) select(InputDevice.getDevice(id)); } }
    public void onInputDeviceChanged(int id) { synchronized (ControllerBridge.class) { if (id == device) { clear(); device = -1; ++generation; InputDevice changed = InputDevice.getDevice(id); if (isPad(changed)) select(changed); } } }
    public void onInputDeviceRemoved(int id) { synchronized (ControllerBridge.class) { if (id == device) { clear(); device = -1; ++generation; } } }
}
