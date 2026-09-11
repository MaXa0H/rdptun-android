using System;
using System.Runtime.InteropServices;

namespace Rdptun.Plugin
{
    [ComVisible(true)]
    [Guid("41F85E29-DFE2-45F9-B3F4-C5F646FA8F73")]
    [ClassInterface(ClassInterfaceType.None)]
    [ComDefaultInterface(typeof(IWTSPlugin))]
    public sealed class RdpPlugin : IWTSPlugin, IWTSListenerCallback, IWTSVirtualChannelCallback
    {
        private const string ChannelName = "rdptun";
        private const int MaxPacket = 4096;
        private const int S_OK = 0;
        private const int E_FAIL = unchecked((int)0x80004005);

        private readonly object _channelLock = new object();
        private readonly object _rxLock = new object();
        private readonly byte[] _rx = new byte[65536];
        private int _rxCount;
        private IWTSVirtualChannel _channel;
        private IWTSListener _listener;
        private readonly PluginPipeClient _pipe;

        public static Guid PluginClsid
        {
            get
            {
                string value = Environment.GetEnvironmentVariable("RDPTUN_CLSID");
                Guid parsed;
                if (!string.IsNullOrWhiteSpace(value) && Guid.TryParse(value, out parsed))
                    return parsed;
                return new Guid("41F85E29-DFE2-45F9-B3F4-C5F646FA8F73");
            }
        }

        public RdpPlugin()
        {
            _pipe = new PluginPipeClient();
            _pipe.PacketToDvc += SendPacketToDvc;
            _pipe.Start();
            _pipe.SendStatus("RDP DVC plugin object created; clsid=" + PluginClsid.ToString("B"));
            PluginPipeClient.Trace("CCW mode: ClassInterface=None, default=IWTSPlugin");
        }

        public int Initialize(IWTSVirtualChannelManager channelManager)
        {
            PluginPipeClient.Trace("IWTSPlugin.Initialize ENTER");
            if (channelManager == null)
            {
                PluginPipeClient.Trace("IWTSPlugin.Initialize channelManager=null");
                return E_FAIL;
            }

            try
            {
                _pipe.SendStatus("IWTSPlugin.Initialize called");
                int hr = channelManager.CreateListener(ChannelName, 0, this, out _listener);
                _pipe.SendStatus(hr == S_OK ? "DVC listener created: " + ChannelName : "CreateListener failed: 0x" + hr.ToString("X8"));
                PluginPipeClient.Trace("IWTSPlugin.Initialize EXIT hr=0x" + hr.ToString("X8"));
                return hr;
            }
            catch (Exception ex)
            {
                PluginPipeClient.Trace("IWTSPlugin.Initialize EXCEPTION " + ex);
                _pipe.SendStatus("Initialize failed: " + ex.Message);
                return E_FAIL;
            }
        }

        public int Connected()
        {
            PluginPipeClient.Trace("IWTSPlugin.Connected ENTER");
            _pipe.SendStatus("RDP client reports Connected");
            return S_OK;
        }

        public int Disconnected(uint disconnectCode)
        {
            PluginPipeClient.Trace("IWTSPlugin.Disconnected code=0x" + disconnectCode.ToString("X8"));
            lock (_channelLock) _channel = null;
            _pipe.SendStatus("RDP disconnected: 0x" + disconnectCode.ToString("X8"));
            _pipe.SendDvcClosed();
            return S_OK;
        }

        public int Terminated()
        {
            PluginPipeClient.Trace("IWTSPlugin.Terminated ENTER");
            lock (_channelLock) _channel = null;
            _pipe.SendStatus("DVC plugin terminated");
            _pipe.SendDvcClosed();
            _pipe.Dispose();
            COMRegistration.LocalServer.RequestShutdown();
            return S_OK;
        }

        public int OnNewChannelConnection(IWTSVirtualChannel channel, string data, out bool accept, out IWTSVirtualChannelCallback callback)
        {
            PluginPipeClient.Trace("IWTSListenerCallback.OnNewChannelConnection ENTER");
            accept = true;
            callback = this;
            lock (_channelLock) _channel = channel;
            lock (_rxLock) _rxCount = 0;
            _pipe.SendStatus("DVC rdptun opened");
            _pipe.SendDvcOpened();
            return S_OK;
        }

        public int OnDataReceived(uint size, byte[] buffer)
        {
            if (buffer == null || size == 0) return S_OK;
            int count = (int)Math.Min(size, (uint)buffer.Length);
            lock (_rxLock)
            {
                if (_rxCount + count > _rx.Length)
                {
                    _rxCount = 0;
                    _pipe.SendStatus("DVC RX buffer overflow; frame reset");
                    return E_FAIL;
                }
                Buffer.BlockCopy(buffer, 0, _rx, _rxCount, count);
                _rxCount += count;

                int offset = 0;
                while (_rxCount - offset >= 2)
                {
                    int length = (_rx[offset] << 8) | _rx[offset + 1];
                    if (length <= 0 || length > MaxPacket)
                    {
                        _rxCount = 0;
                        _pipe.SendStatus("Invalid DVC packet length: " + length);
                        return E_FAIL;
                    }
                    if (_rxCount - offset < 2 + length)
                        break;

                    byte[] packet = new byte[length];
                    Buffer.BlockCopy(_rx, offset + 2, packet, 0, length);
                    _pipe.SendPacketFromDvc(packet);
                    offset += 2 + length;
                }

                if (offset > 0)
                {
                    int remain = _rxCount - offset;
                    if (remain > 0) Buffer.BlockCopy(_rx, offset, _rx, 0, remain);
                    _rxCount = remain;
                }
            }
            return S_OK;
        }

        public int OnClose()
        {
            PluginPipeClient.Trace("IWTSVirtualChannelCallback.OnClose ENTER");
            lock (_channelLock) _channel = null;
            _pipe.SendStatus("DVC rdptun closed");
            _pipe.SendDvcClosed();
            return S_OK;
        }

        private void SendPacketToDvc(byte[] packet)
        {
            if (packet == null || packet.Length == 0 || packet.Length > MaxPacket) return;
            if ((packet[0] >> 4) != 4) return;

            IWTSVirtualChannel channel;
            lock (_channelLock) channel = _channel;
            if (channel == null) return;

            byte[] frame = new byte[packet.Length + 2];
            frame[0] = (byte)(packet.Length >> 8);
            frame[1] = (byte)packet.Length;
            Buffer.BlockCopy(packet, 0, frame, 2, packet.Length);
            try
            {
                int hr = channel.Write((uint)frame.Length, frame, null);
                if (hr != S_OK) _pipe.SendStatus("DVC Write failed: 0x" + hr.ToString("X8"));
            }
            catch (Exception ex)
            {
                _pipe.SendStatus("DVC Write exception: " + ex.Message);
            }
        }
    }
}
