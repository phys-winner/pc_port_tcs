package com.chillasart.theclosingshift.port;

/** Invoke before constructing UnityPlayer; the shim maps libunity without initializing Unity. */
public final class LargeObbIO {
    static { System.loadLibrary("largeobbio"); }
    private LargeObbIO() { }
    public static native int install(String cacheDirectory);
}
