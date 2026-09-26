using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace RSTGameTranslation
{
    /// <summary>
    /// Finds a top-level window by the process that owns it. Used to re-acquire the last captured
    /// game after an app/game restart (the window handle changes, but the process name is stable).
    /// </summary>
    public static class WindowFinder
    {
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        /// <summary>Returns the first visible, titled window owned by the named process (handle Zero if none).</summary>
        public static (IntPtr handle, string title) FindByProcessName(string processName)
        {
            IntPtr found = IntPtr.Zero;
            string foundTitle = string.Empty;
            if (string.IsNullOrEmpty(processName)) return (found, foundTitle);

            EnumWindows((hWnd, l) =>
            {
                try
                {
                    if (!IsWindowVisible(hWnd)) return true;
                    int len = GetWindowTextLength(hWnd);
                    if (len == 0) return true;

                    GetWindowThreadProcessId(hWnd, out uint pid);
                    if (pid == 0) return true;

                    string pname = Process.GetProcessById((int)pid).ProcessName;
                    if (!string.Equals(pname, processName, StringComparison.OrdinalIgnoreCase)) return true;

                    var sb = new StringBuilder(len + 1);
                    GetWindowText(hWnd, sb, sb.Capacity);
                    found = hWnd;
                    foundTitle = sb.ToString();
                    return false; // stop enumeration
                }
                catch { return true; }
            }, IntPtr.Zero);

            return (found, foundTitle);
        }

        /// <summary>Process name that owns the given window (empty on failure).</summary>
        public static string GetProcessName(IntPtr hWnd)
        {
            try
            {
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid == 0) return string.Empty;
                return Process.GetProcessById((int)pid).ProcessName;
            }
            catch { return string.Empty; }
        }
    }
}
