using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using Rdptun.Common;

namespace Rdptun.App
{
    internal sealed class TunnelPipeServer : IDisposable
    {
        private readonly object _writeLock = new object();
        private volatile bool _running;
        private Thread _thread;
        private NamedPipeServerStream _pipe;

        public event Action<string> StatusMessage;
        public event Action DvcOpened;
        public event Action DvcClosed;
        public event Action<byte[]> PacketFromDvc;

        public void Start()
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(ServerLoop) { IsBackground = true, Name = "rdptun-pipe-server" };
            _thread.Start();
        }

        private static PipeSecurity CreatePipeSecurity()
        {
            WindowsIdentity identity = WindowsIdentity.GetCurrent();
            SecurityIdentifier user = identity.User;
            if (user == null)
                throw new InvalidOperationException("Could not determine current Windows user SID");

            PipeSecurity security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new PipeAccessRule(
                user,
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
            return security;
        }

        private void ServerLoop()
        {
            while (_running)
            {
                try
                {
                    PipeSecurity security = CreatePipeSecurity();
                    using (NamedPipeServerStream pipe = new NamedPipeServerStream(
                        PipeProtocol.PipeName,
                        PipeDirection.InOut,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.None,
                        65536,
                        65536,
                        security))
                    {
                        _pipe = pipe;
                        RaiseStatus("IPC pipe ready for current user");
                        pipe.WaitForConnection();
                        RaiseStatus("plugin IPC connected");

                        while (_running && pipe.IsConnected)
                        {
                            PipeFrame frame = PipeProtocol.ReadFrame(pipe);
                            if (frame == null) break;
                            switch (frame.Type)
                            {
                                case PipeFrameType.Status:
                                    RaiseStatus(PipeProtocol.Utf8(frame.Payload));
                                    break;
                                case PipeFrameType.PacketFromDvc:
                                    var packet = PacketFromDvc;
                                    if (packet != null) packet(frame.Payload);
                                    break;
                                case PipeFrameType.DvcOpened:
                                    var opened = DvcOpened;
                                    if (opened != null) opened();
                                    break;
                                case PipeFrameType.DvcClosed:
                                    var closed = DvcClosed;
                                    if (closed != null) closed();
                                    break;
                            }
                        }
                    }
                }
                catch (IOException ex)
                {
                    if (_running) RaiseStatus("plugin IPC reset: " + ex.Message);
                }
                catch (ObjectDisposedException) { }
                catch (Exception ex)
                {
                    if (_running) RaiseStatus("plugin IPC error: " + ex.Message);
                }
                finally
                {
                    _pipe = null;
                }

                if (_running)
                    Thread.Sleep(250);
            }
        }

        public void SendPacketToDvc(byte[] packet)
        {
            NamedPipeServerStream pipe = _pipe;
            if (pipe == null || !pipe.IsConnected)
                return;
            try
            {
                lock (_writeLock)
                {
                    PipeProtocol.WriteFrame(pipe, PipeFrameType.PacketToDvc, packet);
                }
            }
            catch (Exception ex)
            {
                RaiseStatus("pipe write failed: " + ex.Message);
            }
        }

        private void RaiseStatus(string text)
        {
            Action<string> handler = StatusMessage;
            if (handler != null) handler(text);
        }

        public void Dispose()
        {
            _running = false;
            try { if (_pipe != null) _pipe.Dispose(); } catch { }
            if (_thread != null && _thread.IsAlive)
                _thread.Join(1500);
        }
    }
}
