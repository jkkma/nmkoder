using Avalonia.Input;

namespace Nmkoder.UI
{
    /// <summary>
    /// Tracks live modifier state. WinForms could query System.Windows.Input.Keyboard.Modifiers at
    /// any moment; Avalonia only reports modifiers as part of input events, so the main window feeds
    /// them in here and the "hold Shift to edit the command" features read them back.
    /// </summary>
    public static class Hotkeys
    {
        public static KeyModifiers Modifiers { get; private set; } = KeyModifiers.None;

        public static bool ShiftHeld { get { return Modifiers.HasFlag(KeyModifiers.Shift); } }
        public static bool CtrlHeld { get { return Modifiers.HasFlag(KeyModifiers.Control); } }

        public static void Update(KeyModifiers modifiers)
        {
            Modifiers = modifiers;
        }
    }
}
