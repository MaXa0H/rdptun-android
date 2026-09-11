#include <windows.h>
#include <objbase.h>
#include <tsvirtualchannels.h>

#include <atomic>
#include <cstdio>
#include <cstring>
#include <functional>
#include <mutex>
#include <new>
#include <string>
#include <thread>
#include <vector>

#pragma comment(linker, "/EXPORT:DllGetClassObject")
#pragma comment(linker, "/EXPORT:DllCanUnloadNow")

namespace
{
    constexpr BYTE FrameStatus = 1;
    constexpr BYTE FramePacketFromDvc = 2;
    constexpr BYTE FramePacketToDvc = 3;
    constexpr BYTE FrameDvcOpened = 4;
    constexpr BYTE FrameDvcClosed = 5;
    constexpr DWORD MaxPipeFrame = 1024 * 1024;
    constexpr size_t MaxDvcPacket = 4096;
    constexpr const char* ChannelName = "rdptun";

    std::mutex g_traceMutex;
    volatile LONG g_objectCount = 0;
    volatile LONG g_serverLocks = 0;

    std::wstring GetEnv(const wchar_t* name)
    {
        DWORD needed = GetEnvironmentVariableW(name, nullptr, 0);
        if (!needed) return {};
        std::wstring value(needed, L'\0');
        DWORD written = GetEnvironmentVariableW(name, value.data(), needed);
        if (!written) return {};
        value.resize(written);
        return value;
    }

    std::wstring GetTracePath()
    {
        std::wstring value = GetEnv(L"RDPTUN_TRACE");
        if (!value.empty()) return value;
        wchar_t tmp[MAX_PATH]{};
        GetTempPathW(MAX_PATH, tmp);
        return std::wstring(tmp) + L"rdptun-inproc-plugin.log";
    }

    std::wstring GetPipeName()
    {
        std::wstring value = GetEnv(L"RDPTUN_PIPE");
        return value.empty() ? L"rdptun-control" : value;
    }

    std::wstring GuidString(REFGUID guid)
    {
        wchar_t buffer[64]{};
        return StringFromGUID2(guid, buffer, _countof(buffer)) > 0 ? std::wstring(buffer) : L"{}";
    }

    std::string WideToUtf8(const std::wstring& text)
    {
        if (text.empty()) return {};
        int size = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
        if (size <= 0) return {};
        std::string result(static_cast<size_t>(size), '\0');
        WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), result.data(), size, nullptr, nullptr);
        return result;
    }

    std::string HexHr(HRESULT hr)
    {
        char buffer[16]{};
        std::snprintf(buffer, sizeof(buffer), "%08lX", static_cast<unsigned long>(hr));
        return buffer;
    }

    void Trace(const std::string& text)
    {
        std::lock_guard<std::mutex> lock(g_traceMutex);
        SYSTEMTIME st{};
        GetLocalTime(&st);
        char prefix[128]{};
        std::snprintf(prefix, sizeof(prefix), "%02u:%02u:%02u.%03u pid=%lu ",
            st.wHour, st.wMinute, st.wSecond, st.wMilliseconds, GetCurrentProcessId());
        std::string line = std::string(prefix) + text + "\r\n";
        HANDLE file = CreateFileW(GetTracePath().c_str(), FILE_APPEND_DATA,
            FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) return;
        DWORD written = 0;
        WriteFile(file, line.data(), static_cast<DWORD>(line.size()), &written, nullptr);
        CloseHandle(file);
    }

    bool WriteAll(HANDLE handle, const void* data, DWORD size)
    {
        const BYTE* cursor = static_cast<const BYTE*>(data);
        while (size)
        {
            DWORD written = 0;
            if (!WriteFile(handle, cursor, size, &written, nullptr) || !written) return false;
            cursor += written;
            size -= written;
        }
        return true;
    }

    bool ReadAll(HANDLE handle, void* data, DWORD size)
    {
        BYTE* cursor = static_cast<BYTE*>(data);
        while (size)
        {
            DWORD read = 0;
            if (!ReadFile(handle, cursor, size, &read, nullptr) || !read) return false;
            cursor += read;
            size -= read;
        }
        return true;
    }

    class PipeClient
    {
    public:
        using PacketHandler = std::function<void(const std::vector<BYTE>&)>;

        explicit PipeClient(PacketHandler handler) : m_packetHandler(std::move(handler)) {}
        ~PipeClient() { Stop(); }

        void Start()
        {
            if (m_running.exchange(true)) return;
            Trace("INPROC PipeClient.Start pipe=" + WideToUtf8(GetPipeName()));
            m_thread = std::thread([this] { Run(); });
        }

        void Stop()
        {
            if (!m_running.exchange(false)) return;
            HANDLE pipe = INVALID_HANDLE_VALUE;
            {
                std::lock_guard<std::mutex> lock(m_pipeMutex);
                pipe = m_pipe;
            }
            if (pipe != INVALID_HANDLE_VALUE) CancelIoEx(pipe, nullptr);
            if (m_thread.joinable())
            {
                CancelSynchronousIo(static_cast<HANDLE>(m_thread.native_handle()));
                m_thread.join();
            }
        }

        void SendStatus(const std::string& text)
        {
            Trace("STATUS " + text);
            SendFrame(FrameStatus, std::vector<BYTE>(text.begin(), text.end()));
        }

        void SendPacketFromDvc(const BYTE* data, size_t size)
        {
            SendFrame(FramePacketFromDvc, std::vector<BYTE>(data, data + size));
        }

        void SendDvcOpened() { Trace("DVC OPENED"); SendFrame(FrameDvcOpened, {}); }
        void SendDvcClosed() { Trace("DVC CLOSED"); SendFrame(FrameDvcClosed, {}); }

    private:
        void Run()
        {
            const std::wstring fullPipe = L"\\\\.\\pipe\\" + GetPipeName();
            while (m_running.load())
            {
                HANDLE pipe = CreateFileW(fullPipe.c_str(), GENERIC_READ | GENERIC_WRITE,
                    0, nullptr, OPEN_EXISTING, 0, nullptr);
                if (pipe == INVALID_HANDLE_VALUE)
                {
                    if (!m_running.load()) break;
                    WaitNamedPipeW(fullPipe.c_str(), 500);
                    continue;
                }
                {
                    std::lock_guard<std::mutex> lock(m_pipeMutex);
                    m_pipe = pipe;
                }
                Trace("INPROC IPC connected");
                SendStatus("DVC plugin IPC connected (native in-proc)");

                while (m_running.load())
                {
                    BYTE header[5]{};
                    if (!ReadAll(pipe, header, sizeof(header))) break;
                    DWORD length = static_cast<DWORD>(header[1]) |
                        (static_cast<DWORD>(header[2]) << 8) |
                        (static_cast<DWORD>(header[3]) << 16) |
                        (static_cast<DWORD>(header[4]) << 24);
                    if (length > MaxPipeFrame) break;
                    std::vector<BYTE> payload(length);
                    if (length && !ReadAll(pipe, payload.data(), length)) break;
                    if (header[0] == FramePacketToDvc && m_packetHandler) m_packetHandler(payload);
                }

                {
                    std::lock_guard<std::mutex> lock(m_pipeMutex);
                    if (m_pipe == pipe) m_pipe = INVALID_HANDLE_VALUE;
                }
                CloseHandle(pipe);
                if (m_running.load()) Sleep(250);
            }
        }

        void SendFrame(BYTE type, const std::vector<BYTE>& payload)
        {
            if (payload.size() > MaxPipeFrame) return;
            std::lock_guard<std::mutex> writeLock(m_writeMutex);
            HANDLE pipe = INVALID_HANDLE_VALUE;
            {
                std::lock_guard<std::mutex> lock(m_pipeMutex);
                pipe = m_pipe;
            }
            if (pipe == INVALID_HANDLE_VALUE) return;
            DWORD length = static_cast<DWORD>(payload.size());
            BYTE header[5]{ type, static_cast<BYTE>(length), static_cast<BYTE>(length >> 8),
                static_cast<BYTE>(length >> 16), static_cast<BYTE>(length >> 24) };
            if (!WriteAll(pipe, header, sizeof(header))) return;
            if (length) WriteAll(pipe, payload.data(), length);
        }

        std::atomic<bool> m_running{ false };
        std::thread m_thread;
        std::mutex m_pipeMutex;
        std::mutex m_writeMutex;
        HANDLE m_pipe = INVALID_HANDLE_VALUE;
        PacketHandler m_packetHandler;
    };

    class RdpPlugin final : public IWTSPlugin, public IWTSListenerCallback, public IWTSVirtualChannelCallback
    {
    public:
        RdpPlugin() : m_pipe([this](const std::vector<BYTE>& packet) { SendPacketToDvc(packet); })
        {
            InterlockedIncrement(&g_objectCount);
            Trace("INPROC RdpPlugin constructed");
            m_pipe.Start();
            m_pipe.SendStatus("RDP DVC native in-proc plugin object created");
        }

        ~RdpPlugin()
        {
            Trace("INPROC RdpPlugin destroyed");
            ClearChannel();
            if (m_listener) { m_listener->Release(); m_listener = nullptr; }
            if (m_manager) { m_manager->Release(); m_manager = nullptr; }
            m_pipe.Stop();
            InterlockedDecrement(&g_objectCount);
        }

        HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppvObject) override
        {
            if (!ppvObject) return E_POINTER;
            *ppvObject = nullptr;
            Trace("INPROC QI iid=" + WideToUtf8(GuidString(riid)));
            if (riid == IID_IUnknown || riid == __uuidof(IWTSPlugin))
                *ppvObject = static_cast<IWTSPlugin*>(this);
            else if (riid == __uuidof(IWTSListenerCallback))
                *ppvObject = static_cast<IWTSListenerCallback*>(this);
            else if (riid == __uuidof(IWTSVirtualChannelCallback))
                *ppvObject = static_cast<IWTSVirtualChannelCallback*>(this);
            else
                return E_NOINTERFACE;
            AddRef();
            return S_OK;
        }

        ULONG STDMETHODCALLTYPE AddRef() override { return static_cast<ULONG>(InterlockedIncrement(&m_refCount)); }
        ULONG STDMETHODCALLTYPE Release() override
        {
            ULONG value = static_cast<ULONG>(InterlockedDecrement(&m_refCount));
            if (!value) delete this;
            return value;
        }

        HRESULT STDMETHODCALLTYPE Initialize(IWTSVirtualChannelManager* manager) override
        {
            Trace("IWTSPlugin.Initialize ENTER (native in-proc)");
            m_pipe.SendStatus("IWTSPlugin.Initialize called (native in-proc)");
            if (!manager) return E_POINTER;
            if (m_manager) m_manager->Release();
            m_manager = manager;
            m_manager->AddRef();
            if (m_listener) { m_listener->Release(); m_listener = nullptr; }
            HRESULT hr = m_manager->CreateListener(const_cast<LPSTR>(ChannelName), 0,
                static_cast<IWTSListenerCallback*>(this), &m_listener);
            m_pipe.SendStatus(SUCCEEDED(hr) ? "DVC listener created: rdptun (native in-proc)" :
                "CreateListener failed (native in-proc): 0x" + HexHr(hr));
            return hr;
        }

        HRESULT STDMETHODCALLTYPE Connected() override
        {
            m_pipe.SendStatus("RDP client reports Connected (native in-proc)");
            return S_OK;
        }

        HRESULT STDMETHODCALLTYPE Disconnected(DWORD code) override
        {
            ClearChannel();
            m_pipe.SendStatus("RDP disconnected (native in-proc): 0x" + HexHr(static_cast<HRESULT>(code)));
            m_pipe.SendDvcClosed();
            return S_OK;
        }

        HRESULT STDMETHODCALLTYPE Terminated() override
        {
            ClearChannel();
            m_pipe.SendStatus("DVC plugin terminated (native in-proc)");
            m_pipe.SendDvcClosed();
            return S_OK;
        }

        HRESULT STDMETHODCALLTYPE OnNewChannelConnection(IWTSVirtualChannel* channel, BSTR,
            BOOL* accept, IWTSVirtualChannelCallback** callback) override
        {
            if (!channel || !accept || !callback) return E_POINTER;
            {
                std::lock_guard<std::mutex> lock(m_channelMutex);
                if (m_channel) m_channel->Release();
                m_channel = channel;
                m_channel->AddRef();
            }
            {
                std::lock_guard<std::mutex> lock(m_rxMutex);
                m_rx.clear();
            }
            *accept = TRUE;
            *callback = static_cast<IWTSVirtualChannelCallback*>(this);
            AddRef();
            m_pipe.SendStatus("DVC rdptun opened (native in-proc)");
            m_pipe.SendDvcOpened();
            return S_OK;
        }

        HRESULT STDMETHODCALLTYPE OnDataReceived(ULONG size, BYTE* buffer) override
        {
            if (!size || !buffer) return S_OK;
            std::lock_guard<std::mutex> lock(m_rxMutex);
            if (m_rx.size() + size > 65536)
            {
                m_rx.clear();
                return E_FAIL;
            }
            m_rx.insert(m_rx.end(), buffer, buffer + size);
            size_t offset = 0;
            while (m_rx.size() - offset >= 2)
            {
                size_t length = (static_cast<size_t>(m_rx[offset]) << 8) | m_rx[offset + 1];
                if (!length || length > MaxDvcPacket)
                {
                    m_rx.clear();
                    return E_FAIL;
                }
                if (m_rx.size() - offset < length + 2) break;
                m_pipe.SendPacketFromDvc(m_rx.data() + offset + 2, length);
                offset += length + 2;
            }
            if (offset) m_rx.erase(m_rx.begin(), m_rx.begin() + static_cast<ptrdiff_t>(offset));
            return S_OK;
        }

        HRESULT STDMETHODCALLTYPE OnClose() override
        {
            ClearChannel();
            m_pipe.SendStatus("DVC rdptun closed (native in-proc)");
            m_pipe.SendDvcClosed();
            return S_OK;
        }

    private:
        void ClearChannel()
        {
            std::lock_guard<std::mutex> lock(m_channelMutex);
            if (m_channel) { m_channel->Release(); m_channel = nullptr; }
        }

        void SendPacketToDvc(const std::vector<BYTE>& packet)
        {
            if (packet.empty() || packet.size() > MaxDvcPacket || (packet[0] >> 4) != 4) return;
            IWTSVirtualChannel* channel = nullptr;
            {
                std::lock_guard<std::mutex> lock(m_channelMutex);
                channel = m_channel;
                if (channel) channel->AddRef();
            }
            if (!channel) return;
            std::vector<BYTE> frame(packet.size() + 2);
            frame[0] = static_cast<BYTE>(packet.size() >> 8);
            frame[1] = static_cast<BYTE>(packet.size());
            std::memcpy(frame.data() + 2, packet.data(), packet.size());
            HRESULT hr = channel->Write(static_cast<ULONG>(frame.size()), frame.data(), nullptr);
            channel->Release();
            if (FAILED(hr)) m_pipe.SendStatus("DVC Write failed (native in-proc): 0x" + HexHr(hr));
        }

        volatile LONG m_refCount = 1;
        IWTSVirtualChannelManager* m_manager = nullptr;
        IWTSListener* m_listener = nullptr;
        IWTSVirtualChannel* m_channel = nullptr;
        std::mutex m_channelMutex;
        std::mutex m_rxMutex;
        std::vector<BYTE> m_rx;
        PipeClient m_pipe;
    };

    class ClassFactory final : public IClassFactory
    {
    public:
        ClassFactory() { InterlockedIncrement(&g_objectCount); Trace("INPROC ClassFactory constructed"); }
        ~ClassFactory() { Trace("INPROC ClassFactory destroyed"); InterlockedDecrement(&g_objectCount); }

        HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppvObject) override
        {
            if (!ppvObject) return E_POINTER;
            *ppvObject = nullptr;
            if (riid != IID_IUnknown && riid != IID_IClassFactory) return E_NOINTERFACE;
            *ppvObject = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }

        ULONG STDMETHODCALLTYPE AddRef() override { return static_cast<ULONG>(InterlockedIncrement(&m_refCount)); }
        ULONG STDMETHODCALLTYPE Release() override
        {
            ULONG value = static_cast<ULONG>(InterlockedDecrement(&m_refCount));
            if (!value) delete this;
            return value;
        }

        HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID riid, void** ppvObject) override
        {
            Trace("CLASSFACTORY CreateInstance inproc riid=" + WideToUtf8(GuidString(riid)));
            if (!ppvObject) return E_POINTER;
            *ppvObject = nullptr;
            if (outer) return CLASS_E_NOAGGREGATION;
            RdpPlugin* plugin = new (std::nothrow) RdpPlugin();
            if (!plugin) return E_OUTOFMEMORY;
            HRESULT hr = plugin->QueryInterface(riid, ppvObject);
            plugin->Release();
            Trace("CLASSFACTORY CreateInstance inproc hr=0x" + HexHr(hr));
            return hr;
        }

        HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override
        {
            if (lock) InterlockedIncrement(&g_serverLocks);
            else InterlockedDecrement(&g_serverLocks);
            return S_OK;
        }

    private:
        volatile LONG m_refCount = 1;
    };
}

STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, LPVOID* ppv)
{
    Trace("DllGetClassObject clsid=" + WideToUtf8(GuidString(rclsid)) + " riid=" + WideToUtf8(GuidString(riid)));
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    ClassFactory* factory = new (std::nothrow) ClassFactory();
    if (!factory) return E_OUTOFMEMORY;
    HRESULT hr = factory->QueryInterface(riid, ppv);
    factory->Release();
    Trace("DllGetClassObject hr=0x" + HexHr(hr));
    return hr;
}

STDAPI DllCanUnloadNow()
{
    return (g_objectCount == 0 && g_serverLocks == 0) ? S_OK : S_FALSE;
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(instance);
    return TRUE;
}
