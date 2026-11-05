#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <string>
#include <vector>
#include <iostream>

typedef int (__cdecl *hostfxr_main_fn)(const int argc, const wchar_t** argv);

static bool FileExists(const std::wstring& path) {
    DWORD dwAttrib = GetFileAttributesW(path.c_str());
    return (dwAttrib != INVALID_FILE_ATTRIBUTES && !(dwAttrib & FILE_ATTRIBUTE_DIRECTORY));
}

static bool RunCommand(const std::wstring& cmd) {
    STARTUPINFOW si;
    PROCESS_INFORMATION pi;
    ZeroMemory(&si, sizeof(si));
    si.cb = sizeof(si);
    ZeroMemory(&pi, sizeof(pi));

    std::vector<wchar_t> cmdBuf(cmd.begin(), cmd.end());
    cmdBuf.push_back(0);

    if (CreateProcessW(NULL, cmdBuf.data(), NULL, NULL, TRUE, 0, NULL, NULL, &si, &pi)) {
        WaitForSingleObject(pi.hProcess, INFINITE);
        DWORD exitCode = 0;
        GetExitCodeProcess(pi.hProcess, &exitCode);
        CloseHandle(pi.hProcess);
        CloseHandle(pi.hThread);
        return (exitCode == 0);
    }
    return false;
}

static bool DownloadAndExtractRuntime(const std::wstring& appDir, const std::wstring& netAvaloniaDir) {
    AllocConsole();
    SetConsoleOutputCP(CP_UTF8);
    SetConsoleTitleW(L"Comic Downloader GMTPC - Khoi tao Thu vien Portable");

    std::wcout << L"==============================================================" << std::endl;
    std::wcout << L"  Comic Downloader GMTPC - Khoi tao Thu vien .NET Avalonia 10" << std::endl;
    std::wcout << L"==============================================================" << std::endl;
    std::wcout << L"[*] Phat hien chua co bo thu vien tai:" << std::endl;
    std::wcout << L"    .portable\\bin\\net avalonia\\" << std::endl;
    std::wcout << L"[*] Dang tu dong tai bo thu vien Portable tu GitHub Release..." << std::endl;
    std::wcout << L"    https://github.com/ghostminhtoan/comic.downloader-GMTPC-MultiOS/releases/download/beta/runtime-net10-win-x64.zip" << std::endl;
    std::wcout << L"--------------------------------------------------------------" << std::endl;

    std::wstring tmpDir = appDir + L"\\.tmp";
    CreateDirectoryW(tmpDir.c_str(), NULL);
    std::wstring zipPath = tmpDir + L"\\runtime-net10-win-x64.zip";

    std::wstring curlCmd = L"curl.exe -L --progress-bar -o \"" + zipPath + L"\" \"https://github.com/ghostminhtoan/comic.downloader-GMTPC-MultiOS/releases/download/beta/runtime-net10-win-x64.zip\"";
    if (!RunCommand(curlCmd) || !FileExists(zipPath)) {
        std::wcout << L"\n[ERROR] Tai thu vien khong thanh cong! Vui long kiem tra ket noi mang." << std::endl;
        std::wcout << L"Nhan phim bat ky de thoat..." << std::endl;
        system("pause >nul");
        FreeConsole();
        return false;
    }

    std::wcout << L"\n[*] Dang giai nen bo thu vien vao .portable\\bin\\net avalonia\\..." << std::endl;
    CreateDirectoryW((appDir + L"\\.portable").c_str(), NULL);
    CreateDirectoryW((appDir + L"\\.portable\\bin").c_str(), NULL);
    CreateDirectoryW(netAvaloniaDir.c_str(), NULL);

    std::wstring tarCmd = L"tar.exe -xf \"" + zipPath + L"\" -C \"" + netAvaloniaDir + L"\"";
    RunCommand(tarCmd);

    DeleteFileW(zipPath.c_str());

    std::wcout << L"[*] Khoi tao thu vien hoan tat 100%!" << std::endl;
    Sleep(800);
    FreeConsole();
    return true;
}

int APIENTRY wWinMain(HINSTANCE hInstance, HINSTANCE hPrevInstance, LPWSTR lpCmdLine, int nCmdShow) {
    wchar_t exePathBuf[MAX_PATH];
    GetModuleFileNameW(NULL, exePathBuf, MAX_PATH);
    std::wstring exePath(exePathBuf);
    std::wstring appDir = exePath.substr(0, exePath.find_last_of(L"\\/"));

    std::wstring netAvaloniaDir = appDir + L"\\.portable\\bin\\net avalonia";
    std::wstring appDll = netAvaloniaDir + L"\\net avalonia runtimes.dll";
    std::wstring fxrDll = netAvaloniaDir + L"\\hostfxr.dll";

    // Fallback: Nếu chưa có net avalonia runtimes.dll nhưng có ComicDownloaderGMTPC.Desktop.dll
    if (!FileExists(appDll)) {
        std::wstring altDll = netAvaloniaDir + L"\\ComicDownloaderGMTPC.Desktop.dll";
        if (FileExists(altDll)) {
            appDll = altDll;
        }
    }

    // Tự động tải nếu thiếu
    if (!FileExists(appDll) || !FileExists(fxrDll)) {
        if (!DownloadAndExtractRuntime(appDir, netAvaloniaDir)) {
            return 1;
        }
        if (!FileExists(appDll)) {
            std::wstring altDll = netAvaloniaDir + L"\\ComicDownloaderGMTPC.Desktop.dll";
            if (FileExists(altDll)) {
                appDll = altDll;
            }
        }
    }

    SetEnvironmentVariableW(L"COMIC_DOWNLOADER_ROOT", appDir.c_str());
    SetEnvironmentVariableW(L"DOTNET_ROOT", netAvaloniaDir.c_str());

    HMODULE hFxr = LoadLibraryW(fxrDll.c_str());
    if (!hFxr) {
        MessageBoxW(NULL, L"Khong the nap hostfxr.dll tu thu muc .portable\\bin\\net avalonia\\", L"Loi khoi dong Comic Downloader GMTPC", MB_OK | MB_ICONERROR);
        return 1;
    }

    hostfxr_main_fn pfnHostFxrMain = (hostfxr_main_fn)GetProcAddress(hFxr, "hostfxr_main");
    if (!pfnHostFxrMain) {
        MessageBoxW(NULL, L"Khong tim thay ham hostfxr_main trong hostfxr.dll", L"Loi khoi dong Comic Downloader GMTPC", MB_OK | MB_ICONERROR);
        return 1;
    }

    int argc = 0;
    LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);

    std::vector<const wchar_t*> hostArgv;
    hostArgv.push_back(exePath.c_str());
    hostArgv.push_back(appDll.c_str());
    for (int i = 1; i < argc; ++i) {
        hostArgv.push_back(argv[i]);
    }

    int exitCode = pfnHostFxrMain((int)hostArgv.size(), hostArgv.data());
    LocalFree(argv);
    return exitCode;
}

