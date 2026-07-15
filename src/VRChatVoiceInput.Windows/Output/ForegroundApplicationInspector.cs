using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace VRChatVoiceInput.Windows.Output;

public static class ForegroundApplicationInspector
{
    public static ForegroundApplication GetCurrent()
    {
        var window = GetForegroundWindow();
        if (window == 0)
        {
            throw new InvalidOperationException("No foreground window is available.");
        }

        GetWindowThreadProcessId(window, out var processId);
        var title = GetWindowTitle(window);
        var processName = GetProcessName(unchecked((int)processId));
        return new ForegroundApplication(window, unchecked((int)processId), processName, title);
    }

    public static IReadOnlyList<RunningApplicationInfo> ListApplications()
    {
        var applications = new List<RunningApplicationInfo>();
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window))
            {
                return true;
            }

            var title = GetWindowTitle(window);
            if (string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            GetWindowThreadProcessId(window, out var processId);
            var signedProcessId = unchecked((int)processId);
            if (signedProcessId == Environment.ProcessId)
            {
                return true;
            }

            applications.Add(new RunningApplicationInfo(
                signedProcessId,
                GetProcessName(signedProcessId),
                title));
            return true;
        }, 0);

        return applications
            .GroupBy(application => application.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(application => application.ProcessName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public static ForegroundApplication Activate(int processId)
    {
        var application = FindByProcessId(processId)
            ?? throw new InvalidOperationException($"No visible window was found for process {processId}.");
        if (IsIconic(application.WindowHandle))
        {
            _ = ShowWindow(application.WindowHandle, 9);
        }

        if (!SetForegroundWindow(application.WindowHandle))
        {
            throw new InvalidOperationException(
                $"Unable to activate target application '{application.DisplayName}'.");
        }

        return application;
    }

    private static ForegroundApplication? FindByProcessId(int processId)
    {
        ForegroundApplication? result = null;
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || string.IsNullOrWhiteSpace(GetWindowTitle(window)))
            {
                return true;
            }

            GetWindowThreadProcessId(window, out var windowProcessId);
            if (unchecked((int)windowProcessId) != processId)
            {
                return true;
            }

            result = new ForegroundApplication(
                window,
                processId,
                GetProcessName(processId),
                GetWindowTitle(window));
            return false;
        }, 0);
        return result;
    }

    private static string GetWindowTitle(nint window)
    {
        var length = GetWindowTextLength(window);
        var buffer = new StringBuilder(length + 1);
        GetWindowText(window, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static string GetProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return $"PID {processId}";
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint window, StringBuilder text, int maximumCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    private delegate bool EnumWindowsCallback(nint window, nint parameter);
}

public sealed record ForegroundApplication(
    nint WindowHandle,
    int ProcessId,
    string ProcessName,
    string WindowTitle)
{
    public string DisplayName => string.IsNullOrWhiteSpace(WindowTitle)
        ? ProcessName
        : $"{ProcessName}: {WindowTitle}";
}

public sealed record RunningApplicationInfo(
    int ProcessId,
    string ProcessName,
    string WindowTitle)
{
    public string DisplayName => string.IsNullOrWhiteSpace(WindowTitle)
        ? ProcessName
        : $"{ProcessName}: {WindowTitle}";
}
