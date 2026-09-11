package com.freerdp.rdptun;

import com.freerdp.freerdpcore.services.LibFreeRDP;

public final class RdpTunnelNative {
    static {
        LibFreeRDP.getVersion();
    }

    private RdpTunnelNative() {}

    public static native boolean attachTunFd(int fd);
    public static native void closeTun();
}
