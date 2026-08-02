using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Nmkoder.Data.Ui
{
    /// <summary> Where a file stands in a batch queue. <see cref="BatchStatus.None"/> is a file that
    /// has never been part of one, and is what keeps the indicator off the row until a batch runs. </summary>
    public enum BatchStatus { None, Queued, Running, Done, Failed, Canceled, Skipped }

    public class FileListEntry : ListEntryBase
    {
        public MediaFile File { get; }
        public string Title { get { return File.Title; } }
        public string TitleEdited { get; set; } = null;
        public string Language { get { return File.Language; } }
        public string LanguageEdited { get; set; } = null;

        private BatchStatus _status = BatchStatus.None;
        private string _statusNote = "";

        /// <summary> Where this file stands in the batch that is running, or last ran. </summary>
        public BatchStatus Status => _status;

        /// <summary> What became of it - the size summary of a finished encode, the reason a failed
        /// one stopped. Shown in full as the row's tooltip. </summary>
        public string StatusNote => _statusNote;

        public bool HasBatchStatus => _status != BatchStatus.None;

        public string StatusGlyph
        {
            get
            {
                switch (_status)
                {
                    case BatchStatus.Queued: return "○";
                    case BatchStatus.Running: return "▶";
                    case BatchStatus.Done: return "✔";
                    case BatchStatus.Failed: return "✕";
                    case BatchStatus.Canceled: return "■";
                    case BatchStatus.Skipped: return "–";
                    default: return "";
                }
            }
        }

        public string StatusText
        {
            get
            {
                switch (_status)
                {
                    case BatchStatus.Queued: return "Queued";
                    case BatchStatus.Running: return "Running…";
                    case BatchStatus.Done: return string.IsNullOrWhiteSpace(_statusNote) ? "Done" : $"Done - {_statusNote}";
                    case BatchStatus.Failed: return "Failed";
                    case BatchStatus.Canceled: return "Canceled";
                    case BatchStatus.Skipped: return "Skipped";
                    default: return "";
                }
            }
        }

        /// <summary> Null rather than an empty string, so a row with nothing to add shows no tooltip
        /// at all instead of an empty box. </summary>
        public string StatusTooltip => string.IsNullOrWhiteSpace(_statusNote) ? null : _statusNote;

        /// <summary> Looked up from the palette rather than written here - see the App.axaml keys. </summary>
        public IBrush StatusBrush
        {
            get
            {
                string key = _status == BatchStatus.Failed ? "NmkoderDanger"
                    : _status == BatchStatus.Done || _status == BatchStatus.Running ? "NmkoderAccent"
                    : "NmkoderMutedText";

                if (Application.Current != null && Application.Current.TryFindResource(key, out object brush) && brush is IBrush b)
                    return b;

                return Brushes.Transparent;
            }
        }

        public FileListEntry()
        {

        }

        public FileListEntry(MediaFile file)
        {
            File = file;
        }

        /// <summary>
        /// Moves the row to a new batch state. The notifications are marshalled because the batch
        /// loop's awaits can land on a worker thread, and a binding updated from one is a crash
        /// rather than a wrong colour.
        /// </summary>
        public void SetBatchStatus(BatchStatus status, string note = "")
        {
            _status = status;
            _statusNote = note ?? "";

            if (Dispatcher.UIThread.CheckAccess())
                NotifyStatusChanged();
            else
                Dispatcher.UIThread.Post(NotifyStatusChanged);
        }

        private void NotifyStatusChanged()
        {
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(StatusNote));
            OnPropertyChanged(nameof(HasBatchStatus));
            OnPropertyChanged(nameof(StatusGlyph));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusTooltip));
            OnPropertyChanged(nameof(StatusBrush));
        }

        public override string ToString()
        {
            return File?.ToString() ?? "";
        }
    }
}
