using System.Runtime.InteropServices;

namespace Axiom.Providers.Windows;

// Windows SDK shellapi.h uses default packing on _WIN64 (48 bytes), pack(1) on x86 (36 bytes).
// All 32-bit fields have the same x86 offsets under sequential default packing.
[StructLayout(LayoutKind.Sequential)]
internal struct TaskbarAppBarData
{
    internal uint Size;
    internal nint Window;
    internal uint CallbackMessage, Edge;
    internal int Left, Top, Right, Bottom;
    internal nint Parameter;
}
