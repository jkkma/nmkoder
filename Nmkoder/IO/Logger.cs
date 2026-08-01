using Avalonia.Threading;
using Nmkoder.Data;
using Nmkoder.Data.Ui;
using Nmkoder.Extensions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using DT = System.DateTime;

namespace Nmkoder.IO
{
    public class Logger
    {
        /// <summary>
        /// How loud a line is. Set explicitly by whoever logs it rather than guessed from the text -
        /// guessing from substrings is precisely the mistake the ffmpeg error handling used to make.
        /// <see cref="Level.Debug"/> is what hidden lines get: they only reach the file.
        /// </summary>
        public enum Level { Debug, Info, Warning, Error }

        /// <summary>
        /// The lines in the log box. Bound to a virtualizing list rather than concatenated into a
        /// TextBox: the old box reassigned its entire text on every line, and split-and-rejoined all
        /// of it again for every ffmpeg progress line, which is the hottest path in the app.
        /// </summary>
        public static ObservableCollection<LogRow> Rows { get; } = new ObservableCollection<LogRow>();

        /// <summary> Lines kept in the box. The list virtualizes, so this is about memory rather
        /// than render cost; the session log on disk keeps everything either way. </summary>
        private const int MaxRows = 5000;

        /// <summary> The cap that applies even to a log the user has paused. </summary>
        private const int HardMaxRows = 40000;

        /// <summary> Set by the window while the user has scrolled away from the bottom. Trimming
        /// waits for them, since it moves the content under whatever they are reading. </summary>
        public static bool FollowingSuspended;

        /// <summary> Raised after the box has been emptied, so the window can drop its "scrolled up"
        /// state - clearing produces no scroll event to infer it from. </summary>
        public static event Action Cleared;

        static string file;
        public const string defaultLogName = "sessionlog";
        public static long id;

        private static string _lastUi = "";
        public static string LastUiLine { get { return _lastUi; } }
        private static string _lastLog = "";
        public static string LastLogLine { get { return _lastLog; } }

        public struct LogEntry
        {
            public string logMessage;
            public bool hidden;
            public bool replaceLastLine;
            public string filename;
            public Level level;

            public LogEntry(string logMessageArg, bool hiddenArg = false, bool replaceLastLineArg = false, string filenameArg = "", Level levelArg = Level.Info)
            {
                logMessage = logMessageArg;
                hidden = hiddenArg;
                replaceLastLine = replaceLastLineArg;
                filename = filenameArg;
                level = levelArg;
            }
        }

        private static ConcurrentQueue<LogEntry> logQueue = new ConcurrentQueue<LogEntry>();

        public static void Log(string msg, bool hidden = false, bool replaceLastLine = false, string filename = "", Level level = Level.Info)
        {
            logQueue.Enqueue(new LogEntry(msg, hidden, replaceLastLine, filename, level));
            ShowNext();
        }

        /// <summary> Shorthand for the many call sites that only want to say "this one is bad". </summary>
        public static void LogErr(string msg, string filename = "") => Log(msg, false, false, filename, Level.Error);

        public static void LogWarn(string msg, string filename = "") => Log(msg, false, false, filename, Level.Warning);

        public static void ShowNext()
        {
            if (logQueue.TryDequeue(out LogEntry entry))
                Show(entry);
        }

        public static void Show(LogEntry entry)
        {
            if (string.IsNullOrWhiteSpace(entry.logMessage))
                return;

            string msg = entry.logMessage;
            // A line identical to the one before it is counted rather than repeated - and counted
            // visibly, where it used to be dropped without trace, so forty of them read as one.
            bool repeat = msg == LastUiLine && !entry.replaceLastLine;

            _lastLog = msg;

            if (!entry.hidden)
                _lastUi = msg;

            Console.WriteLine(msg);

            if (!entry.hidden)
                AppendToUi(msg.Replace("\n", Environment.NewLine), entry.replaceLastLine, repeat, entry.level);

            msg = msg.Replace("\n", Environment.NewLine);

            if (entry.replaceLastLine)
                msg = "[REPL] " + msg;

            if (!entry.hidden)
                msg = "[UI] " + msg;

            LogToFile(msg, false, entry.filename);
        }

        /// <summary>
        /// Appends to the log box. Log calls arrive from ffmpeg/av1an reader threads, so the actual
        /// mutation is always marshalled onto the UI thread.
        /// </summary>
        private static void AppendToUi(string msg, bool replaceLastLine, bool repeat, Level level)
        {
            void Append()
            {
                try
                {
                    LogRow last = Rows.Count > 0 ? Rows[Rows.Count - 1] : null;

                    if (repeat && last != null && last.Text == msg)
                    {
                        last.AddRepeat();
                        return;
                    }

                    // An error neither replaces nor is replaced. LogMode.OnlyLastLine - which every
                    // encode uses - rewrites the last row on each progress update, so a failure line
                    // arriving in that mode overwrote the progress line and was then overwritten by
                    // the next one a fraction of a second later. Colouring it red bought nothing if
                    // it was gone before anyone looked. Warnings stay replaceable: a damaged source
                    // prints scores of them per encode and pinning each one would bury the log.
                    if (replaceLastLine && last != null && last.Level != Level.Error && level != Level.Error)
                    {
                        last.Replace(msg, level);
                        return;
                    }

                    Rows.Add(new LogRow(msg, level));

                    // Trimmed a block at a time rather than a row at a time, so a long session does
                    // not pay a collection notification per line forever.
                    //
                    // Held off entirely while the user has scrolled up to read something: the scroll
                    // offset is in pixels, so dropping rows off the top slides the content out from
                    // under them - and what they scrolled up to read is almost always the error they
                    // are trying to read. HardMaxRows is the backstop for a log left paused all day.
                    int limit = FollowingSuspended ? HardMaxRows : MaxRows;

                    if (Rows.Count > limit + 250)
                    {
                        for (int i = 0; i < 250; i++)
                            Rows.RemoveAt(0);
                    }
                }
                catch { }
            }

            if (Dispatcher.UIThread.CheckAccess())
                Append();
            else
                Dispatcher.UIThread.Post(Append, DispatcherPriority.Background);
        }

        /// <summary> The whole log box as text, for Copy and Save. Built from the rows rather than
        /// by driving the control's selection, which measures a thousand times slower. </summary>
        public static string GetBoxText()
        {
            return string.Join(Environment.NewLine, Rows.Select(x => x.Display));
        }

        public static void LogToFile(string logStr, bool noLineBreak, string filename)
        {
            if (string.IsNullOrWhiteSpace(filename))
                filename = defaultLogName;

            if (Path.GetExtension(filename) != ".txt")
                filename = Path.ChangeExtension(filename, "txt");

            logStr = logStr.Replace(Environment.NewLine, " <br> ").TrimWhitespaces();
            string time = DateTime.Now.ToString("MM-dd-yyyy HH:mm:ss");

            try
            {
                // Inside the try: resolving the log path creates directories, and a location that
                // refuses them must not take down whatever was only trying to write a log line.
                file = Path.Combine(Paths.GetLogPath(), filename);
                string appendStr = noLineBreak ? $" {logStr}" : $"{Environment.NewLine}[{id.ToString().PadLeft(8, '0')}] [{time}]: {logStr}";

                File.AppendAllText(file, appendStr);
                id++;
            }
            catch
            {
                // this if fine, i forgot why
            }
        }

        public static void LogIfLastLineDoesNotContainMsg(string s, bool hidden = false, bool replaceLastLine = false, string filename = "")
        {
            if (!GetLastLine().Contains(s))
                Log(s, hidden, replaceLastLine, filename);
        }

        public static void WriteToFile(string content, bool append, string filename)
        {
            if (string.IsNullOrWhiteSpace(filename))
                filename = defaultLogName;

            if (Path.GetExtension(filename) != ".txt")
                filename = Path.ChangeExtension(filename, "txt");

            file = Path.Combine(Paths.GetLogPath(), filename);

            string time = DT.Now.Month + "-" + DT.Now.Day + "-" + DT.Now.Year + " " + DT.Now.Hour + ":" + DT.Now.Minute + ":" + DT.Now.Second;

            try
            {
                if (append)
                    File.AppendAllText(file, Environment.NewLine + time + ":" + Environment.NewLine + content);
                else
                    File.WriteAllText(file, Environment.NewLine + time + ":" + Environment.NewLine + content);
            }
            catch
            {

            }
        }

        public static void ClearLogBox()
        {
            void Clear()
            {
                Rows.Clear();
                _lastUi = ""; // Or the first line after a clear would be counted as a repeat of one nobody can see
                Cleared?.Invoke();
            }

            // Posted at the same priority as the appends rather than run inline, so lines already
            // queued from a reader thread land *before* the clear instead of surviving it.
            Dispatcher.UIThread.Post(Clear, DispatcherPriority.Background);
        }

        public static string GetLastLine(bool includeHidden = false)
        {
            return includeHidden ? _lastLog : _lastUi;
        }
    }
}
