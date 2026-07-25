using Avalonia.Controls;
using Avalonia.Threading;
using Nmkoder.Data;
using Nmkoder.Extensions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DT = System.DateTime;

namespace Nmkoder.IO
{
    class Logger
    {
        /// <summary> Log output box in the main window. Written to on the UI thread only. </summary>
        public static TextBox textbox;

        static string file;
        public const string defaultLogName = "sessionlog";
        public static long id;

        private static Dictionary<string, string> sessionLogs = new Dictionary<string, string>();
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

            public LogEntry(string logMessageArg, bool hiddenArg = false, bool replaceLastLineArg = false, string filenameArg = "")
            {
                logMessage = logMessageArg;
                hidden = hiddenArg;
                replaceLastLine = replaceLastLineArg;
                filename = filenameArg;
            }
        }

        private static ConcurrentQueue<LogEntry> logQueue = new ConcurrentQueue<LogEntry>();

        public static void Log(string msg, bool hidden = false, bool replaceLastLine = false, string filename = "")
        {
            logQueue.Enqueue(new LogEntry(msg, hidden, replaceLastLine, filename));
            ShowNext();
        }

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

            if (msg == LastUiLine)
                entry.hidden = true; // Never show the same line twice in UI, but log it to file

            _lastLog = msg;

            if (!entry.hidden)
                _lastUi = msg;

            Console.WriteLine(msg);

            if (!entry.hidden)
                AppendToUi(msg.Replace("\n", Environment.NewLine), entry.replaceLastLine);

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
        private static void AppendToUi(string msg, bool replaceLastLine)
        {
            TextBox box = textbox;

            if (box == null)
                return;

            void Append()
            {
                try
                {
                    string current = box.Text ?? "";

                    if (replaceLastLine && current.Length > 0)
                    {
                        string[] lines = current.SplitIntoLines();
                        current = string.Join(Environment.NewLine, lines.Take(lines.Length - 1));
                    }

                    box.Text = current.Length > 0 ? current + Environment.NewLine + msg : msg;
                    box.CaretIndex = box.Text.Length;
                }
                catch { }
            }

            if (Dispatcher.UIThread.CheckAccess())
                Append();
            else
                Dispatcher.UIThread.Post(Append, DispatcherPriority.Background);
        }

        public static void LogToFile(string logStr, bool noLineBreak, string filename)
        {
            if (string.IsNullOrWhiteSpace(filename))
                filename = defaultLogName;

            if (Path.GetExtension(filename) != ".txt")
                filename = Path.ChangeExtension(filename, "txt");

            file = Path.Combine(Paths.GetLogPath(), filename);
            logStr = logStr.Replace(Environment.NewLine, " <br> ").TrimWhitespaces();
            string time = DateTime.Now.ToString("MM-dd-yyyy HH:mm:ss");

            try
            {
                string appendStr = noLineBreak ? $" {logStr}" : $"{Environment.NewLine}[{id.ToString().PadLeft(8, '0')}] [{time}]: {logStr}";

                lock (sessionLogs)
                {
                    sessionLogs[filename] = (sessionLogs.ContainsKey(filename) ? sessionLogs[filename] : "") + appendStr;
                }

                File.AppendAllText(file, appendStr);
                id++;
            }
            catch
            {
                // this if fine, i forgot why
            }
        }

        public static string GetSessionLog(string filename)
        {
            if (!filename.Contains(".txt"))
                filename = Path.ChangeExtension(filename, "txt");

            lock (sessionLogs)
            {
                return sessionLogs.ContainsKey(filename) ? sessionLogs[filename] : "";
            }
        }

        public static List<string> GetSessionLogLastLines(string filename, int linesCount = 5)
        {
            string log = GetSessionLog(filename);
            string[] lines = log.SplitIntoLines();
            return lines.Reverse().Take(linesCount).Reverse().ToList();
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
            TextBox box = textbox;

            if (box == null)
                return;

            if (Dispatcher.UIThread.CheckAccess())
                box.Text = "";
            else
                Dispatcher.UIThread.Post(() => box.Text = "");
        }

        public static string GetLastLine(bool includeHidden = false)
        {
            return includeHidden ? _lastLog : _lastUi;
        }
    }
}
