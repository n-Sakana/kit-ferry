using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Ferry
{
    internal delegate void FileProgressHandler(
        FolderFile file,
        int index,
        int total,
        bool completed);

    internal static class ConsoleCommand
    {
        public static int Run(string appDirectory, string mode, string path)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                SetTitle(mode);
                var source = ReadSource(path);
                var files = SelectFiles(source, mode);
                if (files.Count == 0 && !(mode == "markdown" && MarkdownService.CanWriteOnlyOmissions(source)))
                {
                    throw new InvalidOperationException(
                        mode == "markdown"
                            ? "Markdown 化できるファイルがありません。"
                            : "VBA を含められるブックがありません。");
                }

                WriteHeader(mode, source, files);
                var outputRoot = Path.Combine(
                    Path.GetFullPath(appDirectory),
                    "output");
                var progress = new ConsoleFileProgress();
                string outputPath;
                var resultCode = 0;

                if (mode == "markdown")
                {
                    var names = FileNames(files);
                    var task = Task.Run(delegate
                    {
                        return MarkdownService.Convert(
                            source,
                            names,
                            true,
                            outputRoot,
                            progress.Report);
                    });
                    var result = progress.Wait(task);
                    outputPath = result.OutputPath;
                    Console.WriteLine();
                    Console.WriteLine(
                        "Converted: {0}/{1}",
                        result.ConvertedCount,
                        files.Count);
                    foreach (var failure in result.Failures)
                    {
                        Console.WriteLine(
                            "[WARN] {0}: {1}",
                            failure.Name,
                            OneLine(failure.Error));
                    }
                    if (result.FailedCount > 0)
                    {
                        resultCode = 1;
                    }
                }
                else
                {
                    var names = FileNames(files);
                    var task = Task.Run(delegate
                    {
                        return VbaService.Extract(
                            source,
                            names,
                            outputRoot,
                            progress.Report);
                    });
                    var result = progress.Wait(task);
                    outputPath = result.OutputPath;
                    Console.WriteLine();
                    Console.WriteLine(
                        "Extracted: {0} book(s), {1} module(s), {2} line(s)",
                        result.BookCount,
                        result.ModuleCount,
                        result.LineCount);
                }

                stopwatch.Stop();
                WriteOutput(outputPath);
                Console.WriteLine("Elapsed: {0}", FormatElapsed(stopwatch.Elapsed));
                WaitForKey();
                return resultCode;
            }
            catch (Exception exception)
            {
                stopwatch.Stop();
                Console.Error.WriteLine();
                Console.Error.WriteLine("[ERROR] {0}", exception.GetBaseException().Message);
                Console.Error.WriteLine("Elapsed: {0}", FormatElapsed(stopwatch.Elapsed));
                WaitForKey();
                return 1;
            }
        }

        private static FolderSnapshot ReadSource(string path)
        {
            var fullPath = Path.GetFullPath(path);
            if (Directory.Exists(fullPath))
            {
                return FolderCatalog.Inspect(fullPath);
            }
            if (File.Exists(fullPath))
            {
                return FolderCatalog.InspectFiles(new[] { fullPath });
            }
            throw new FileNotFoundException(
                string.Format("Path not found: {0}", fullPath),
                fullPath);
        }

        private static List<FolderFile> SelectFiles(
            FolderSnapshot source,
            string mode)
        {
            var selected = new List<FolderFile>();
            foreach (var file in source.Files)
            {
                if ((mode == "markdown" && file.MarkdownSupported)
                    || (mode == "vba" && file.VbaWorkbook))
                {
                    selected.Add(file);
                }
            }
            return selected;
        }

        private static List<string> FileNames(IList<FolderFile> files)
        {
            var names = new List<string>();
            foreach (var file in files)
            {
                names.Add(file.Name);
            }
            return names;
        }

        private static void WriteHeader(
            string mode,
            FolderSnapshot source,
            IList<FolderFile> files)
        {
            long totalBytes = 0;
            foreach (var file in files)
            {
                totalBytes = checked(totalBytes + file.Size);
            }

            Console.WriteLine();
            Console.WriteLine("Fin-Ferry / {0}", ModeName(mode));
            Console.WriteLine(new string('=', 56));
            Console.WriteLine("Target: {0}", source.Path);
            Console.WriteLine("Files: {0}", files.Count);
            Console.WriteLine("Total size: {0}", FormatSize(totalBytes));
            Console.WriteLine();
        }

        private static void WriteOutput(string outputPath)
        {
            var fullPath = Path.GetFullPath(outputPath);
            var folderPath = Directory.Exists(fullPath)
                ? fullPath
                : Path.GetDirectoryName(fullPath);

            Console.WriteLine();
            Console.WriteLine("Output:");
            Console.WriteLine(fullPath);
            if (!string.IsNullOrWhiteSpace(folderPath))
            {
                var uri = new Uri(
                    folderPath.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar).AbsoluteUri;
                Console.Write("Ctrl+click to open output folder: ");
                Console.Write("\u001b]8;;{0}\u001b\\", uri);
                Console.Write(folderPath);
                Console.Write("\u001b]8;;\u001b\\");
                Console.WriteLine();
            }
        }

        private static void WaitForKey()
        {
            if (Console.IsInputRedirected)
            {
                return;
            }

            Console.WriteLine();
            Console.Write("Press any key to close...");
            Console.ReadKey(true);
            Console.WriteLine();
        }

        private static void SetTitle(string mode)
        {
            try
            {
                Console.Title = "Fin-Ferry - " + ModeName(mode);
            }
            catch
            {
            }
        }

        private static string ModeName(string mode)
        {
            return mode == "markdown" ? "Markdown 化" : "VBA 抽出";
        }

        private static string OneLine(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Replace("\r", " ").Replace("\n", " ").Trim();
        }

        private static string FormatElapsed(TimeSpan elapsed)
        {
            return elapsed.TotalHours >= 1
                ? elapsed.ToString(@"hh\:mm\:ss")
                : elapsed.ToString(@"mm\:ss\.ff");
        }

        private static string FormatSize(long size)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double value = size;
            var unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return unit == 0
                ? string.Format("{0} {1}", size, units[unit])
                : string.Format("{0:0.0} {1}", value, units[unit]);
        }

        private sealed class ConsoleFileProgress
        {
            private static readonly string[] Frames =
            {
                "\u280B", "\u2819", "\u2839", "\u2838",
                "\u283C", "\u2834", "\u2826", "\u2827",
                "\u2807", "\u280F"
            };

            private readonly object _gate = new object();
            private readonly Queue<ProgressEvent> _events =
                new Queue<ProgressEvent>();
            private string _activeName;
            private int _activeIndex;
            private int _activeTotal;
            private int _renderedWidth;

            public void Report(
                FolderFile file,
                int index,
                int total,
                bool completed)
            {
                lock (_gate)
                {
                    _events.Enqueue(new ProgressEvent(
                        file.Name,
                        index,
                        total,
                        completed));
                    if (completed)
                    {
                        _activeName = null;
                    }
                    else
                    {
                        _activeName = file.Name;
                        _activeIndex = index;
                        _activeTotal = total;
                    }
                }
            }

            public T Wait<T>(Task<T> task)
            {
                var frame = 0;
                while (!task.IsCompleted)
                {
                    DrainEvents();
                    Render(frame++);
                    Thread.Sleep(125);
                }

                DrainEvents();
                ClearRenderedLine();
                return task.GetAwaiter().GetResult();
            }

            private void DrainEvents()
            {
                while (true)
                {
                    ProgressEvent progress;
                    lock (_gate)
                    {
                        if (_events.Count == 0)
                        {
                            return;
                        }
                        progress = _events.Dequeue();
                    }

                    if (progress.Completed)
                    {
                        ClearRenderedLine();
                        Console.WriteLine(
                            "[OK] [{0}/{1}] {2}",
                            progress.Index,
                            progress.Total,
                            progress.Name);
                    }
                    else if (Console.IsOutputRedirected)
                    {
                        Console.WriteLine(
                            "[RUN] [{0}/{1}] {2}",
                            progress.Index,
                            progress.Total,
                            progress.Name);
                    }
                }
            }

            private void Render(int frame)
            {
                if (Console.IsOutputRedirected)
                {
                    return;
                }

                string name;
                int index;
                int total;
                lock (_gate)
                {
                    name = _activeName;
                    index = _activeIndex;
                    total = _activeTotal;
                }
                if (string.IsNullOrWhiteSpace(name))
                {
                    return;
                }

                var line = string.Format(
                    "{0} [{1}/{2}] {3}",
                    Frames[frame % Frames.Length],
                    index,
                    total,
                    name);
                Console.Write("\r{0}", line);
                if (_renderedWidth > line.Length)
                {
                    Console.Write(new string(' ', _renderedWidth - line.Length));
                }
                _renderedWidth = line.Length;
            }

            private void ClearRenderedLine()
            {
                if (Console.IsOutputRedirected || _renderedWidth == 0)
                {
                    return;
                }
                Console.Write("\r{0}\r", new string(' ', _renderedWidth));
                _renderedWidth = 0;
            }

            private sealed class ProgressEvent
            {
                public ProgressEvent(
                    string name,
                    int index,
                    int total,
                    bool completed)
                {
                    Name = name;
                    Index = index;
                    Total = total;
                    Completed = completed;
                }

                public string Name { get; private set; }
                public int Index { get; private set; }
                public int Total { get; private set; }
                public bool Completed { get; private set; }
            }
        }
    }
}
