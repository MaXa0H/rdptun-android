#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <objbase.h>
#include <tsvirtualchannels.h>

#include <atomic>
#include <cstdint>
#include <cstdio>
#include <functional>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

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
    HANDLE g_shutdownEvent = nullptr;

    std::wstring GetEnv(const wchar_t* name)
    {
        DWORD needed = GetEnvironmentVariableW(name, nullptr, 0);
        if (needed == 0)
            return {};
        std::wstring value(needed, L'\0');
        DWORD written = GetEnvironmentVariableW(name, value.data(), needed);
        if (written == 0)
            return {};
        value.resize(written);
        return value;
    }

    std::wstring GetTracePath()
    {
        std::wstring value = GetEnv(L"RDPTUN_TRACE");
        if (!value.empty())
            return value;
        wchar_t tmp[MAX_PATH]{};
        GetTempPathW(MAX_PATH, tmp);
        return std::wstring(tmp) + L"rdptun-native-plugin.log";
    }

    std::wstring GetPipeName()
    {
        std::wstring value = GetEnv(L"RDPTUN_PIPE");
        if (value.empty())
            value = L"rdptun-control";
        return value;
    }

    GUID GetPluginClsid()
    {
        GUID clsid{ 0x41F85E29, 0xDFE2, 0x45F9, {0xB3,0xF4,0xC5,0xF6,0x46,0xFA,0x8F,0x73} };
        std::wstring value = GetEnv(L"RDPTUN_CLSID");
        if (!value.empty())
        {
            GUID parsed{};
            if (SUCCEEDED(CLSIDFromString(value.c_str(), &parsed)))
                clsid = parsed;
        }
        return clsid;
    }

    std::wstring GuidString(REFGUID guid)
    {
        wchar_t buffer[64]{};
        if (StringFromGUID2(guid, buffer, static_cast<int>(std::size(buffer))) <= 0)
            return L"{}";
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
        if (file == INVALID_HANDLE_VALUE)
            return;
        DWORD written = 0;
        WriteFile(file, line.data(), static_cast<DWORD>(line.size()), &written, nullptr);
        CloseHandle(file);
    }

    bool WriteAll(HANDLE h, const void* data, DWORD size)
    {
        const BYTE* p = static_cast<const BYTE*>(data);
        while (size > 0)
        {
            DWORD written = 0;
            if (!WriteFile(h, p, size, &written, nullptr) || written == 0)
                return false;
            p += written;
            size -= written;
        }
        return true;
    }

    bool ReadAll(HANDLE h, void* data, DWORD size)
    {
        BYTE* p = static_cast<BYTE*>(data);
        while (size > 0)
        {
            DWORD read = 0;
            if (!ReadFile(h, p, size, &read, nullptr) || read == 0)
                return false;
            p += read;
            size -= read;
        }
        return true;
    }

    class PipeClient
    {
    public:
        using PacketHandler = std::function<void(const std::vector<BYTE>&)>;

        explicit PipeClient(PacketHandler handler)
            : m_packetHandler(std::move(handler))
        {
        }

        ~PipeClient() { Stop(); }

        void Start()
        {
            if (m_running.exchange(true))
                return;
            Trace("NativePipeClient.Start pipe=" + WideToUtf8(GetPipeName()));
            m_thread = std::thread([this] { Run(); });
        }

        void Stop()
        {
            if (!m_running.exchange(false))
                return;
            if (m_thread.joinable())
            {
                CancelSynchronousIo(static_cast<HANDLE>(m_thread.native_handle()));
                m_thread.join();
            }
        }

        void SendStatus(const std::string& text)
        {
            Trace("STATUS " + text);
            std::vector<BYTE> payload(text.begin(), text.end());
            SendFrame(FrameStatus, payload);
        }

        void SendPacketFromDvc(const BYTE* data, size_t size)
        {
            std::vector<BYTE> payload(data, data + size);
            SendFrame(FramePacketFromDvc, payload);
        }

        void SendDvcOpened()
        {
            Trace("DVC OPENED");
            SendFrame(FrameDvcOpened, {});
        }

        void SendDvcClosed()
        {
            Trace("DVC CLOSED");
            SendFrame(FrameDvcClosed, {});
        }

        static std::string WideToUtf8(const std::wstring& text)
        {
            if (text.empty()) return {};
            int n = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
            if (n <= 0) return {};
            std::string out(static_cast<size_t>(n), '\0');
            WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), out.data(), n, nullptr, nullptr);
            return out;
        }

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
                Trace("IPC connected");
                SendStatus("DVC plugin IPC connected (native)");

                while (m_running.load())
                {
                    BYTE header[5]{};
                    if (!ReadAll(pipe, header, 5))
                        break;
                    DWORD length = static_cast<DWORD>(header[1]) |
                        (static_cast<DWORD>(header[2]) << 8) |
                        (static_cast<DWORD>(header[3]) << 16) |
                        (static_cast<DWORD>(header[4]) << 24);
                    if (length > MaxPipeFrame)
                    {
                        Trace("IPC invalid frame length=" + std::to_string(length));
                        break;
                    }
                    std::vector<BYTE> payload(length);
                    if (length != 0 && !ReadAll(pipe, payload.data(), length))
                        break;
                    if (header[0] == FramePacketToDvc && m_packetHandler)
                        m_packetHandler(payload);
                }

                {
                    std::lock_guard<std::mutex> lock(m_pipeMutex);
                    if (m_pipe == pipe)
                        m_pipe = INVALID_HANDLE_VALUE;
                }
                CloseHandle(pipe);
                if (m_running.load())
                    Sleep(250);
            }
        }

        void SendFrame(BYTE type, const std::vector<BYTE>& payload)
        {
            if (payload.size() > MaxPipeFrame)
                return;
            std::lock_guard<std::mutex> writeLock(m_writeMutex);
            HANDLE pipe = INVALID_HANDLE_VALUE;
            {
                std::lock_guard<std::mutex> lock(m_pipeMutex);
                pipe = m_pipe;
            }
            if (pipe == INVALID_HANDLE_VALUE)
                return;

            DWORD length = static_cast<DWORD>(payload.size());
            BYTE header[5]{
                type,
                static_cast<BYTE>(length),
                static_cast<BYTE>(length >> 8),
                static_cast<BYTE>(length >> 16),
                static_cast<BYTE>(length >> 24)
            };
            if (!WriteAll(pipe, header, 5))
                return;
            if (length != 0)
                WriteAll(pipe, payload.data(), length);
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
        RdpPlugin()
            : m_pipe([this](const std::vector<BYTE>& packet) { SendPacketToDvc(packet); })
        {
            m_pipe.Start();
            m_pipe.SendStatus("RDP DVC native plugin object created; clsid=" + PipeClient::WideToUtf8(GuidString(GetPluginClsid())));
            Trace("NATIVE COM object constructed");
        }

        ~RdpPlugin() override
        {
            Trace("NATIVE COM object destroyed");
            ClearChannel();
            if (m_listener) { m_listener->Release(); m_listener = nullptr; }
            if (m_manager) { m_manager->Release(); m_manager = nullptr; }
            m_pipe.Stop();
        }

        HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppvObject) override
        {
            if (!ppvObject) return E_POINTER;
            *ppvObject = nullptr;
            Trace("NATIVE QI iid=" + PipeClient::WideToUtf8(GuidString(riid)));

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

        ULONG STDMETHODCALLTYPE AddRef() override
        {
            return static_cast<ULONG>(InterlockedIncrement(&m_refCount));
        }

        ULONG STDMETHODCALLTYPE Release() override
        {
            ULONG value = static_cast<ULONG>(InterlockedDecrement(&m_refCount));
            if (value == 0)
                delete this;
            return value;
        }

        HRESULT STDMETHODCALLTYPE Initialize(IWTSVirtualChannelManager* channelManager) override
        {
            Trace("IWTSPlugin.Initialize ENTER (native)");
            m_pipe.SendStatus("IWTSPlugin.Initialize called (native)");
            if (!channelManager)
                return E_POINTER;

            if (m_manager) { m_manager->Release(); m_manager = nullptr; }
            m_manager = channelManager;
            m_manager->AddRef();

            if (m_listener) { m_listener->Release(); m_listener = nullptr; }
            HRESULT hr = m_manager->CreateListener(const_cast<LPSTR>(ChannelName), 0,
                static_cast<IWTSListenerCallback*>(this), &m_listener);
            if (SUCCEEDED(hr))
                m_pipe.SendStatus("DVC listener created: rdptun (native)");
            else
                m_pipe.SendStatus("CreateListener failed (native): 0x" + HexHr(hr));
            return hr;
        }

        HRESULT STDMETHODCALLTYPE Connected() override
        {
            m_pipe.SendStatus("RDP client reports Connected (native)");
            return S_OK;
        }

        HRESULT STDMETHODCALLTYPE Disconnected(DWORD disconnectCode) override
        {
            ClearChannel();
            m_pipe.SendStatus("RDP disconnected (native): 0x" + HexHr(static_cast<HRESULT>(disconnectCode)));
            m_pipe.SendDvcClosed();
            return S_OK;
        }

        HRESULT STDMETHODCALLTYPE Terminated() override
        {
            ClearChannel();
            m_pipe.SendStatus("DVC plugin terminated (native)");
            m_pipe.SendDvcClosed();
            return S_OK;
        }

        HRESULT STDMETHODCALLTYPE OnNewChannelConnection(
            IWTSVirtualChannel* channel,
            BSTR,
            BOOL* accept,
            IWTSVirtualChannelCallback** callback) override
        {
            if (!channel || !accept || !callback)
                return E_POINTER;

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

            m_pipe.SendStatus("DVC rdptun opened (native)");
            m_pipe.SendDvcOpened();
            return S_OK;
        }

        HRESULT STDMETHODCALLTYPE OnDataReceived(ULONG size, BYTE* buffer) override
        {
            if (size == 0 || !buffer)
                return S_OK;

            std::lock_guard<std::mutex> lock(m_rxMutex);
            if (m_rx.size() + size > 65536)
            {
                m_rx.clear();
                m_pipe.SendStatus("DVC RX buffer overflow (native)");
                return E_FAIL;
            }
            m_rx.insert(m_rx.end(), buffer, buffer + size);

            size_t offset = 0;
            while (m_rx.size() - offset >= 2)
            {
                size_t length = (static_cast<size_t>(m_rx[offset]) << 8) | m_rx[offset + 1];
                if (length == 0 || length > MaxDvcPacket)
                {
                    m_rx.clear();
                    m_pipe.SendStatus("Invalid DVC packet length (native): " + std::to_string(length));
                    return E_FAIL;
                }
                if (m_rx.size() - offset < length + 2)
                    break;

                m_pipe.SendPacketFromDvc(m_rx.data() + offset + 2, length);
                offset += length + 2;
            }
            if (offset > 0)
                m_rx.erase(m_rx.begin(), m_rx.begin() + static_cast<ptrdiff_t>(offset));
            return S_OK;
        }

        HRESULT STDMETHODCALLTYPE OnClose() override
        {
            ClearChannel();
            m_pipe.SendStatus("DVC rdptun closed (native)");
            m_pipe.SendDvcClosed();
            return S_OK;
        }

    private:
        static std::string HexHr(HRESULT hr)
        {
            char buffer[16]{};
            std::snprintf(buffer, sizeof(buffer), "%08lX", static_cast<unsigned long>(hr));
            return buffer;
        }

        void ClearChannel()
        {
            std::lock_guard<std::mutex> lock(m_channelMutex);
            if (m_channel)
            {
                m_channel->Release();
                m_channel = nullptr;
            }
        }

        void SendPacketToDvc(const std::vector<BYTE>& packet)
        {
            if (packet.empty() || packet.size() > MaxDvcPacket || ((packet[0] >> 4) != 4))
                return;

            IWTSVirtualChannel* channel = nullptr;
            {
                std::lock_guard<std::mutex> lock(m_channelMutex);
                channel = m_channel;
                if (channel) channel->AddRef();
            }
            if (!channel)
                return;

            std::vector<BYTE> frame(packet.size() + 2);
            frame[0] = static_cast<BYTE>(packet.size() >> 8);
            frame[1] = static_cast<BYTE>(packet.size());
            memcpy(frame.data() + 2, packet.data(), packet.size());
            HRESULT hr = channel->Write(static_cast<ULONG>(frame.size()), frame.data(), nullptr);
            channel->Release();
            if (FAILED(hr))
                m_pipe.SendStatus("DVC Write failed (native): 0x" + HexHr(hr));
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
        HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppvObject) override
        {
            if (!ppvObject) return E_POINTER;
            *ppvObject = nullptr;
            if (riid != IID_IUnknown && riid != IID_IClassFactory)
                return E_NOINTERFACE;
            *ppvObject = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }

        ULONG STDMETHODCALLTYPE AddRef() override
        {
            return static_cast<ULONG>(InterlockedIncrement(&m_refCount));
        }

        ULONG STDMETHODCALLTYPE Release() override
        {
            ULONG value = static_cast<ULONG>(InterlockedDecrement(&m_refCount));
            if (value == 0) delete this;
            return value;
        }

        HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID riid, void** ppvObject) override
        {
            Trace("CLASSFACTORY CreateInstance native riid=" + PipeClient::WideToUtf8(GuidString(riid)));
            if (!ppvObject) return E_POINTER;
            *ppvObject = nullptr;
            if (outer) return CLASS_E_NOAGGREGATION;

            RdpPlugin* plugin = new (std::nothrow) RdpPlugin();
            if (!plugin) return E_OUTOFMEMORY;
            HRESULT hr = plugin->QueryInterface(riid, ppvObject);
            plugin->Release();
            Trace(std::string("CLASSFACTORY CreateInstance native hr=0x") + Hex(hr));
            return hr;
        }

        HRESULT STDMETHODCALLTYPE LockServer(BOOL) override { return S_OK; }

    private:
        static std::string Hex(HRESULT hr)
        {
            char buffer[16]{};
            std::snprintf(buffer, sizeof(buffer), "%08lX", static_cast<unsigned long>(hr));
            return buffer;
        }
        volatile LONG m_refCount = 1;
    };

    bool SetStringValue(HKEY root, const std::wstring& path, const wchar_t* name, const std::wstring& value)
    {
        HKEY key = nullptr;
        if (RegCreateKeyExW(root, path.c_str(), 0, nullptr, 0, KEY_WRITE, nullptr, &key, nullptr) != ERROR_SUCCESS)
            return false;
        const BYTE* data = reinterpret_cast<const BYTE*>(value.c_str());
        DWORD bytes = static_cast<DWORD>((value.size() + 1) * sizeof(wchar_t));
        LONG rc = RegSetValueExW(key, name, 0, REG_SZ, data, bytes);
        RegCloseKey(key);
        return rc == ERROR_SUCCESS;
    }

    std::wstring ModulePath()
    {
        std::wstring path(32768, L'\0');
        DWORD n = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
        if (n == 0 || n >= path.size())
            return {};
        path.resize(n);
        return path;
    }

    bool RegisterPlugin()
    {
        GUID clsid = GetPluginClsid();
        std::wstring clsidText = GuidString(clsid);
        std::wstring addin = L"Software\\Microsoft\\Terminal Server Client\\Default\\AddIns\\Rdptun";
        std::wstring localServer = L"Software\\Classes\\CLSID\\" + clsidText + L"\\LocalServer32";
        std::wstring exe = L"\"" + ModulePath() + L"\"";
        bool ok1 = SetStringValue(HKEY_CURRENT_USER, addin, L"Name", clsidText);
        bool ok2 = SetStringValue(HKEY_CURRENT_USER, localServer, nullptr, exe);
        Trace("REGISTER native clsid=" + PipeClient::WideToUtf8(clsidText));
        return ok1 && ok2;
    }

    bool UnregisterPlugin()
    {
        GUID clsid = GetPluginClsid();
        std::wstring clsidText = GuidString(clsid);
        RegDeleteTreeW(HKEY_CURRENT_USER, L"Software\\Microsoft\\Terminal Server Client\\Default\\AddIns\\Rdptun");
        std::wstring clsidPath = L"Software\\Classes\\CLSID\\" + clsidText;
        RegDeleteTreeW(HKEY_CURRENT_USER, clsidPath.c_str());
        return true;
    }
}

int wmain(int argc, wchar_t** argv)
{
    if (argc > 1)
    {
        if (_wcsicmp(argv[1], L"/register") == 0 || _wcsicmp(argv[1], L"-register") == 0)
            return RegisterPlugin() ? 0 : 1;
        if (_wcsicmp(argv[1], L"/unregister") == 0 || _wcsicmp(argv[1], L"-unregister") == 0)
            return UnregisterPlugin() ? 0 : 1;
    }

    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(hr))
        return 2;

    g_shutdownEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!g_shutdownEvent)
    {
        CoUninitialize();
        return 3;
    }

    GUID clsid = GetPluginClsid();
    ClassFactory* factory = new (std::nothrow) ClassFactory();
    if (!factory)
    {
        CloseHandle(g_shutdownEvent);
        CoUninitialize();
        return 4;
    }

    DWORD cookie = 0;
    hr = CoRegisterClassObject(clsid, factory, CLSCTX_LOCAL_SERVER,
        REGCLS_MULTIPLEUSE | REGCLS_SUSPENDED, &cookie);
    factory->Release();
    if (FAILED(hr))
    {
        CloseHandle(g_shutdownEvent);
        CoUninitialize();
        return 5;
    }

    hr = CoResumeClassObjects();
    if (FAILED(hr))
    {
        CoRevokeClassObject(cookie);
        CloseHandle(g_shutdownEvent);
        CoUninitialize();
        return 6;
    }

    Trace("Native COM LocalServer ready clsid=" + PipeClient::WideToUtf8(GuidString(clsid)));
    WaitForSingleObject(g_shutdownEvent, INFINITE);

    CoRevokeClassObject(cookie);
    CloseHandle(g_shutdownEvent);
    g_shutdownEvent = nullptr;
    CoUninitialize();
    return 0;
}
