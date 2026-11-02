using System.Runtime.InteropServices;
using System.Text;

namespace MultiClaude;

/// <summary>Win32 and shell interop: process command lines, windows, AppUserModelIDs, shortcuts.</summary>
internal static class Native
{
    // ---- processes -------------------------------------------------------

    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    const int ProcessCommandLineInformation = 60;
    const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageNameW(IntPtr h, int flags, StringBuilder path, ref int size);

    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(IntPtr h, int infoClass, IntPtr info, int length, out int returnLength);

    /// <summary>Executable path, command line and parent of a process we are allowed to query, or nulls.</summary>
    public static (string? Exe, string? CommandLine, int ParentPid) QueryProcess(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return (null, null, 0);
        try
        {
            // PROCESS_BASIC_INFORMATION: six pointer-sized fields, the last is the parent's id.
            int parent = 0;
            int pbiSize = IntPtr.Size * 6;
            IntPtr pbi = Marshal.AllocHGlobal(pbiSize);
            try
            {
                if (NtQueryInformationProcess(h, 0, pbi, pbiSize, out _) == 0)
                    parent = (int)Marshal.ReadIntPtr(pbi, IntPtr.Size * 5).ToInt64();
            }
            finally { Marshal.FreeHGlobal(pbi); }

            string? exe = null;
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            if (QueryFullProcessImageNameW(h, 0, sb, ref size)) exe = sb.ToString(0, size);

            string? cmd = null;
            NtQueryInformationProcess(h, ProcessCommandLineInformation, IntPtr.Zero, 0, out int needed);
            if (needed > 0)
            {
                IntPtr buf = Marshal.AllocHGlobal(needed);
                try
                {
                    if (NtQueryInformationProcess(h, ProcessCommandLineInformation, buf, needed, out _) == 0)
                    {
                        // UNICODE_STRING { USHORT Length; USHORT MaximumLength; PWSTR Buffer; }
                        int len = Marshal.ReadInt16(buf) & 0xFFFF;
                        IntPtr str = Marshal.ReadIntPtr(buf, IntPtr.Size);
                        if (str != IntPtr.Zero && len > 0) cmd = Marshal.PtrToStringUni(str, len / 2);
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            return (exe, cmd, parent);
        }
        finally { CloseHandle(h); }
    }

    // ---- windows ---------------------------------------------------------

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool AllowSetForegroundWindow(int pid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeoutW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    const uint GW_OWNER = 4;
    const int GWL_EXSTYLE = -20;
    const long WS_EX_TOOLWINDOW = 0x80;
    public const int SW_RESTORE = 9;
    public const int ASFW_ANY = -1;

    public static int WindowProcessId(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out int pid);
        return pid;
    }

    /// <summary>Top-level, unowned, non-tool windows of a process: the ones the taskbar shows.</summary>
    public static List<IntPtr> AppWindows(int pid)
    {
        var list = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out int p);
            if (p != pid) return true;
            if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;
            long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            if ((ex & WS_EX_TOOLWINDOW) != 0) return true;
            list.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static void SetWindowIcons(IntPtr hwnd, Icon small, Icon big)
    {
        const uint WM_SETICON = 0x80, SMTO_ABORTIFHUNG = 2;
        SendMessageTimeoutW(hwnd, WM_SETICON, (IntPtr)0, small.Handle, SMTO_ABORTIFHUNG, 500, out _);
        SendMessageTimeoutW(hwnd, WM_SETICON, (IntPtr)1, big.Handle, SMTO_ABORTIFHUNG, 500, out _);
    }

    // ---- AppUserModelID (taskbar grouping) ------------------------------

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
        public PropertyKey(Guid f, uint p) { FormatId = f; PropertyId = p; }
    }

    // PROPVARIANT is 16 bytes on x86 and 24 on x64; only VT_LPWSTR is used here.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr p;

        public static PropVariant FromString(string s) => new() { vt = 31 /* VT_LPWSTR */, p = Marshal.StringToCoTaskMemUni(s) };
        public string? AsString() => vt == 31 && p != IntPtr.Zero ? Marshal.PtrToStringUni(p) : null;
    }

    [DllImport("ole32.dll")]
    static extern int PropVariantClear(ref PropVariant pv);

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    static readonly Guid AumFmt = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    static PropertyKey KeyRelaunchCommand = new(AumFmt, 2);
    static PropertyKey KeyRelaunchIcon = new(AumFmt, 3);
    static PropertyKey KeyRelaunchName = new(AumFmt, 4);
    static PropertyKey KeyId = new(AumFmt, 5);

    [DllImport("shell32.dll")]
    static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    static void Set(IPropertyStore store, PropertyKey key, string value)
    {
        var pv = PropVariant.FromString(value);
        try { store.SetValue(ref key, ref pv); } finally { PropVariantClear(ref pv); }
    }

    static string? Get(IPropertyStore store, PropertyKey key)
    {
        store.GetValue(ref key, out var pv);
        try { return pv.AsString(); } finally { PropVariantClear(ref pv); }
    }

    public static string? GetWindowAppId(IntPtr hwnd)
    {
        var iid = typeof(IPropertyStore).GUID;
        if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0) return null;
        try { return Get(store, KeyId); } catch { return null; } finally { Marshal.ReleaseComObject(store); }
    }

    /// <summary>
    /// Gives a window its own taskbar identity. Relaunch properties must be set before the ID. The
    /// taskbar draws the button from the relaunch icon (without one it uses the relaunch exe's icon).
    /// </summary>
    public static bool SetWindowAppId(IntPtr hwnd, string appId, string relaunchCommand, string displayName, string iconPath)
    {
        var iid = typeof(IPropertyStore).GUID;
        if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0) return false;
        try
        {
            Set(store, KeyRelaunchCommand, relaunchCommand);
            Set(store, KeyRelaunchIcon, iconPath + ",0");
            Set(store, KeyRelaunchName, displayName);
            Set(store, KeyId, appId);
            return true;
        }
        catch { return false; }
        finally { Marshal.ReleaseComObject(store); }
    }

    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    class TaskbarListClass { }

    [ComImport, Guid("56FDF342-FD6D-11d0-958A-006097C9A090"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ITaskbarList
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
    }

    static ITaskbarList? taskbar;

    /// <summary>The taskbar reads a window's ID when it adds its button, so a retagged window is re-added.</summary>
    public static void RefreshTaskbarButton(IntPtr hwnd)
    {
        try
        {
            if (taskbar == null) { taskbar = (ITaskbarList)new TaskbarListClass(); taskbar.HrInit(); }
            taskbar.DeleteTab(hwnd);
            taskbar.AddTab(hwnd);
        }
        catch { }
    }

    // ---- shortcuts -------------------------------------------------------

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr fd, uint flags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int cmd);
        void SetShowCmd(int cmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int cch, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPersistFile
    {
        void GetClassID(out Guid clsid);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string file, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string file, bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string file);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string file);
    }

    public static void CreateShortcut(string lnkPath, string target, string arguments, string iconPath, string description, string? appId)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(target);
            link.SetArguments(arguments);
            link.SetWorkingDirectory(Path.GetDirectoryName(target) ?? "");
            link.SetIconLocation(iconPath, 0);
            link.SetDescription(description);
            if (appId != null)
            {
                var store = (IPropertyStore)link;
                Set(store, KeyId, appId);
                store.Commit();
            }
            Directory.CreateDirectory(Path.GetDirectoryName(lnkPath)!);
            ((IPersistFile)link).Save(lnkPath, true);
        }
        finally { Marshal.ReleaseComObject(link); }
    }

    // ---- foreground changes ---------------------------------------------

    public delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);

    /// <summary>Calls back on this thread whenever another window comes to the front. Keep the delegate alive.</summary>
    public static IntPtr OnForegroundChange(WinEventProc proc)
    {
        const uint EVENT_SYSTEM_FOREGROUND = 3, WINEVENT_OUTOFCONTEXT = 0;
        return SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, proc, 0, 0, WINEVENT_OUTOFCONTEXT);
    }

    // ---- misc ------------------------------------------------------------

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Rounded corners and a dark or light frame on Windows 11; ignored on Windows 10.</summary>
    public static void StyleWindow(IntPtr hwnd, bool dark)
    {
        int round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
        int d = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref d, sizeof(int));
    }

    [DllImport("kernel32.dll")]
    public static extern uint SetThreadExecutionState(uint flags);
    public const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1, ES_DISPLAY_REQUIRED = 0x2;

    [DllImport("user32.dll")]
    public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_NOREPEAT = 0x4000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
