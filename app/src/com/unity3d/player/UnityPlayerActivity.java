package com.unity3d.player;

import android.app.Activity;
import android.app.ActivityManager;
import android.content.Intent;
import android.content.res.Configuration;
import android.graphics.PixelFormat;
import android.os.Bundle;
import android.view.KeyEvent;
import android.view.MotionEvent;
import android.view.View;
import android.view.Window;
import android.view.WindowManager;
import android.os.Process;

public class UnityPlayerActivity extends Activity implements IUnityPlayerLifecycleEvents
{
    protected UnityPlayer mUnityPlayer; // don't change the name of this variable; referenced from native code
    private android.app.AlertDialog storageDialog;
    private boolean activityStarted, activityResumed;

    // Override this in your custom UnityPlayerActivity to tweak the command line arguments passed to the Unity Android Player
    // The command line arguments are passed as a string, separated by spaces
    // UnityPlayerActivity calls this from 'onCreate'
    // Supported: -force-gles20, -force-gles30, -force-gles31, -force-gles31aep, -force-gles32, -force-gles, -force-vulkan
    // See https://docs.unity3d.com/Manual/CommandLineArguments.html
    // @param cmdLine the current command line arguments, may be null
    // @return the modified command line string or null
    protected String updateUnityCommandLineArguments(String cmdLine)
    {
        ActivityManager manager = (ActivityManager)getSystemService(ACTIVITY_SERVICE);
        ActivityManager.MemoryInfo memory = new ActivityManager.MemoryInfo();
        if (manager != null) manager.getMemoryInfo(memory);
        // Bound rendering queues and CPU parallelism on low-memory phones.
        String arguments = com.chillasart.theclosingshift.port.PortConfig.startupArguments(cmdLine, memory.totalMem);
        if (arguments != cmdLine) android.util.Log.i("Unity", "PORT_CPU lowMemoryMB=" + memory.totalMem / 1048576);
        return arguments;
    }

    // Preserve the app's cutout policy when Unity updates other window attributes.
    @Override public void onWindowAttributesChanged(WindowManager.LayoutParams attributes)
    {
        if (android.os.Build.VERSION.SDK_INT >= 28)
            attributes.layoutInDisplayCutoutMode = WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES;
        super.onWindowAttributesChanged(attributes);
    }

    // Setup activity layout
    private void hideSystemUi()
    {
        View decor = getWindow().getDecorView();
        decor.setSystemUiVisibility(View.SYSTEM_UI_FLAG_HIDE_NAVIGATION | View.SYSTEM_UI_FLAG_FULLSCREEN |
            View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY | View.SYSTEM_UI_FLAG_LAYOUT_STABLE |
            View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN);
        if (android.os.Build.VERSION.SDK_INT >= 29) decor.setForceDarkAllowed(false);
        if (android.os.Build.VERSION.SDK_INT >= 30) {
            getWindow().setDecorFitsSystemWindows(false);
            android.view.WindowInsetsController controller = getWindow().getInsetsController();
            if (controller != null) {
                controller.setSystemBarsBehavior(android.view.WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE);
                controller.hide(android.view.WindowInsets.Type.systemBars());
            }
        }
    }

    @Override protected void onCreate(Bundle savedInstanceState)
    {
        requestWindowFeature(Window.FEATURE_NO_TITLE);
        super.onCreate(savedInstanceState);

        com.chillasart.theclosingshift.port.SessionLog.begin(this);
        com.chillasart.theclosingshift.port.PortDisplay.install(this);
        setVolumeControlStream(android.media.AudioManager.STREAM_MUSIC);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);

        tryStartUnity();
    }

    private void tryStartUnity()
    {
        if (mUnityPlayer != null || isFinishing()) return;
        try { com.chillasart.theclosingshift.port.PortStorage.prepare(this); }
        catch (java.io.IOException | SecurityException error) {
            android.util.Log.e("Unity", "PORT_SAVE app-specific storage is unavailable", error);
            if (storageDialog != null && storageDialog.isShowing()) return;
            boolean ru = java.util.Locale.getDefault().getLanguage().equals("ru");
            storageDialog = new android.app.AlertDialog.Builder(this)
                .setTitle(ru ? "Папка сохранений" : "Saved games folder")
                .setMessage(ru ? "Не удалось открыть папку игры в Android/data. Проверьте доступность хранилища и свободное место, затем повторите." :
                                "Cannot open the game's Android/data folder. Check storage availability and free space, then retry.")
                .setPositiveButton(ru ? "Повторить" : "Retry", (dialog, which) -> getWindow().getDecorView().post(this::tryStartUnity))
                .setNegativeButton(ru ? "Закрыть" : "Close", (dialog, which) -> finish())
                .setOnCancelListener(dialog -> finish()).create();
            storageDialog.setOnDismissListener(dialog -> storageDialog = null);
            storageDialog.show();
            return;
        }
        startUnity();
    }

    private void startUnity()
    {

        String cmdLine = updateUnityCommandLineArguments(getIntent().getStringExtra("unity"));
        getIntent().putExtra("unity", cmdLine);

        com.chillasart.theclosingshift.port.PortConfig.seed(this);
        com.chillasart.theclosingshift.port.ControllerBridge.install(this);
        int ioResult = com.chillasart.theclosingshift.port.LargeObbIO.install(getCacheDir().getAbsolutePath());
        if (ioResult != 0) throw new IllegalStateException("Large OBB IO initialization failed: " + ioResult);
        mUnityPlayer = new UnityPlayer(this, this);
        setContentView(mUnityPlayer);
        if (android.os.Build.VERSION.SDK_INT >= 28) {
            WindowManager.LayoutParams attributes = getWindow().getAttributes();
            attributes.layoutInDisplayCutoutMode = WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES;
            getWindow().setAttributes(attributes);
        }
        mUnityPlayer.requestFocus();
        hideSystemUi();
        // A storage Retry can create Unity after the normal resume callback.
        if (com.chillasart.theclosingshift.port.PortStorage.resumeAfterCreation(activityResumed, activityStarted,
                MultiWindowSupport.getAllowResizableWindow(this))) mUnityPlayer.resume();
    }

    // When Unity player unloaded move task to background
    @Override public void onUnityPlayerUnloaded() {
        moveTaskToBack(true);
    }

    // Callback before Unity player process is killed
    @Override public void onUnityPlayerQuitted() {
    }

    @Override protected void onNewIntent(Intent intent)
    {
        // To support deep linking, we need to make sure that the client can get access to
        // the last sent intent. The clients access this through a JNI api that allows them
        // to get the intent set on launch. To update that after launch we have to manually
        // replace the intent with the one caught here.
        setIntent(intent);
        if (mUnityPlayer != null) mUnityPlayer.newIntent(intent);
    }

    // Quit Unity
    @Override protected void onDestroy ()
    {
        if (mUnityPlayer != null) mUnityPlayer.destroy();
        super.onDestroy();
    }

    // If the activity is in multi window mode or resizing the activity is allowed we will use
    // onStart/onStop (the visibility callbacks) to determine when to pause/resume.
    // Otherwise it will be done in onPause/onResume as Unity has done historically to preserve
    // existing behavior.
    @Override protected void onStop()
    {
        activityStarted = false;
        super.onStop();

        if (!MultiWindowSupport.getAllowResizableWindow(this))
            return;

        if (mUnityPlayer != null) mUnityPlayer.pause();
    }

    @Override protected void onStart()
    {
        activityStarted = true;
        super.onStart();

        if (!MultiWindowSupport.getAllowResizableWindow(this))
            return;

        if (mUnityPlayer != null) mUnityPlayer.resume();
    }

    // Pause Unity
    @Override protected void onPause()
    {
        activityResumed = false;
        super.onPause();
        com.chillasart.theclosingshift.port.ControllerBridge.cancel();

        if (MultiWindowSupport.getAllowResizableWindow(this))
            return;

        if (mUnityPlayer != null) mUnityPlayer.pause();
    }

    // Resume Unity
    @Override protected void onResume()
    {
        activityResumed = true;
        super.onResume();

        if (mUnityPlayer == null) {
            tryStartUnity();
            return;
        }

        if (MultiWindowSupport.getAllowResizableWindow(this))
            return;

        mUnityPlayer.resume();
    }

    // Low Memory Unity
    @Override public void onLowMemory()
    {
        super.onLowMemory();
        if (mUnityPlayer != null) mUnityPlayer.lowMemory();
    }

    // Trim Memory Unity
    @Override public void onTrimMemory(int level)
    {
        super.onTrimMemory(level);
        if (level == TRIM_MEMORY_RUNNING_CRITICAL)
        {
            if (mUnityPlayer != null) mUnityPlayer.lowMemory();
        }
    }

    // This ensures the layout will be correct.
    @Override public void onConfigurationChanged(Configuration newConfig)
    {
        super.onConfigurationChanged(newConfig);
        if (mUnityPlayer != null) mUnityPlayer.configurationChanged(newConfig);
    }

    // Notify Unity of the focus change.
    @Override public void onWindowFocusChanged(boolean hasFocus)
    {
        super.onWindowFocusChanged(hasFocus);
        if (!hasFocus) com.chillasart.theclosingshift.port.ControllerBridge.cancel();
        if (mUnityPlayer != null) mUnityPlayer.windowFocusChanged(hasFocus);
        if (hasFocus) hideSystemUi();
    }

    // For some reason the multiple keyevent type is not supported by the ndk.
    // Force event injection by overriding dispatchKeyEvent().
    @Override public boolean dispatchKeyEvent(KeyEvent event)
    {
        if (mUnityPlayer == null) return super.dispatchKeyEvent(event);
        if (com.chillasart.theclosingshift.port.ControllerBridge.key(event)) return true;
        if (event.getKeyCode() == KeyEvent.KEYCODE_BACK) {
            if (event.getAction() == KeyEvent.ACTION_DOWN && event.getRepeatCount() == 0) com.chillasart.theclosingshift.port.ControllerBridge.back();
            return true;
        }
        if (event.getAction() == KeyEvent.ACTION_MULTIPLE)
            return mUnityPlayer.injectEvent(event);
        return super.dispatchKeyEvent(event);
    }

    @Override public boolean dispatchGenericMotionEvent(MotionEvent event) {
        if (com.chillasart.theclosingshift.port.ControllerBridge.motion(event)) return true;
        return super.dispatchGenericMotionEvent(event);
    }
    @Override public void onBackPressed() { if (mUnityPlayer == null) finish(); else com.chillasart.theclosingshift.port.ControllerBridge.back(); }

    // Pass any events not handled by (unfocused) views straight to UnityPlayer
    @Override public boolean onKeyUp(int keyCode, KeyEvent event)     { return mUnityPlayer != null ? mUnityPlayer.injectEvent(event) : super.onKeyUp(keyCode, event); }
    @Override public boolean onKeyDown(int keyCode, KeyEvent event)   { return mUnityPlayer != null ? mUnityPlayer.injectEvent(event) : super.onKeyDown(keyCode, event); }
    @Override public boolean onTouchEvent(MotionEvent event)          { return mUnityPlayer != null ? mUnityPlayer.injectEvent(event) : super.onTouchEvent(event); }
    /*API12*/ public boolean onGenericMotionEvent(MotionEvent event)  { return mUnityPlayer != null ? mUnityPlayer.injectEvent(event) : super.onGenericMotionEvent(event); }
}
