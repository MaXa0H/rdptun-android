using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using Rdptun.Common;

namespace Rdptun.Plugin
{
    internal sealed class PluginPipeClient : IDisposable
    {
        private readonly object _writeLock = new object();
        private volatile bool _running;
        private Thread _thread;
        private NamedPipeClientStream _pipe;

        public event Action<byte[]> PacketToDvc;

        public void Start()
        {
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "rdptun-plugin-pipe" };
            _thread.Start();
        }

        private void Loop()
        {
            while (_running)
            {
                try
                {
                    using (NamedPipeClientStream pipe = new NamedPipeClientStream(".", PipeProtocol.PipeName, PipeDirection.InOut, PipeOptions.None))
                    {
                        pipe.Connect(1000);
                        _pipe = pipe;
                        SendStatus("DVC plugin IPC connected");
                        while (_running && pipe.IsConnected)
                        {
                            PipeFrame frame = PipeProtocol.ReadFrame(pipe);
                            if (frame == null) break;
                            if (frame.Type == PipeFrameType.PacketToDvc)
                            {
                                Action<byte[]> handler = PacketToDvc;
                                if (handler != null) handler(frame.Payload);
                            }
                        }
                    }
                }
                catch (TimeoutException) { }
                catch (IOException) { }
                catch { }
                finally { _pipe = null; }
                if (_running) Thread.Sleep(250);
            }
        }

        public void SendStatus(string text) { Send(PipeFrameType.Status, PipeProtocol.Utf8(text)); }
        public void SendPacketFromDvc(byte[] packet) { Send(PipeFrameType.PacketFromDvc, packet); }
        public void SendDvcOpened() { Send(PipeFrameType.DvcOpened, new byte[0]); }
        public void SendDvcClosed() { Send(PipeFrameType.DvcClosed, new byte[0]); }

        private void Send(PipeFrameType type, byte[] payload)
        {
            NamedPipeClientStream pipe = _pipe;
            if (pipe == null || !pipe.IsConnected) return;
            try
            {
                lock (_writeLock) PipeProtocol.WriteFrame(pipe, type, payload);
            }
            catch { }
        }

        public void Dispose()
        {
            _running = false;
            try { if (_pipe != null) _pipe.Dispose(); } catch { }
            if (_thread != null && _thread.IsAlive) _thread.Join(1000);
        }
    }
}
