namespace Nmkoder.Data.Ui
{
    public class FileListEntry : ListEntryBase
    {
        public MediaFile File { get; }
        public string Title { get { return File.Title; } }
        public string TitleEdited { get; set; } = null;
        public string Language { get { return File.Language; } }
        public string LanguageEdited { get; set; } = null;

        public FileListEntry()
        {

        }

        public FileListEntry(MediaFile file)
        {
            File = file;
        }

        public override string ToString()
        {
            return File?.ToString() ?? "";
        }
    }
}
