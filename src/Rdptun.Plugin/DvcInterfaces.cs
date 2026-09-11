// COM interface definitions adapted from Microsoft's rdp-dvc-plugin-samples (MIT).
using System;
using System.Runtime.InteropServices;

namespace Rdptun.Plugin
{
    [ComImport]
    [Guid("A1230201-1439-4e62-a414-190d0ac3d40e")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IWTSPlugin
    {
        [PreserveSig] int Initialize([In, MarshalAs(UnmanagedType.Interface)] IWTSVirtualChannelManager channelManager);
        [PreserveSig] int Connected();
        [PreserveSig] int Disconnected([In] uint disconnectCode);
        [PreserveSig] int Terminated();
    }

    [ComImport]
    [Guid("A1230206-9a39-4d58-8674-cdb4dff4e73b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IWTSListener
    {
        [PreserveSig] int GetConfiguration([Out, MarshalAs(UnmanagedType.Interface)] out object propertyBag);
    }

    [ComImport]
    [Guid("A1230203-d6a7-11d8-b9fd-000bdbd1f198")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IWTSListenerCallback
    {
        [PreserveSig]
        int OnNewChannelConnection(
            [In, MarshalAs(UnmanagedType.Interface)] IWTSVirtualChannel channel,
            [In, MarshalAs(UnmanagedType.BStr)] string data,
            [Out, MarshalAs(UnmanagedType.Bool)] out bool accept,
            [Out, MarshalAs(UnmanagedType.Interface)] out IWTSVirtualChannelCallback callback);
    }

    [ComImport]
    [Guid("A1230204-d6a7-11d8-b9fd-000bdbd1f198")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IWTSVirtualChannelCallback
    {
        [PreserveSig]
        int OnDataReceived([In] uint size, [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] byte[] buffer);
        [PreserveSig] int OnClose();
    }

    [ComImport]
    [Guid("A1230205-d6a7-11d8-b9fd-000bdbd1f198")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IWTSVirtualChannelManager
    {
        [PreserveSig]
        int CreateListener(
            [In, MarshalAs(UnmanagedType.LPStr)] string channelName,
            [In] uint flags,
            [In, MarshalAs(UnmanagedType.Interface)] IWTSListenerCallback listenerCallback,
            [Out, MarshalAs(UnmanagedType.Interface)] out IWTSListener listener);
    }

    [ComImport]
    [Guid("A1230207-d6a7-11d8-b9fd-000bdbd1f198")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IWTSVirtualChannel
    {
        [PreserveSig]
        int Write([In] uint size, [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] byte[] buffer, [In, MarshalAs(UnmanagedType.IUnknown)] object reserved);
        [PreserveSig] int Close();
    }
}
