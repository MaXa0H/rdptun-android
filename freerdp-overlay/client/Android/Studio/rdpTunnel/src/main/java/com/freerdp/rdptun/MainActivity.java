package com.freerdp.rdptun;

import android.content.Intent;
import android.net.VpnService;
import android.os.Bundle;
import android.widget.Button;
import android.widget.EditText;
import android.widget.TextView;
import android.widget.Toast;

import androidx.annotation.Nullable;
import androidx.appcompat.app.AppCompatActivity;
import androidx.core.content.ContextCompat;

public class MainActivity extends AppCompatActivity {
    private static final int REQ_VPN = 1001;

    private EditText server;
    private EditText port;
    private EditText username;
    private EditText password;
    private TextView status;

    private String pendingServer;
    private int pendingPort;
    private String pendingUser;
    private String pendingPassword;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(R.layout.activity_main);

        server = findViewById(R.id.server);
        port = findViewById(R.id.port);
        username = findViewById(R.id.username);
        password = findViewById(R.id.password);
        status = findViewById(R.id.status);
        Button connect = findViewById(R.id.connect);
        Button disconnect = findViewById(R.id.disconnect);

        server.setText(getPreferences(MODE_PRIVATE).getString("server", ""));
        username.setText(getPreferences(MODE_PRIVATE).getString("user", "rdptun"));

        connect.setOnClickListener(v -> requestConnect());
        disconnect.setOnClickListener(v -> {
            Intent i = new Intent(this, RdpVpnService.class);
            i.setAction(RdpVpnService.ACTION_STOP);
            startService(i);
            status.setText("Disconnected");
        });
    }

    private void requestConnect() {
        pendingServer = server.getText().toString().trim();
        pendingUser = username.getText().toString().trim();
        pendingPassword = password.getText().toString();
        try {
            pendingPort = Integer.parseInt(port.getText().toString().trim());
        } catch (NumberFormatException e) {
            pendingPort = 3389;
        }

        if (pendingServer.isEmpty() || pendingUser.isEmpty() || pendingPassword.isEmpty()) {
            Toast.makeText(this, "Server, username and password are required", Toast.LENGTH_SHORT).show();
            return;
        }

        getPreferences(MODE_PRIVATE).edit()
                .putString("server", pendingServer)
                .putString("user", pendingUser)
                .apply();

        Intent prepare = VpnService.prepare(this);
        if (prepare != null) {
            startActivityForResult(prepare, REQ_VPN);
        } else {
            startTunnel();
        }
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, @Nullable Intent data) {
        super.onActivityResult(requestCode, resultCode, data);
        if (requestCode == REQ_VPN) {
            if (resultCode == RESULT_OK) {
                startTunnel();
            } else {
                status.setText("VPN permission denied");
            }
        }
    }

    private void startTunnel() {
        Intent i = new Intent(this, RdpVpnService.class);
        i.setAction(RdpVpnService.ACTION_START);
        i.putExtra(RdpVpnService.EXTRA_SERVER, pendingServer);
        i.putExtra(RdpVpnService.EXTRA_PORT, pendingPort);
        i.putExtra(RdpVpnService.EXTRA_USER, pendingUser);
        i.putExtra(RdpVpnService.EXTRA_PASSWORD, pendingPassword);
        ContextCompat.startForegroundService(this, i);
        status.setText("Connecting RDP…");
    }
}
