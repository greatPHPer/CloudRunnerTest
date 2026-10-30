using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CloudRunner.WinForms;

internal static class WindowsCommandLineParser
{
    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    public static string[] ParseArguments(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return [];
        }

        var pointer = CommandLineToArgvW("CloudRunnerDummy.exe " + commandLine, out var count);
        if (pointer == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not parse command-line arguments.");
        }

        try
        {
            var result = new string[Math.Max(0, count - 1)];
            for (var index = 1; index < count; index++)
            {
                var item = Marshal.ReadIntPtr(pointer, index * IntPtr.Size);
                result[index - 1] = Marshal.PtrToStringUni(item) ?? string.Empty;
            }

            return result;
        }
        finally
        {
            _ = LocalFree(pointer);
        }
    }
}
