using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace Rdptun.App
{
    internal sealed class WintunSession : IDisposable
    {
        private const int ErrorNoMoreItems = 259;
        private const uint WaitObject0 = 0;
        private const uint WaitTimeout = 258;

        private IntPtr _adapter;
        private IntPtr _session;
        private volatile bool _running;
        private Thread _reader;
        private readonly Action<byte[]> _packetHandler;
        private readonly Action<string> _log;

        public WintunSession(Action<byte[]> packetHandler, Action<string> log)
        {
            _packetHandler = packetHandler;
            _log = log;
        }

        public void Start()
        {
            _adapter = Native.WintunOpenAdapter("rdptun");
            if (_adapter == IntPtr.Zero)
            {
                _adapter = Native.WintunCreateAdapter("rdptun", "RDP Tunnel", IntPtr.Zero);
                if (_adapter == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "WintunCreateAdapter failed");
            }

            _session = Native.WintunStartSession(_adapter, 0x400000);
            if (_session == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "WintunStartSession failed");

            _running = true;
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "rdptun-wintun-reader" };
            _reader.Start();
        }

        private void ReadLoop()
        {
            IntPtr waitEvent = Native.WintunGetReadWaitEvent(_session);
            while (_running)
            {
                uint size;
                IntPtr packet = Native.WintunReceivePacket(_session, out size);
                if (packet != IntPtr.Zero)
                {
                    try
                    {
                        if (size > 0 && size <= 65535)
                        {
                            byte[] data = new byte[size];
                            Marshal.Copy(packet, data, 0, (int)size);
                            _packetHandler(data);
                        }
                    }
                    finally
                    {
                        Native.WintunReleaseReceivePacket(_session, packet);
                    }
                    continue;
                }

                int error = Marshal.GetLastWin32Error();
                if (error != ErrorNoMoreItems)
                {
                    if (_running) _log("WintunReceivePacket error: " + error);
                    Thread.Sleep(50);
                    continue;
                }

                uint wait = Native.WaitForSingleObject(waitEvent, 500);
                if (wait != WaitObject0 && wait != WaitTimeout && _running)
                    Thread.Sleep(50);
            }
        }

        public void Inject(byte[] packet)
        {
            IntPtr session = _session;
            if (session == IntPtr.Zero || packet == null || packet.Length == 0)
                return;
            IntPtr dst = Native.WintunAllocateSendPacket(session, (uint)packet.Length);
            if (dst == IntPtr.Zero)
                return;
            Marshal.Copy(packet, 0, dst, packet.Length);
            Native.WintunSendPacket(session, dst);
        }

        public void Dispose()
        {
            _running = false;
            if (_reader != null && _reader.IsAlive)
                _reader.Join(1000);
            if (_session != IntPtr.Zero)
            {
                Native.WintunEndSession(_session);
                _session = IntPtr.Zero;
            }
            if (_adapter != IntPtr.Zero)
            {
                Native.WintunCloseAdapter(_adapter);
                _adapter = IntPtr.Zero;
            }
        }

        private static class Native
        {
            [DllImport("wintun.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern IntPtr WintunCreateAdapter(string name, string tunnelType, IntPtr requestedGuid);
            [DllImport("wintun.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern IntPtr WintunOpenAdapter(string name);
            [DllImport("wintun.dll")]
            internal static extern void WintunCloseAdapter(IntPtr adapter);
            [DllImport("wintun.dll", SetLastError = true)]
            internal static extern IntPtr WintunStartSession(IntPtr adapter, uint capacity);
            [DllImport("wintun.dll")]
            internal static extern void WintunEndSession(IntPtr session);
            [DllImport("wintun.dll")]
            internal static extern IntPtr WintunGetReadWaitEvent(IntPtr session);
            [DllImport("wintun.dll", SetLastError = true)]
            internal static extern IntPtr WintunReceivePacket(IntPtr session, out uint packetSize);
            [DllImport("wintun.dll")]
            internal static extern void WintunReleaseReceivePacket(IntPtr session, IntPtr packet);
            [DllImport("wintun.dll", SetLastError = true)]
            internal static extern IntPtr WintunAllocateSendPacket(IntPtr session, uint packetSize);
            [DllImport("wintun.dll")]
            internal static extern void WintunSendPacket(IntPtr session, IntPtr packet);
            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        }
    }
}
