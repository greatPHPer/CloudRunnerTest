using System.Runtime.InteropServices;
using System.Text.Json;

namespace CloudRunner.WinForms;

/// <summary>
/// Sends a tightly validated set of pointer and keyboard events to the currently
/// selected application window. This is used only after the host explicitly
/// enables remote control in the live-preview page.
/// </summary>
internal static class RemoteInputService
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;

    private const uint MouseMove = 0x0001;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseRightDown = 0x0008;
    private const uint MouseRightUp = 0x0010;
    private const uint MouseMiddleDown = 0x0020;
    private const uint MouseMiddleUp = 0x0040;
    private const uint MouseWheel = 0x0800;
    private const uint MouseAbsolute = 0x8000;
    private const uint MouseVirtualDesk = 0x4000;

    private const uint KeyUp = 0x0002;
    private const uint KeyUnicode = 0x0004;

    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const int SwRestore = 9;

    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;
    private const int VkApps = 0x5D;
    private const int VkEscape = 0x1B;
    private const int VkF4 = 0x73;

    private static readonly object Sync = new();
    private static readonly HashSet<int> HeldKeys = [];
    private static readonly HashSet<int> HeldButtons = [];

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint Type;
        public InputUnion Data;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, NativeInput[] inputs, int inputSize);

    public static bool TryHandle(IntPtr targetWindow, JsonElement payload, out string message)
    {
        message = string.Empty;

        if (targetWindow == IntPtr.Zero || !IsWindow(targetWindow))
        {
            message = "The target application window is no longer available.";
            return false;
        }

        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("kind", out var kindElement) ||
            kindElement.ValueKind != JsonValueKind.String)
        {
            message = "The remote input message is invalid.";
            return false;
        }

        lock (Sync)
        {
            var kind = kindElement.GetString();
            return kind switch
            {
                "pointer" => HandlePointer(targetWindow, payload, out message),
                "wheel" => HandleWheel(targetWindow, payload, out message),
                "key" => HandleKey(targetWindow, payload, out message),
                "text" => HandleText(targetWindow, payload, out message),
                _ => Fail("Unsupported remote input event.", out message)
            };
        }
    }

    public static void ReleaseAll()
    {
        lock (Sync)
        {
            foreach (var key in HeldKeys.ToArray())
            {
                SendVirtualKey((ushort)key, keyUp: true);
            }

            HeldKeys.Clear();

            var releases = new List<NativeInput>();
            foreach (var button in HeldButtons)
            {
                var flags = button switch
                {
                    0 => MouseLeftUp,
                    1 => MouseMiddleUp,
                    2 => MouseRightUp,
                    _ => 0u
                };
                if (flags != 0)
                {
                    releases.Add(MouseEvent(0, 0, 0, flags));
                }
            }

            HeldButtons.Clear();
            Send(releases);
        }
    }

    private static bool HandlePointer(IntPtr target, JsonElement payload, out string message)
    {
        message = string.Empty;
        if (!TryGetNumber(payload, "x", out var x) ||
            !TryGetNumber(payload, "y", out var y) ||
            x is < 0 or > 1 || y is < 0 or > 1 ||
            !payload.TryGetProperty("phase", out var phaseElement) ||
            phaseElement.ValueKind != JsonValueKind.String)
        {
            return Fail("Pointer coordinates are invalid.", out message);
        }

        var phase = phaseElement.GetString();
        var button = 0;
        uint buttonFlags = 0;

        if (phase is "down" or "up")
        {
            if (!payload.TryGetProperty("button", out var buttonElement) ||
                !buttonElement.TryGetInt32(out button) || button is < 0 or > 2)
            {
                return Fail("Mouse button is invalid.", out message);
            }

            buttonFlags = (button, phase) switch
            {
                (0, "down") => MouseLeftDown,
                (0, "up") => MouseLeftUp,
                (1, "down") => MouseMiddleDown,
                (1, "up") => MouseMiddleUp,
                (2, "down") => MouseRightDown,
                (2, "up") => MouseRightUp,
                _ => 0u
            };
        }
        else if (phase != "move")
        {
            return Fail("Pointer action is invalid.", out message);
        }

        if (!TryGetAbsoluteCoordinates(target, x, y, out var absoluteX, out var absoluteY, out message))
        {
            return false;
        }

        if (phase == "down")
        {
            ActivateTarget(target);
            HeldButtons.Add(button);
        }
        else if (phase == "up")
        {
            HeldButtons.Remove(button);
        }

        var inputs = new List<NativeInput>
        {
            MouseEvent(absoluteX, absoluteY, 0, MouseMove | MouseAbsolute | MouseVirtualDesk)
        };

        if (buttonFlags != 0)
        {
            inputs.Add(MouseEvent(absoluteX, absoluteY, 0,
                buttonFlags | MouseAbsolute | MouseVirtualDesk));
        }

        return Send(inputs) || Fail(GetLastInputError(), out message);
    }

    private static bool HandleWheel(IntPtr target, JsonElement payload, out string message)
    {
        message = string.Empty;
        if (!TryGetNumber(payload, "x", out var x) ||
            !TryGetNumber(payload, "y", out var y) ||
            x is < 0 or > 1 || y is < 0 or > 1 ||
            !TryGetNumber(payload, "delta", out var deltaValue))
        {
            return Fail("Mouse-wheel coordinates or delta are invalid.", out message);
        }

        var delta = (int)Math.Clamp(Math.Round(deltaValue), -1200, 1200);
        if (delta == 0)
        {
            return true;
        }

        if (!TryGetAbsoluteCoordinates(target, x, y, out var absoluteX, out var absoluteY, out message))
        {
            return false;
        }

        var inputs = new[]
        {
            MouseEvent(absoluteX, absoluteY, 0, MouseMove | MouseAbsolute | MouseVirtualDesk),
            MouseEvent(absoluteX, absoluteY, unchecked((uint)delta), MouseWheel)
        };
        return Send(inputs) || Fail(GetLastInputError(), out message);
    }

    private static bool HandleKey(IntPtr target, JsonElement payload, out string message)
    {
        message = string.Empty;
        if (!payload.TryGetProperty("phase", out var phaseElement) ||
            phaseElement.ValueKind != JsonValueKind.String ||
            !payload.TryGetProperty("keyCode", out var keyElement) ||
            !keyElement.TryGetInt32(out var keyCode) ||
            keyCode is < 1 or > 255)
        {
            return Fail("Keyboard event is invalid.", out message);
        }

        var phase = phaseElement.GetString();
        if (phase is not ("down" or "up"))
        {
            return Fail("Keyboard action is invalid.", out message);
        }

        var altPressed = payload.TryGetProperty("altKey", out var altElement) &&
                         altElement.ValueKind == JsonValueKind.True;
        if (keyCode is VkMenu or VkLWin or VkRWin or VkApps ||
            altPressed ||
            (keyCode == VkEscape &&
             payload.TryGetProperty("ctrlKey", out var ctrlElement) &&
             ctrlElement.ValueKind == JsonValueKind.True) ||
            (keyCode == VkF4 &&
             payload.TryGetProperty("altKey", out var altF4Element) &&
             altF4Element.ValueKind == JsonValueKind.True))
        {
            return Fail("That system shortcut is blocked by CloudRunner.", out message);
        }

        if (phase == "down")
        {
            ActivateTarget(target);
            HeldKeys.Add(keyCode);
        }
        else
        {
            HeldKeys.Remove(keyCode);
        }

        return SendVirtualKey((ushort)keyCode, keyUp: phase == "up") ||
               Fail(GetLastInputError(), out message);
    }

    private static bool HandleText(IntPtr target, JsonElement payload, out string message)
    {
        message = string.Empty;
        if (!payload.TryGetProperty("text", out var textElement) ||
            textElement.ValueKind != JsonValueKind.String)
        {
            return Fail("Text input is invalid.", out message);
        }

        var text = textElement.GetString() ?? string.Empty;
        if (text.Length == 0 || text.Length > 256)
        {
            return Fail("Text input must contain between 1 and 256 characters.", out message);
        }

        ActivateTarget(target);
        var inputs = new List<NativeInput>(text.Length * 2);
        foreach (var character in text)
        {
            if (character == '\n' || character == '\r')
            {
                inputs.Add(KeyEvent(0x0D, 0, 0));
                inputs.Add(KeyEvent(0x0D, 0, KeyUp));
                continue;
            }

            if (character == '\t')
            {
                inputs.Add(KeyEvent(0x09, 0, 0));
                inputs.Add(KeyEvent(0x09, 0, KeyUp));
                continue;
            }

            inputs.Add(KeyEvent(0, character, KeyUnicode));
            inputs.Add(KeyEvent(0, character, KeyUnicode | KeyUp));
        }

        return Send(inputs) || Fail(GetLastInputError(), out message);
    }

    private static bool TryGetAbsoluteCoordinates(
        IntPtr target,
        double x,
        double y,
        out int absoluteX,
        out int absoluteY,
        out string message)
    {
        absoluteX = 0;
        absoluteY = 0;
        message = string.Empty;

        if (!GetWindowRect(target, out var rect))
        {
            return Fail("Could not read the target window bounds.", out message);
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var virtualLeft = GetSystemMetrics(SmXVirtualScreen);
        var virtualTop = GetSystemMetrics(SmYVirtualScreen);
        var virtualWidth = GetSystemMetrics(SmCxVirtualScreen);
        var virtualHeight = GetSystemMetrics(SmCyVirtualScreen);

        if (width <= 0 || height <= 0 || virtualWidth <= 1 || virtualHeight <= 1)
        {
            return Fail("The target window has invalid screen dimensions.", out message);
        }

        var screenX = rect.Left + (int)Math.Round(x * (width - 1));
        var screenY = rect.Top + (int)Math.Round(y * (height - 1));

        absoluteX = Math.Clamp(
            (int)Math.Round((screenX - virtualLeft) * 65535.0 / (virtualWidth - 1)),
            0, 65535);
        absoluteY = Math.Clamp(
            (int)Math.Round((screenY - virtualTop) * 65535.0 / (virtualHeight - 1)),
            0, 65535);
        return true;
    }

    private static void ActivateTarget(IntPtr target)
    {
        if (IsIconic(target))
        {
            ShowWindowAsync(target, SwRestore);
        }

        SetForegroundWindow(target);
    }

    private static NativeInput MouseEvent(int x, int y, uint data, uint flags) => new()
    {
        Type = InputMouse,
        Data = new InputUnion
        {
            Mouse = new MouseInput
            {
                Dx = x,
                Dy = y,
                MouseData = data,
                Flags = flags
            }
        }
    };

    private static NativeInput KeyEvent(ushort virtualKey, ushort scan, uint flags) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                VirtualKey = virtualKey,
                Scan = scan,
                Flags = flags
            }
        }
    };

    private static bool SendVirtualKey(ushort virtualKey, bool keyUp)
    {
        var input = KeyEvent(virtualKey, 0, keyUp ? KeyUp : 0);
        return Send([input]);
    }

    private static bool Send(IReadOnlyCollection<NativeInput> inputs)
    {
        if (inputs.Count == 0)
        {
            return true;
        }

        return SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<NativeInput>()) ==
               (uint)inputs.Count;
    }

    private static bool TryGetNumber(JsonElement payload, string name, out double value)
    {
        value = 0;
        return payload.TryGetProperty(name, out var element) &&
               element.ValueKind == JsonValueKind.Number &&
               element.TryGetDouble(out value) &&
               double.IsFinite(value);
    }

    private static string GetLastInputError()
    {
        var error = Marshal.GetLastWin32Error();
        return error == 0
            ? "Windows did not accept the input event."
            : $"Windows did not accept the input event (error {error}).";
    }

    private static bool Fail(string error, out string message)
    {
        message = error;
        return false;
    }

    private static bool IsWindow(IntPtr hWnd) =>
        hWnd != IntPtr.Zero && GetWindowRect(hWnd, out _);
}
