using Avalonia.Media;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Nmkoder.Data.Ui
{
    /// <summary>
    /// Base for the file/track list rows. WinForms carried this state on ListViewItem
    /// (Checked, BackColor, Text); with Avalonia the rows are data-bound, so it lives here.
    /// </summary>
    public abstract class ListEntryBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private bool _isChecked = true;
        private IBrush _rowBrush = Brushes.Transparent;

        /// <summary> Whether this entry is included in the output. </summary>
        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked == value)
                    return;

                _isChecked = value;
                OnPropertyChanged();
                CheckedChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary> Per-source-file tint, used to tell tracks of different files apart. </summary>
        public IBrush RowBrush
        {
            get => _rowBrush;
            set { _rowBrush = value; OnPropertyChanged(); }
        }

        /// <summary> The text shown in the list. </summary>
        public string DisplayName => ToString();

        /// <summary> Raised whenever any entry's checked state changes. </summary>
        public static event EventHandler CheckedChanged;

        public void RefreshDisplayName() => OnPropertyChanged(nameof(DisplayName));

        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        /// <summary> Sets IsChecked without raising <see cref="CheckedChanged"/>, for bulk updates. </summary>
        public void SetCheckedQuiet(bool value)
        {
            if (_isChecked == value)
                return;

            _isChecked = value;
            OnPropertyChanged(nameof(IsChecked));
        }

        internal static void RaiseCheckedChanged() => CheckedChanged?.Invoke(null, EventArgs.Empty);
    }
}
