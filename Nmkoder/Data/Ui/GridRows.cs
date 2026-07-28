using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Nmkoder.Data.Ui
{
    /// <summary>
    /// Row models backing the editable grids. WinForms' DataGridView stored values in loosely typed
    /// cells; Avalonia's DataGrid binds to objects, so each grid gets a small model.
    /// </summary>
    public abstract class GridRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (Equals(field, value))
                return;

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary> One row of the metadata grid: track name, editable title and language. </summary>
    public class MetadataRow : GridRow
    {
        private string _field;
        private string _title;
        private string _language;

        /// <summary> Read-only description of the track this row belongs to. </summary>
        public string Field { get => _field; set => Set(ref _field, value); }
        public string Title { get => _title; set => Set(ref _title, value); }
        public string Language { get => _language; set => Set(ref _language, value); }

        /// <summary> The stream this row maps to, or null for the output file's own metadata. </summary>
        public StreamListEntry Stream { get; set; }

        public MetadataRow() { }

        public MetadataRow(string field, string title, string language, StreamListEntry stream = null)
        {
            _field = field;
            _title = title;
            _language = language;
            Stream = stream;
        }
    }

    /// <summary> One row of the custom filter grids. </summary>
    public class FilterRow : GridRow
    {
        private string _filter = "";

        public string Filter { get => _filter; set => Set(ref _filter, value); }

        public FilterRow() { }
        public FilterRow(string filter) { _filter = filter; }
    }

    /// <summary> One row of av1an's per-encoder advanced argument grid. </summary>
    public class EncoderArgRow : GridRow
    {
        private string _argument = "";
        private string _value = "";
        private string _description = "";

        public string Argument { get => _argument; set => Set(ref _argument, value); }
        public string Value { get => _value; set => Set(ref _value, value); }
        public string Description { get => _description; set => Set(ref _description, value); }

        /// <summary> Which category tab the row is shown under. Fixed at load, so no notification. </summary>
        public string Category { get; set; } = "";

        /// <summary>
        /// The long-form explanation shown when the row is right-clicked. The grid's own description
        /// is one clipped line, so this is where anything that needs room goes. Paragraphs are
        /// separated by blank lines. Empty for arguments nothing has been written for yet.
        /// </summary>
        public string Details { get; set; } = "";

        /// <summary> Example settings, one per line, each "value|what that value gets you". </summary>
        public string Examples { get; set; } = "";

        public EncoderArgRow() { }

        public EncoderArgRow(string argument, string value, string description, string category = "", string details = "", string examples = "")
        {
            _argument = argument;
            _value = value;
            _description = description;
            Category = category;
            Details = details;
            Examples = examples;
        }
    }

    /// <summary> One row of the per-track audio configuration grid. </summary>
    public class AudioTrackRow : GridRow
    {
        private int _channels;
        private int _bitrateKbps;

        public string Track { get; set; }
        public string Title { get; set; }
        public string Language { get; set; }
        public int Channels { get => _channels; set => Set(ref _channels, value); }
        public int BitrateKbps { get => _bitrateKbps; set => Set(ref _bitrateKbps, value); }

        public AudioTrackRow() { }

        public AudioTrackRow(string track, string title, string language, int channels, int kbps)
        {
            Track = track;
            Title = title;
            Language = language;
            _channels = channels;
            _bitrateKbps = kbps;
        }
    }

    /// <summary> One row of the "reset on new file" settings list. </summary>
    public class ToggleRow : GridRow
    {
        private bool _isChecked;

        public string Name { get; set; }
        public string PropertyName { get; set; }
        public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }

        public ToggleRow(string name, string propertyName, bool isChecked)
        {
            Name = name;
            PropertyName = propertyName;
            _isChecked = isChecked;
        }
    }
}
