#include "PduReassembler.h"
#include "WinHandle.h"
#include "RdpPlugin.h"
#include "ServerLifetime.h"
#include "AgentLink.Transport.h"
#include <wrl/client.h>
#include <appxpackaging.h>
#include <shlwapi.h>
#include <array>
#include <atomic>
#include <cstdio>
#include <mutex>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

using Microsoft::WRL::ComPtr;
using ral::PduReassembler;

unsigned ActivationTests();
unsigned CrossProcessActivationTests();
int CrossProcessActivationServer(char** arguments);

namespace {
unsigned checks{};
HRESULT NoClientReporter(std::wstring&) noexcept { return S_FALSE; }
void Check(bool value, const char* message) {
    ++checks;
    if (!value) throw std::runtime_error(message);
}
std::vector<uint8_t> Pdu(uint32_t total, uint32_t flags, std::span<const uint8_t> payload = {}) {
    CHANNEL_PDU_HEADER header{total, flags};
    std::vector<uint8_t> result(sizeof(header) + payload.size());
    std::memcpy(result.data(), &header, sizeof(header));
    if (!payload.empty()) std::memcpy(result.data() + sizeof(header), payload.data(), payload.size());
    return result;
}

void ParserTests() {
    const std::array<uint8_t, 5> bytes{0, 1, 2, 0xfe, 0xff};
    PduReassembler parser;
    Check(parser.Feed(Pdu(5, CHANNEL_FLAG_ONLY, bytes)) == ERROR_SUCCESS, "single record");
    std::array<uint8_t, 8> output{};
    Check(parser.Read({output.data(), 2}) == 2 && output[1] == 1, "capacity slicing first");
    Check(parser.Feed(Pdu(0, CHANNEL_FLAG_ONLY)) == ERROR_BUSY, "do not overwrite unread bytes");
    Check(parser.Read({output.data(), 2}) == 2 && output[0] == 2 && output[1] == 0xfe, "capacity slicing middle");
    Check(parser.Read(output) == 1 && output[0] == 0xff, "capacity slicing last");
    Check(parser.Read(output) == 0, "empty read");
    Check(parser.Feed(Pdu(0, CHANNEL_FLAG_ONLY)) == ERROR_SUCCESS, "empty record");
    Check(!parser.Ready(), "empty record produces no bytes");
    Check(parser.Feed(Pdu(5, CHANNEL_FLAG_FIRST, std::span(bytes).first(2))) == ERROR_SUCCESS, "first fragment");
    Check(!parser.Ready() && parser.Incomplete() && parser.Read(output) == 0, "withhold incomplete record");
    Check(parser.Feed(Pdu(5, 0, std::span(bytes).subspan(2, 1))) == ERROR_SUCCESS, "middle fragment");
    Check(parser.Feed(Pdu(5, CHANNEL_FLAG_LAST, std::span(bytes).last(2))) == ERROR_SUCCESS, "last fragment");
    Check(parser.Read(output) == 5 && std::equal(bytes.begin(), bytes.end(), output.begin()), "multi-fragment bytes");
    Check(parser.Feed(Pdu(5, CHANNEL_FLAG_ONLY, bytes)) == ERROR_SUCCESS && parser.Read(output) == 5, "consecutive records");
    Check(parser.EndOfStream() == ERROR_BROKEN_PIPE, "clean EOF");

    for (size_t size = 0; size < sizeof(CHANNEL_PDU_HEADER); ++size) {
        PduReassembler bad;
        Check(bad.Feed(std::span(output).first(size)) == ERROR_INVALID_DATA, "short header");
    }
    for (const auto flags : {uint32_t{0}, uint32_t{CHANNEL_FLAG_LAST}, uint32_t{4},
                            uint32_t{CHANNEL_FLAG_ONLY | CHANNEL_FLAG_FAIL}, uint32_t{0xffffffff}}) {
        PduReassembler bad;
        Check(bad.Feed(Pdu(5, flags, bytes)) == ERROR_INVALID_DATA, "invalid initial flags");
        Check(bad.Feed(Pdu(5, CHANNEL_FLAG_ONLY, bytes)) == ERROR_INVALID_DATA, "sticky protocol fault");
    }
    {
        PduReassembler bad;
        Check(bad.Feed(Pdu(4, CHANNEL_FLAG_ONLY, bytes)) == ERROR_INVALID_DATA, "payload exceeds record length");
    }
    {
        PduReassembler bad;
        Check(bad.Feed(Pdu(6, CHANNEL_FLAG_ONLY, bytes)) == ERROR_INVALID_DATA, "premature last");
    }
    {
        PduReassembler bad;
        Check(bad.Feed(Pdu(5, CHANNEL_FLAG_FIRST, bytes)) == ERROR_INVALID_DATA, "missing last at exact length");
    }
    for (const auto length : {ral::MaxRecordBytes + 1, uint32_t{0xffffffff}}) {
        PduReassembler bad;
        Check(bad.Feed(Pdu(length, CHANNEL_FLAG_FIRST, bytes)) == ERROR_FILE_TOO_LARGE, "record allocation limit");
    }
    {
        PduReassembler bad;
        Check(bad.Feed(Pdu(8, CHANNEL_FLAG_FIRST, bytes)) == ERROR_SUCCESS, "incomplete start");
        Check(bad.EndOfStream() == ERROR_INVALID_DATA, "truncated EOF");
    }
    {
        PduReassembler bad;
        bad.Feed(Pdu(8, CHANNEL_FLAG_FIRST, bytes));
        Check(bad.Feed(Pdu(5, CHANNEL_FLAG_ONLY, bytes)) == ERROR_INVALID_DATA, "duplicate first");
    }
    {
        PduReassembler bad;
        bad.Feed(Pdu(10, CHANNEL_FLAG_FIRST, bytes));
        Check(bad.Feed(Pdu(11, CHANNEL_FLAG_LAST, bytes)) == ERROR_INVALID_DATA, "inconsistent advertised length");
    }
    {
        PduReassembler bad;
        Check(bad.Feed(Pdu(1, CHANNEL_FLAG_FIRST)) == ERROR_INVALID_DATA, "nonprogress fragment");
    }
    {
        PduReassembler bad;
        std::vector<uint8_t> oversized(CHANNEL_CHUNK_LENGTH + 1);
        Check(bad.Feed(Pdu(static_cast<uint32_t>(oversized.size()), CHANNEL_FLAG_ONLY, oversized)) ==
            ERROR_INVALID_DATA, "oversized WTS PDU");
    }
    for (const uint32_t size : {1u, 1599u, 1600u, 1601u, 16384u, ral::MaxRecordBytes}) {
        PduReassembler boundary;
        std::vector<uint8_t> data(size);
        for (uint32_t i = 0; i < size; ++i) data[i] = static_cast<uint8_t>(i * 31u);
        for (uint32_t offset = 0; offset < size;) {
            const auto count = std::min(size - offset, uint32_t{CHANNEL_CHUNK_LENGTH});
            const auto flags = (offset == 0 ? CHANNEL_FLAG_FIRST : 0u) |
                (offset + count == size ? CHANNEL_FLAG_LAST : 0u);
            Check(boundary.Feed(Pdu(size, flags, std::span(data).subspan(offset, count))) == ERROR_SUCCESS, "boundary fragment");
            offset += count;
        }
        std::vector<uint8_t> recovered(size);
        uint32_t offset{};
        while (offset < size) {
            const auto count = boundary.Read(std::span(recovered).subspan(offset, std::min(size - offset, 997u)));
            Check(count != 0, "boundary capacity progress");
            offset += static_cast<uint32_t>(count);
        }
        Check(data == recovered, "boundary exact bytes");
    }
}

class FakeListener final : public ral::ComObject<IWTSListener> {
public:
    HRESULT STDMETHODCALLTYPE GetConfiguration(IPropertyBag** value) noexcept override {
        if (!value) return E_POINTER;
        *value = nullptr;
        return E_NOTIMPL;
    }
};
class FakeManager final : public ral::ComObject<IWTSVirtualChannelManager> {
public:
    ComPtr<IWTSListenerCallback> callback;
    bool failListener{};
    HRESULT STDMETHODCALLTYPE CreateListener(const unsigned char* name, ULONG flags,
        IWTSListenerCallback* handler, IWTSListener** listener) noexcept override {
        if (!listener || !handler || !name || std::strcmp(reinterpret_cast<const char*>(name), "RALink.v1") || flags)
            return E_INVALIDARG;
        if (failListener) { *listener = nullptr; return E_FAIL; }
        callback = handler;
        *listener = new (std::nothrow) FakeListener;
        return *listener ? S_OK : E_OUTOFMEMORY;
    }
};
class FakeChannel final : public ral::ComObject<IWTSVirtualChannel> {
public:
    ral::WinHandle closed{CreateEventW(nullptr, TRUE, FALSE, nullptr)};
    ral::WinHandle written{CreateEventW(nullptr, TRUE, FALSE, nullptr)};
    std::mutex lock;
    std::vector<BYTE> bytes;
    std::atomic<unsigned> closeCount{};
    ComPtr<IWTSVirtualChannelCallback> callback;
    HRESULT STDMETHODCALLTYPE Write(ULONG size, BYTE* data, IUnknown* reserved) noexcept override {
        if (!data || size > ral::TransportChunkBytes || reserved) return E_INVALIDARG;
        try {
            std::lock_guard guard(lock);
            bytes.insert(bytes.end(), data, data + size);
            SetEvent(written.get());
            return S_OK;
        } catch (...) { return E_OUTOFMEMORY; }
    }
    HRESULT STDMETHODCALLTYPE Close() noexcept override {
        ++closeCount;
        if (callback) callback->OnClose();
        SetEvent(closed.get());
        return S_OK;
    }
};

std::wstring clientTestPipe;
HRESULT ClientTestPipe(std::wstring& name) noexcept {
    try { name = clientTestPipe; return S_OK; }
    catch (...) { return E_OUTOFMEMORY; }
}

ral::WinHandle ClientTestServer() {
    ral::WinHandle pipe(CreateNamedPipeW(clientTestPipe.c_str(), PIPE_ACCESS_DUPLEX |
        FILE_FLAG_OVERLAPPED | FILE_FLAG_FIRST_PIPE_INSTANCE,
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
        1, 4096, 4096, 0, nullptr));
    Check(static_cast<bool>(pipe), "create only a random test-owned lifecycle pipe");
    return pipe;
}

void WaitClientReporter(HANDLE pipe) {
    ral::WinHandle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
    OVERLAPPED operation{};
    operation.hEvent = event.get();
    if (ConnectNamedPipe(pipe, &operation) || GetLastError() == ERROR_PIPE_CONNECTED) return;
    Check(GetLastError() == ERROR_IO_PENDING, "pending lifecycle connect");
    const auto wait = WaitForSingleObject(event.get(), 8000);
    DWORD transferred{};
    if (wait != WAIT_OBJECT_0) {
        CancelIoEx(pipe, &operation);
        GetOverlappedResult(pipe, &operation, &transferred, TRUE);
    }
    Check(wait == WAIT_OBJECT_0 && GetOverlappedResult(pipe, &operation, &transferred, FALSE),
        "local lifecycle connects independently of DVC");
}

std::array<BYTE, 48> ReadClientReport(HANDLE pipe, DWORD state) {
    const auto deadline = GetTickCount64() + 8000;
    while (GetTickCount64() < deadline) {
        std::array<BYTE, 48> bytes{};
        DWORD offset{};
        while (offset < bytes.size()) {
            ral::WinHandle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
            OVERLAPPED operation{};
            operation.hEvent = event.get();
            DWORD read{};
            if (!ReadFile(pipe, bytes.data() + offset, static_cast<DWORD>(bytes.size()) - offset, &read, &operation)) {
                Check(GetLastError() == ERROR_IO_PENDING, "read lifecycle record");
                const auto wait = WaitForSingleObject(event.get(), 8000);
                if (wait != WAIT_OBJECT_0) {
                    CancelIoEx(pipe, &operation);
                    GetOverlappedResult(pipe, &operation, &read, TRUE);
                }
                Check(wait == WAIT_OBJECT_0 && GetOverlappedResult(pipe, &operation, &read, FALSE),
                    "bounded lifecycle record read");
            }
            Check(read != 0, "lifecycle record is not truncated");
            offset += read;
        }
        Check(std::memcmp(bytes.data(), "RALCLT1", 8) == 0, "lifecycle protocol signature");
        DWORD actual{}, reserved{};
        std::memcpy(&actual, bytes.data() + 40, sizeof(actual));
        std::memcpy(&reserved, bytes.data() + 44, sizeof(reserved));
        Check(reserved == 0 && actual <= 2, "bounded lifecycle state and reserved bytes");
        GUID instance{}, epoch{};
        std::memcpy(&instance, bytes.data() + 8, sizeof(instance));
        std::memcpy(&epoch, bytes.data() + 24, sizeof(epoch));
        Check(instance != GUID_NULL && epoch != GUID_NULL, "lifecycle identities are nonzero");
        if (actual == state) return bytes;
    }
    throw std::runtime_error("lifecycle state transition was not reported");
}

void ClientLifecycleTests() {
    GUID unique{};
    Check(SUCCEEDED(CoCreateGuid(&unique)), "random lifecycle fixture identity");
    std::array<wchar_t, 40> text{};
    Check(StringFromGUID2(unique, text.data(), static_cast<int>(text.size())) != 0, "format fixture identity");
    clientTestPipe = L"\\\\.\\pipe\\RemoteAgentLink.Test.ClientLifecycle." + std::wstring(text.data());
    ComPtr<IWTSPluginFactory> factory;
    Check(SUCCEEDED(ral::CreatePluginFactory(ral::PluginLoadingMode::Modern, &factory, ClientTestPipe)),
        "inject isolated lifecycle pipe without changing production transport");
    ComPtr<IWTSPlugin> plugin;
    Check(SUCCEEDED(factory->CreatePlugin(0, &plugin)), "create lifecycle plugin");
    ComPtr<FakeManager> manager;
    manager.Attach(new FakeManager);
    Check(SUCCEEDED(plugin->Initialize(manager.Get())), "initialize without a broker or DVC channel");
    auto pipe = ClientTestServer();
    WaitClientReporter(pipe.get());
    const auto initialized = ReadClientReport(pipe.get(), 0);
    Check(SUCCEEDED(plugin->Connected()), "lifecycle connected callback");
    const auto connected = ReadClientReport(pipe.get(), 1);
    Check(std::memcmp(initialized.data() + 8, connected.data() + 8, 16) == 0,
        "connect retains plugin instance identity");
    Check(SUCCEEDED(plugin->Disconnected(0)), "lifecycle disconnect callback");
    const auto disconnected = ReadClientReport(pipe.get(), 2);
    Check(std::memcmp(connected.data() + 8, disconnected.data() + 8, 32) == 0,
        "disconnect retains identity and last epoch");
    Check(SUCCEEDED(plugin->Connected()), "lifecycle reconnect callback");
    const auto reconnected = ReadClientReport(pipe.get(), 1);
    Check(std::memcmp(connected.data() + 8, reconnected.data() + 8, 16) == 0 &&
        std::memcmp(connected.data() + 24, reconnected.data() + 24, 16) != 0,
        "reconnect retains instance and invalidates epoch");
    pipe.reset();
    pipe = ClientTestServer();
    WaitClientReporter(pipe.get());
    const auto restarted = ReadClientReport(pipe.get(), 1);
    Check(restarted == reconnected, "broker restart republishes the same target without DVC");
    Check(SUCCEEDED(plugin->Terminated()), "terminate lifecycle worker without joining in callback");
    plugin.Reset();
    manager.Reset();
    pipe.reset();
    pipe = ClientTestServer();
    Check(SUCCEEDED(factory->CreatePlugin(0, &plugin)), "create independent lifecycle-only plugin");
    manager.Attach(new FakeManager);
    manager->failListener = true;
    Check(SUCCEEDED(plugin->Initialize(manager.Get())) && !manager->callback,
        "listener failure does not unload independent client discovery");
    Check(SUCCEEDED(plugin->Connected()), "report an RDP connection even when listener creation failed");
    WaitClientReporter(pipe.get());
    const auto withoutListener = ReadClientReport(pipe.get(), 1);
    Check(std::memcmp(withoutListener.data() + 8, connected.data() + 8, 16) != 0,
        "independent plugin instances never share identity");
    Check(SUCCEEDED(plugin->Terminated()), "stop lifecycle-only plugin");
    plugin.Reset();
    manager.Reset();
    factory.Reset();
}

void AbiTests() {
    uint32_t error = 99, count = 99;
    Check(!RAL_Open(nullptr, &error) && error == ERROR_INVALID_PARAMETER, "open null name");
    Check(!RAL_Open(L"other", &error) && error == ERROR_INVALID_PARAMETER, "open only approved channel");
    Check(!RAL_Open(L"RALink.v1", nullptr), "open requires error");
    Check(RAL_Read(nullptr, nullptr, 0, 0, &count) == ERROR_INVALID_PARAMETER && count == 0, "read invalid context");
    Check(RAL_Write(nullptr, nullptr, 0, &count) == ERROR_INVALID_PARAMETER && count == 0, "write invalid context");
    Check(RAL_Read(nullptr, nullptr, 0, 0, nullptr) == ERROR_INVALID_PARAMETER, "read invalid output");
    Check(RAL_Write(nullptr, nullptr, 0, nullptr) == ERROR_INVALID_PARAMETER, "write invalid output");
    RAL_Close(nullptr);
}

void ProxyTests() {
    std::array<wchar_t, 32768> modulePath{};
    const auto length = GetModuleFileNameW(nullptr, modulePath.data(), static_cast<DWORD>(modulePath.size()));
    Check(length && length < modulePath.size(), "find test executable");
    std::wstring path(modulePath.data(), length);
    path = path.substr(0, path.find_last_of(L'\\') + 1) + L"AgentLink.ProxyStub.dll";
    struct Library {
        HMODULE value;
        ~Library() { if (value) FreeLibrary(value); }
    } library{LoadLibraryExW(path.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32)};
    Check(library.value != nullptr, "load generated proxy DLL without registration");
    Check(GetProcAddress(library.value, "GetProxyDllInfo") != nullptr, "proxy metadata export");
    const CLSID expected{0x586b220f, 0x6970, 0x4e64, {0xb5, 0xdd, 0x43, 0x8a, 0x29, 0x8a, 0x87, 0x6a}};
    const std::array<IID, 10> interfaces{__uuidof(IWTSPluginFactory), __uuidof(IWTSPlugin),
        __uuidof(IWTSListener), __uuidof(IWTSListenerCallback), __uuidof(IWTSVirtualChannelCallback),
        __uuidof(IWTSVirtualChannelManager), __uuidof(IWTSVirtualChannel),
        __uuidof(IWTSPluginServiceProvider), __uuidof(IWTSWindowInfoService), __uuidof(IWTSWindowChangedCallback)};
    const std::array publicSurfaceIids{
        L"{D3E07363-087C-476C-86A7-DBB15F46DDB4}",
        L"{2A002C7B-0120-4D04-BFB1-3C73110C8581}",
        L"{E8A47FD3-1AF6-4D7F-8E15-515CCAE1C00C}"};
    for (size_t i = 0; i < publicSurfaceIids.size(); ++i) {
        IID expectedIid{};
        Check(SUCCEEDED(IIDFromString(publicSurfaceIids[i], &expectedIid)) && expectedIid == interfaces[i + 7],
            "surface interface IID exactly matches the licensed public contract");
    }
    using GetFactory = HRESULT (STDAPICALLTYPE*)(REFCLSID, REFIID, void**);
    GetFactory getFactory{};
    auto address = GetProcAddress(library.value, "DllGetClassObject");
    std::memcpy(&getFactory, &address, sizeof(getFactory));
    Check(getFactory != nullptr, "proxy class factory export");
    ComPtr<IPSFactoryBuffer> factory;
    Check(SUCCEEDED(getFactory(expected, IID_PPV_ARGS(&factory))), "activate proxy class factory in memory");
    for (const auto& iid : interfaces) {
        ComPtr<IRpcProxyBuffer> proxy;
        void* raw{};
        Check(SUCCEEDED(factory->CreateProxy(nullptr, iid, &proxy, &raw)) && raw, "construct generated interface proxy");
        static_cast<IUnknown*>(raw)->Release();
    }
    Check(!GetProcAddress(library.value, "DllRegisterServer"), "no registry self-registration export");
}

void PluginTests() {
    ComPtr<IWTSPluginFactory> factory;
    Check(SUCCEEDED(ral::CreatePluginFactory(ral::PluginLoadingMode::Modern, &factory, NoClientReporter)), "create factory");
    long count{};
    Check(factory->GetPluginCount(nullptr) == E_POINTER, "factory count pointer");
    Check(SUCCEEDED(factory->GetPluginCount(&count)) && count == 1, "factory count ABI");
    ComPtr<IWTSPlugin> first, second;
    Check(factory->CreatePlugin(1, &first) == E_INVALIDARG && !first, "factory index boundary");
    Check(SUCCEEDED(factory->CreatePlugin(0, &first)) && SUCCEEDED(factory->CreatePlugin(0, &second)) &&
        first.Get() != second.Get(), "independent plugin objects");
    ComPtr<FakeManager> manager;
    manager.Attach(new FakeManager);
    Check(first->Initialize(nullptr) == E_POINTER, "initialize pointer");
    Check(SUCCEEDED(first->Initialize(manager.Get())) && manager->callback, "listener created");
    Check(first->Initialize(manager.Get()) == E_UNEXPECTED, "initialize once");
    Check(SUCCEEDED(first->Connected()), "connected");

    std::wstring pipeName;
    Check(SUCCEEDED(ral::GetBrokerPipeName(pipeName)), "broker pipe identity");
    ral::WinHandle pipe(CreateNamedPipeW(pipeName.c_str(), PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED |
        FILE_FLAG_FIRST_PIPE_INSTANCE, PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
        1, 65536, 65536, 0, nullptr));
    Check(static_cast<bool>(pipe), "isolated test broker pipe (must not replace an existing broker)");
    ral::WinHandle connectEvent(CreateEventW(nullptr, TRUE, FALSE, nullptr));
    OVERLAPPED connect{};
    connect.hEvent = connectEvent.get();
    const auto connected = ConnectNamedPipe(pipe.get(), &connect);
    Check(connected || GetLastError() == ERROR_IO_PENDING, "start broker pipe accept");

    ComPtr<FakeChannel> channel;
    channel.Attach(new FakeChannel);
    BOOL accepted{};
    ComPtr<IWTSVirtualChannelCallback> callback;
    Check(SUCCEEDED(manager->callback->OnNewChannelConnection(channel.Get(), nullptr, &accepted, &callback)) &&
        accepted && callback, "accept channel");
    channel->callback = callback;
    Check(WaitForSingleObject(connectEvent.get(), 3000) == WAIT_OBJECT_0, "worker pipe connects");
    DWORD transferred{};
    Check(GetOverlappedResult(pipe.get(), &connect, &transferred, FALSE) != FALSE, "accept completes");

    const std::array<BYTE, 6> inbound{0x16, 0x03, 0x03, 0, 1, 0xab};
    Check(SUCCEEDED(callback->OnDataReceived(static_cast<ULONG>(inbound.size()), const_cast<BYTE*>(inbound.data()))),
        "enqueue TLS bytes");
    std::array<BYTE, 64> received{};
    ral::WinHandle ioEvent(CreateEventW(nullptr, TRUE, FALSE, nullptr));
    OVERLAPPED operation{};
    operation.hEvent = ioEvent.get();
    auto done = ReadFile(pipe.get(), received.data(), static_cast<DWORD>(received.size()), &transferred, &operation);
    Check(done || GetLastError() == ERROR_IO_PENDING, "broker read starts");
    if (!done) {
        Check(WaitForSingleObject(ioEvent.get(), 3000) == WAIT_OBJECT_0, "broker read completes");
        Check(GetOverlappedResult(pipe.get(), &operation, &transferred, FALSE) != FALSE, "broker read result");
    }
    Check(transferred == inbound.size() && std::equal(inbound.begin(), inbound.end(), received.begin()),
        "raw DVC to pipe: no framing/handshake bytes");

    std::vector<BYTE> outbound(ral::TransportChunkBytes * 3 + 7);
    for (size_t i = 0; i < outbound.size(); ++i) outbound[i] = static_cast<BYTE>(i);
    ResetEvent(ioEvent.get());
    operation = {};
    operation.hEvent = ioEvent.get();
    done = WriteFile(pipe.get(), outbound.data(), static_cast<DWORD>(outbound.size()), &transferred, &operation);
    Check(done || GetLastError() == ERROR_IO_PENDING, "broker write starts");
    if (!done) {
        Check(WaitForSingleObject(ioEvent.get(), 3000) == WAIT_OBJECT_0, "broker write completes");
        Check(GetOverlappedResult(pipe.get(), &operation, &transferred, FALSE) != FALSE, "broker write result");
    }
    const auto deadline = GetTickCount64() + 3000;
    bool equal{};
    do {
        {
            std::lock_guard lock(channel->lock);
            equal = channel->bytes == outbound;
        }
        if (equal) break;
        Sleep(10);
    } while (GetTickCount64() < deadline);
    Check(equal, "raw pipe to DVC serialized 16KiB chunks");
    Check(SUCCEEDED(first->Disconnected(0)), "disconnect callback");
    Check(WaitForSingleObject(channel->closed.get(), 3000) == WAIT_OBJECT_0 && channel->closeCount == 1,
        "disconnect cancels pending pipe read and explicitly closes once");
    Check(FAILED(callback->OnDataReceived(1, received.data())), "reject data after disconnect");
    BOOL rejected = TRUE;
    ComPtr<IWTSVirtualChannelCallback> refused;
    Check(SUCCEEDED(manager->callback->OnNewChannelConnection(channel.Get(), nullptr, &rejected, &refused)) &&
        !rejected && !refused, "disconnect rejects new channels until reconnect");
    channel->callback.Reset();
    callback.Reset();
    Check(SUCCEEDED(first->Terminated()) && first->Connected() == E_UNEXPECTED, "termination final");
    Check(SUCCEEDED(second->Connected()), "other plugin unaffected");
}

void FailureTests() {
    ComPtr<IWTSPluginFactory> factory;
    ComPtr<IWTSPlugin> plugin;
    ComPtr<FakeManager> manager;
    manager.Attach(new FakeManager);
    Check(SUCCEEDED(ral::CreatePluginFactory(ral::PluginLoadingMode::Modern, &factory, NoClientReporter)) &&
        SUCCEEDED(factory->CreatePlugin(0, &plugin)) &&
        SUCCEEDED(plugin->Initialize(manager.Get())), "failure fixture");
    const auto open = [&] {
        ComPtr<FakeChannel> channel;
        channel.Attach(new FakeChannel);
        BOOL accepted{};
        Check(SUCCEEDED(manager->callback->OnNewChannelConnection(channel.Get(), nullptr, &accepted, &channel->callback)) &&
            accepted, "failure channel accepted asynchronously");
        return channel;
    };
    {
        auto channel = open();
        Check(WaitForSingleObject(channel->closed.get(), 8000) == WAIT_OBJECT_0 && channel->closeCount == 1,
            "missing broker bounded retry explicitly closes channel");
        channel->callback.Reset();
    }
    {
        auto channel = open();
        std::vector<BYTE> payload(ral::MaxRecordBytes);
        Check(SUCCEEDED(channel->callback->OnDataReceived(static_cast<ULONG>(payload.size()), payload.data())),
            "queue first 16MiB");
        Check(SUCCEEDED(channel->callback->OnDataReceived(static_cast<ULONG>(payload.size()), payload.data())),
            "queue exactly 32MiB");
        Check(channel->callback->OnDataReceived(1, payload.data()) == HRESULT_FROM_WIN32(ERROR_NOT_ENOUGH_QUOTA),
            "queue cap rejects before allocation");
        Check(WaitForSingleObject(channel->closed.get(), 3000) == WAIT_OBJECT_0, "queue overflow closes channel");
        channel->callback.Reset();
    }
    {
        auto channel = open();
        BYTE byte{};
        Check(channel->callback->OnDataReceived(ral::MaxRecordBytes + 1, &byte) == HRESULT_FROM_WIN32(ERROR_FILE_TOO_LARGE),
            "oversized callback rejected before copying");
        Check(WaitForSingleObject(channel->closed.get(), 3000) == WAIT_OBJECT_0, "oversized callback closes channel");
        channel->callback.Reset();
    }
    {
        auto channel = open();
        Check(SUCCEEDED(channel->callback->OnClose()), "remote OnClose cancels connection retry");
        channel->callback.Reset();
        Check(SUCCEEDED(plugin->Terminated()), "terminate during cancellation");
        Check(channel->closeCount == 0, "remote OnClose avoids redundant close RPC");
    }
}
}

namespace {
void PluginStartupTests() {
    std::array<wchar_t, 32768> module{};
    const auto length = GetModuleFileNameW(nullptr, module.data(), static_cast<DWORD>(module.size()));
    Check(length > 0 && length < module.size(), "native test executable path");
    std::wstring path(module.data(), length);
    const auto separator = path.find_last_of(L'\\');
    Check(separator != std::wstring::npos, "native test executable directory");
    path.resize(separator + 1);
    path += L"AgentLink.RdpPlugin.exe";
    auto commandLine = L"\"" + path + L"\" --check-startup";
    STARTUPINFOW startup{sizeof(startup)};
    PROCESS_INFORMATION process{};
    Check(CreateProcessW(path.c_str(), commandLine.data(), nullptr, nullptr, FALSE,
        CREATE_NO_WINDOW, nullptr, nullptr, &startup, &process) != FALSE, "start production COM initialization probe");
    ral::WinHandle processHandle(process.hProcess);
    ral::WinHandle threadHandle(process.hThread);
    const auto waited = WaitForSingleObject(processHandle.get(), 10000);
    if (waited != WAIT_OBJECT_0) {
        TerminateProcess(processHandle.get(), 1);
        WaitForSingleObject(processHandle.get(), 5000);
    }
    Check(waited == WAIT_OBJECT_0, "COM initialization probe exits without waiting for activation");
    DWORD exitCode{};
    Check(GetExitCodeProcess(processHandle.get(), &exitCode) != FALSE, "COM initialization probe exit code");
    Check(exitCode == 0, "fast rundown is configured before COM security initialization");
}

void PackageManifestTests() {
    Microsoft::WRL::ComPtr<IAppxFactory> factory;
    Check(SUCCEEDED(CoCreateInstance(CLSID_AppxFactory, nullptr, CLSCTX_INPROC_SERVER,
        IID_PPV_ARGS(&factory))), "Windows package manifest factory");
    Microsoft::WRL::ComPtr<IStream> stream;
    Check(SUCCEEDED(SHCreateStreamOnFileEx(L"packaging\\AgentLink.Msix\\Package.appxmanifest",
        STGM_READ | STGM_SHARE_DENY_WRITE, 0, FALSE, nullptr, &stream)), "package manifest stream");
    STATSTG stat{};
    Check(SUCCEEDED(stream->Stat(&stat, STATFLAG_NONAME)) && stat.cbSize.QuadPart < 65536, "bounded manifest source");
    std::string xml(static_cast<size_t>(stat.cbSize.QuadPart), '\0');
    ULONG bytes{};
    Check(SUCCEEDED(stream->Read(xml.data(), static_cast<ULONG>(xml.size()), &bytes)) && bytes == xml.size(),
        "read manifest interface registrations");
    for (const auto iid : {"D3E07363-087C-476C-86A7-DBB15F46DDB4", "2A002C7B-0120-4D04-BFB1-3C73110C8581",
        "E8A47FD3-1AF6-4D7F-8E15-515CCAE1C00C"}) {
        const auto registration = std::string("<com:Interface Id=\"") + iid +
            "\" ProxyStubClsid=\"586B220F-6970-4E64-B5DD-438A298A876A\"";
        const auto found = xml.find(registration);
        Check(found != std::string::npos && xml.find(registration, found + 1) == std::string::npos,
            "each public surface interface has exactly one owned packaged proxy mapping");
    }
    LARGE_INTEGER beginning{};
    Check(SUCCEEDED(stream->Seek(beginning, STREAM_SEEK_SET, nullptr)), "rewind manifest for Windows schema validation");
    Microsoft::WRL::ComPtr<IAppxManifestReader> reader;
    const auto parsed = factory->CreateManifestReader(stream.Get(), &reader);
    if (FAILED(parsed)) {
        std::fprintf(stderr, "Manifest validation HRESULT: 0x%08lX\n", static_cast<unsigned long>(parsed));
        Microsoft::WRL::ComPtr<IErrorInfo> information;
        if (GetErrorInfo(0, &information) == S_OK) {
            BSTR description{};
            if (SUCCEEDED(information->GetDescription(&description)) && description) {
                std::fwprintf(stderr, L"%ls\n", description);
                SysFreeString(description);
            }
        }
    }
    Check(SUCCEEDED(parsed), "dual-role package manifest passes the Windows schema reader");
    Microsoft::WRL::ComPtr<IAppxManifestApplicationsEnumerator> applications;
    Check(SUCCEEDED(reader->GetApplications(&applications)), "package applications");
    BOOL current{};
    unsigned count{};
    Check(SUCCEEDED(applications->GetHasCurrent(&current)), "package application enumeration");
    while (current) {
        ++count;
        Check(SUCCEEDED(applications->MoveNext(&current)), "next package application");
    }
    Check(count == 3, "package contains broker, MCP, and RDP plugin applications");
}
}

int main(int argc, char** argv) {
    if (argc == 6 && std::string_view(argv[1]) == "--activation-server")
        return CrossProcessActivationServer(argv + 2);
    const bool manifestOnly = argc == 2 && std::string_view(argv[1]) == "--manifest-only";
    const bool startupOnly = argc == 2 && std::string_view(argv[1]) == "--startup-only";
    const bool activationOnly = argc == 2 && std::string_view(argv[1]) == "--activation-only";
    const bool clientOnly = argc == 2 && std::string_view(argv[1]) == "--client-lifecycle-only";
    if (argc != 1 && !manifestOnly && !startupOnly && !activationOnly && !clientOnly) {
        std::fprintf(stderr, "Usage: Native.Tests.exe [--manifest-only|--startup-only|--activation-only|--client-lifecycle-only]\n");
        return 2;
    }
    const auto initialized = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(initialized)) return 1;
    int result{};
    {
        ral::ServerLease keepAlive;
        try {
            if (!manifestOnly && !startupOnly && !clientOnly) {
                ProxyTests();
                checks += ActivationTests();
                checks += CrossProcessActivationTests();
            }
            if (argc == 1 || clientOnly) ClientLifecycleTests();
            if (!manifestOnly && !startupOnly && !activationOnly && !clientOnly) {
                ParserTests();
                AbiTests();
                PluginTests();
                FailureTests();
            }
            if (!manifestOnly && !activationOnly && !clientOnly) PluginStartupTests();
            if (!startupOnly && !activationOnly && !clientOnly) PackageManifestTests();
            const auto deadline = GetTickCount64() + 3000;
            while (ral::ServerLeaseCount.load() != 1 && GetTickCount64() < deadline) Sleep(10);
            Check(ral::ServerLeaseCount.load() == 1, "all COM objects and background workers released");
            std::printf("PASS: %u native checks (%s).\n", checks,
                clientOnly ? "isolated client lifecycle reporting (no production broker pipe or DVC)" :
                activationOnly ? "isolated loading modes, COM activation and surface marshaling (test-owned pipes only)" :
                startupOnly ? "COM executable startup" :
                manifestOnly ? "package manifest" : "activation, PDU parser, DLL ABI, raw duplex bridge, cancellation, COM lifetime, executable startup, package manifest");
        } catch (const std::exception& error) {
            std::fprintf(stderr, "FAIL after %u checks: %s\n", checks, error.what());
            result = 1;
        }
    }
    CoUninitialize();
    return result;
}
