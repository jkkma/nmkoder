using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Nmkoder.Data.Ui
{
    /// <summary>
    /// One line in the log box. The rows are what the list is bound to, and colour comes off the
    /// three flags below rather than a brush: the XAML switches style classes on them, so the palette
    /// stays in App.axaml where it belongs.
    /// </summary>
    public class LogRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private string _text;
        private int _repeats = 1;

        public IO.Logger.Level Level { get; private set; }

        public LogRow(string text, IO.Logger.Level level)
        {
            _text = text ?? "";
            Level = level;
        }

        public string Text => _text;

        /// <summary> How many times in a row this exact line has been logged. Repeats used to be
        /// dropped silently, which made a message that fired forty times look like it fired once. </summary>
        public int Repeats => _repeats;

        public string Display => _repeats > 1 ? $"{_text}   (x{_repeats})" : _text;

        public bool IsError => Level == IO.Logger.Level.Error;
        public bool IsWarning => Level == IO.Logger.Level.Warning;
        public bool IsDim => Level == IO.Logger.Level.Debug;

        /// <summary> Rewrites the line in place, for the progress lines that overwrite their
        /// predecessor. Cheap: it goes through one binding on at most one realized row. </summary>
        public void Replace(string text, IO.Logger.Level level)
        {
            _text = text ?? "";
            _repeats = 1;
            Level = level;
            Notify();
        }

        public void AddRepeat()
        {
            _repeats++;
            Notify();
        }

        private void Notify([CallerMemberName] string _ = null)
        {
            OnPropertyChanged(nameof(Text));
            OnPropertyChanged(nameof(Repeats));
            OnPropertyChanged(nameof(Display));
            OnPropertyChanged(nameof(Level));
            OnPropertyChanged(nameof(IsError));
            OnPropertyChanged(nameof(IsWarning));
            OnPropertyChanged(nameof(IsDim));
        }

        private void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public override string ToString() => Display;
    }
}
