package com.freerdp.rdptun;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.net.Uri;
import android.net.VpnService;
import android.os.Build;
import android.os.ParcelFileDescriptor;
import android.util.Log;

import androidx.core.app.NotificationCompat;

import com.freerdp.freerdpcore.services.LibFreeRDP;

import java.io.IOException;
import java.net.Inet4Address;
import java.net.InetAddress;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public class RdpVpnService extends VpnService implements LibFreeRDP.EventListener {
    public static final String ACTION_START = "com.freerdp.rdptun.START";
    public static final String ACTION_STOP = "com.freerdp.rdptun.STOP";
    public static final String EXTRA_SERVER = "server";
    public static final String EXTRA_PORT = "port";
    public static final String EXTRA_USER = "user";
    public static final String EXTRA_PASSWORD = "password";

    private static final String TAG = "RdpVpnService";
    private static final String NOTIF_CHANNEL = "rdptun";
    private static final int NOTIF_ID = 77;

    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private volatile long instance = 0;
    private volatile boolean stopping = false;
    private volatile boolean freeRdpReady = false;
    private volatile String freeRdpInitError = null;
    private String serverIpv4;

    @Override
    public void onCreate() {
        super.onCreate();
        NotificationManager nm = getSystemService(NotificationManager.class);
        nm.createNotificationChannel(new NotificationChannel(
                NOTIF_CHANNEL, "RDP Tunnel", NotificationManager.IMPORTANCE_LOW));

        try {
            // This is deliberately guarded. Accessing LibFreeRDP triggers its static
            // native-library loader. If a device cannot load one of the .so files,
            // keep the VPN process alive long enough to expose the actual error.
            LibFreeRDP.setEventListener(this);
            freeRdpReady = true;
        } catch (Throwable t) {
            freeRdpInitError = describe(t);
            Log.e(TAG, "FreeRDP initialization failed", t);
        }
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent != null && ACTION_STOP.equals(intent.getAction())) {
            stopTunnel();
            return START_NOT_STICKY;
        }

        if (intent == null || !ACTION_START.equals(intent.getAction()))
            return START_NOT_STICKY;

        String host = intent.getStringExtra(EXTRA_SERVER);
        int port = intent.getIntExtra(EXTRA_PORT, 3389);
        String user = intent.getStringExtra(EXTRA_USER);
        String password = intent.getStringExtra(EXTRA_PASSWORD);

        startForegroundCompat(buildNotification("VPN service started"));
        stopping = false;

        if (!freeRdpReady) {
            updateNotification("FreeRDP load failed: " + freeRdpInitError);
            return START_NOT_STICKY;
        }

        updateNotification("Resolving server…");
        worker.execute(() -> connectRdp(host, port, user, password));
        return START_NOT_STICKY;
    }

    private void connectRdp(String host, int port, String user, String password) {
        long inst = 0;
        try {
            serverIpv4 = resolveIpv4(host);
            updateNotification("Creating FreeRDP instance…");

            Uri uri = new Uri.Builder()
                    .scheme("freerdp")
                    .encodedAuthority(serverIpv4 + ":" + port)
                    .appendPath("connect")
                    .appendQueryParameter("u", user)
                    .appendQueryParameter("p", password)
                    .appendQueryParameter("dvc", "rdptun")
                    .appendQueryParameter("cert", "ignore")
                    .appendQueryParameter("size", "320x240")
                    .appendQueryParameter("bpp", "16")
                    .appendQueryParameter("network", "modem")
                    .appendQueryParameter("audio-mode", "2")
                    .appendQueryParameter("wallpaper", "-")
                    .appendQueryParameter("window-drag", "-")
                    .appendQueryParameter("menu-anims", "-")
                    .appendQueryParameter("themes", "-")
                    .appendQueryParameter("fonts", "-")
                    .appendQueryParameter("aero", "-")
                    .appendQueryParameter("clipboard", "-")
                    .appendQueryParameter("disp", "-")
                    .appendQueryParameter("gfx", "-")
                    .appendQueryParameter("log-level", "INFO")
                    .build();

            inst = LibFreeRDP.newInstance(this);
            if (inst == 0)
                throw new IllegalStateException("LibFreeRDP.newInstance returned 0");

            instance = inst;
            updateNotification("Parsing RDP settings…");
            if (!LibFreeRDP.setConnectionInfo(this, inst, uri))
                throw new IllegalStateException("FreeRDP argument parsing failed");

            updateNotification("Connecting RDP to " + serverIpv4 + ":" + port + "…");
            if (!LibFreeRDP.connect(inst))
                throw new IllegalStateException("LibFreeRDP.connect returned false");
        } catch (Throwable t) {
            Log.e(TAG, "RDP connection failed", t);
            updateNotification("Failed: " + describe(t));
        } finally {
            if (inst != 0) {
                try { LibFreeRDP.freeInstance(inst); } catch (Throwable t) {
                    Log.e(TAG, "FreeRDP freeInstance failed", t);
                }
            }
            if (instance == inst)
                instance = 0;
            safeCloseTun();
            // Keep the foreground service alive on failure so its notification keeps
            // the last diagnostic stage visible. The Disconnect action stops it.
        }
    }

    private static String resolveIpv4(String host) throws Exception {
        for (InetAddress address : InetAddress.getAllByName(host)) {
            if (address instanceof Inet4Address)
                return address.getHostAddress();
        }
        throw new IllegalArgumentException("Server has no IPv4 address");
    }

    @Override
    public void OnPreConnect(long inst) {
        updateNotification("RDP negotiating…");
    }

    @Override
    public void OnConnectionSuccess(long inst) {
        if (inst != instance || stopping)
            return;
        try {
            updateNotification("RDP connected; creating VPN TUN…");
            establishVpn();
            updateNotification("Connected — traffic via RDP");
        } catch (Throwable t) {
            Log.e(TAG, "VPN setup failed", t);
            updateNotification("VPN setup failed: " + describe(t));
            try { LibFreeRDP.disconnect(inst); } catch (Throwable ignored) {}
        }
    }

    private void establishVpn() throws Exception {
        Builder b = new Builder()
                .setSession("RDP Tunnel")
                .setMtu(1200)
                .addAddress("10.77.0.2", 24)
                .addRoute("0.0.0.0", 0)
                .addDnsServer("1.1.1.1")
                .setBlocking(true);

        // API 31-compatible loop prevention: the app hosting the RDP socket is
        // excluded from its own VPN. Other apps remain routed into the VPN TUN.
        b.addDisallowedApplication(getPackageName());

        ParcelFileDescriptor pfd = b.establish();
        if (pfd == null)
            throw new IllegalStateException("VpnService.Builder.establish() returned null");

        int fd = pfd.detachFd();
        boolean accepted;
        try {
            accepted = RdpTunnelNative.attachTunFd(fd);
        } catch (Throwable t) {
            try { ParcelFileDescriptor.adoptFd(fd).close(); } catch (IOException ignored) {}
            throw new IllegalStateException("rdptun JNI attachTunFd failed: " + describe(t), t);
        }

        if (!accepted) {
            try { ParcelFileDescriptor.adoptFd(fd).close(); } catch (IOException ignored) {}
            throw new IllegalStateException("Native rdptun did not accept TUN fd");
        }
    }

    @Override
    public void OnConnectionFailure(long inst) {
        updateNotification("RDP connection failed");
    }

    @Override
    public void OnDisconnecting(long inst) {
        updateNotification("Disconnecting…");
    }

    @Override
    public void OnDisconnected(long inst) {
        safeCloseTun();
        updateNotification("Disconnected");
    }

    @Override
    public void onRevoke() {
        stopTunnel();
        super.onRevoke();
    }

    @Override
    public void onDestroy() {
        stopping = true;
        safeCloseTun();
        try { LibFreeRDP.setEventListener(null); } catch (Throwable ignored) {}
        worker.shutdownNow();
        super.onDestroy();
    }

    private void stopTunnel() {
        stopping = true;
        safeCloseTun();
        long inst = instance;
        if (inst != 0) {
            try { LibFreeRDP.disconnect(inst); } catch (Throwable ignored) {}
        }
        stopForeground(STOP_FOREGROUND_REMOVE);
        stopSelf();
    }

    private void safeCloseTun() {
        try {
            RdpTunnelNative.closeTun();
        } catch (Throwable t) {
            Log.e(TAG, "rdptun JNI closeTun failed", t);
        }
    }

    private static String describe(Throwable t) {
        String msg = t.getMessage();
        if (msg == null || msg.isEmpty())
            return t.getClass().getSimpleName();
        return t.getClass().getSimpleName() + ": " + msg;
    }

    private Notification buildNotification(String text) {
        Intent stop = new Intent(this, RdpVpnService.class).setAction(ACTION_STOP);
        PendingIntent stopPi = PendingIntent.getService(this, 1, stop,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        return new NotificationCompat.Builder(this, NOTIF_CHANNEL)
                .setSmallIcon(android.R.drawable.stat_sys_upload_done)
                .setContentTitle("RDP Tunnel")
                .setContentText(text)
                .setStyle(new NotificationCompat.BigTextStyle().bigText(text))
                .setOngoing(true)
                .addAction(0, "Disconnect", stopPi)
                .build();
    }

    private void updateNotification(String text) {
        Log.i(TAG, text);
        NotificationManager nm = getSystemService(NotificationManager.class);
        nm.notify(NOTIF_ID, buildNotification(text));
    }

    private void startForegroundCompat(Notification n) {
        if (Build.VERSION.SDK_INT >= 34) {
            startForeground(NOTIF_ID, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE);
        } else {
            startForeground(NOTIF_ID, n);
        }
    }
}
