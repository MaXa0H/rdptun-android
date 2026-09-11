package com.freerdp.rdptun;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.net.IpPrefix;
import android.net.Uri;
import android.net.VpnService;
import android.os.Build;
import android.os.ParcelFileDescriptor;

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

    private static final String NOTIF_CHANNEL = "rdptun";
    private static final int NOTIF_ID = 77;

    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private volatile long instance = 0;
    private volatile boolean stopping = false;
    private String serverIpv4;

    @Override
    public void onCreate() {
        super.onCreate();
        NotificationManager nm = getSystemService(NotificationManager.class);
        nm.createNotificationChannel(new NotificationChannel(
                NOTIF_CHANNEL, "RDP Tunnel", NotificationManager.IMPORTANCE_LOW));
        LibFreeRDP.setEventListener(this);
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

        startForegroundCompat(buildNotification("Resolving server…"));
        stopping = false;

        worker.execute(() -> connectRdp(host, port, user, password));
        return START_NOT_STICKY;
    }

    private void connectRdp(String host, int port, String user, String password) {
        long inst = 0;
        try {
            serverIpv4 = resolveIpv4(host);
            updateNotification("Connecting RDP to " + serverIpv4 + ":" + port);

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
            instance = inst;
            if (!LibFreeRDP.setConnectionInfo(this, inst, uri))
                throw new IllegalStateException("FreeRDP argument parsing failed");

            LibFreeRDP.connect(inst);
        } catch (Throwable t) {
            updateNotification("Failed: " + t.getClass().getSimpleName() + ": " + t.getMessage());
        } finally {
            if (inst != 0) {
                try { LibFreeRDP.freeInstance(inst); } catch (Throwable ignored) {}
            }
            if (instance == inst)
                instance = 0;
            RdpTunnelNative.closeTun();
            if (!stopping)
                stopSelf();
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
            establishVpn();
            updateNotification("Connected — traffic via RDP");
        } catch (Throwable t) {
            updateNotification("VPN setup failed: " + t.getMessage());
            LibFreeRDP.disconnect(inst);
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

        b.excludeRoute(new IpPrefix(InetAddress.getByName(serverIpv4), 32));

        ParcelFileDescriptor pfd = b.establish();
        if (pfd == null)
            throw new IllegalStateException("VpnService.Builder.establish() returned null");

        int fd = pfd.detachFd();
        if (!RdpTunnelNative.attachTunFd(fd)) {
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
        RdpTunnelNative.closeTun();
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
        RdpTunnelNative.closeTun();
        LibFreeRDP.setEventListener(null);
        worker.shutdownNow();
        super.onDestroy();
    }

    private void stopTunnel() {
        stopping = true;
        RdpTunnelNative.closeTun();
        long inst = instance;
        if (inst != 0)
            LibFreeRDP.disconnect(inst);
        stopForeground(STOP_FOREGROUND_REMOVE);
        stopSelf();
    }

    private Notification buildNotification(String text) {
        Intent stop = new Intent(this, RdpVpnService.class).setAction(ACTION_STOP);
        PendingIntent stopPi = PendingIntent.getService(this, 1, stop,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        return new NotificationCompat.Builder(this, NOTIF_CHANNEL)
                .setSmallIcon(android.R.drawable.stat_sys_upload_done)
                .setContentTitle("RDP Tunnel")
                .setContentText(text)
                .setOngoing(true)
                .addAction(0, "Disconnect", stopPi)
                .build();
    }

    private void updateNotification(String text) {
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
