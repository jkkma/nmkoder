using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Nmkoder.Views;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace Nmkoder.UI
{
    public class UiUtils
    {
        public enum MessageType { Message, Warning, Error };

        /// <summary> Buttons a message dialog can offer. Mirrors WinForms' MessageBoxButtons. </summary>
        public enum MessageButtons { Ok, YesNo, YesNoCancel };

        /// <summary> Result of a message dialog. Mirrors WinForms' DialogResult. </summary>
        public enum DialogResult { None, Ok, Cancel, Yes, No };

        public static Window MainWindowHandle
        {
            get
            {
                if (Program.MainWin != null)
                    return Program.MainWin;

                return (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            }
        }

        public static Task<DialogResult> ShowMessageBox(string text, MessageType type = MessageType.Message)
        {
            return MessageWindow.Show(text, $"Nmkoder - {type}", MessageButtons.Ok);
        }

        public static Task<DialogResult> ShowMessageBox(string text, string title, MessageButtons btns)
        {
            return MessageWindow.Show(text, title, btns);
        }

        /// <summary>
        /// Fire-and-forget message box, for the many call sites that just want to inform the user.
        /// Safe to call from any thread.
        /// </summary>
        public static void ShowMessageBoxAsync(string text, MessageType type = MessageType.Message)
        {
            Dispatcher.UIThread.Post(() => _ = ShowMessageBox(text, type));
        }

        public enum MoveDirection { Up = -1, Down = 1 };

        /// <summary>
        /// Moves an item within a bound collection, wrapping around at the ends -
        /// same behaviour the WinForms ListView helper had.
        /// </summary>
        public static void MoveItem<T>(ObservableCollection<T> collection, T item, MoveDirection direction)
        {
            if (item == null)
                return;

            int index = collection.IndexOf(item);

            if (index < 0)
                return;

            int count = collection.Count;
            int newIndex;

            if (direction == MoveDirection.Up)
                newIndex = index == 0 ? count - 1 : index - 1;
            else
                newIndex = index == count - 1 ? 0 : index + 1;

            collection.Move(index, newIndex);
        }

        /// <summary> Replaces a collection's contents in place, keeping bindings intact. </summary>
        public static void ReplaceAll<T>(ObservableCollection<T> collection, IEnumerable<T> newItems)
        {
            collection.Clear();

            foreach (T item in newItems)
                collection.Add(item);
        }
    }
}
