using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using KnowledgeStudio;

namespace Ferry
{
    internal static class MarkdownService
    {
        internal const long LargeFileBytes = 1000000;

        public static MarkdownConversionResult Convert(
            FolderSnapshot source,
            IList<string> selectedNames,
            bool combine,
            string outputRoot)
        {
            return Convert(source, selectedNames, combine, outputRoot, null);
        }

        public static MarkdownConversionResult Convert(
            FolderSnapshot source,
            IList<string> selectedNames,
            bool combine,
            string outputRoot,
            FileProgressHandler progress,
            bool excludeLargeFiles = false)
        {
            var files = ResolveSelectedFiles(source, selectedNames);
            return WriteCombined(source, files, outputRoot, progress, excludeLargeFiles);
        }

        internal static bool CanWriteOnlyOmissions(FolderSnapshot source)
        {
            return source != null && source.Files.Count > 0
                && source.Files.TrueForAll(delegate (FolderFile file) { return !file.MarkdownSupported; });
        }

        internal static string ExclusionReason(ExtractResult result)
        {
            switch (result.FailureReason)
            {
                case ExtractFailureReason.Binary: return "バイナリまたは不正な文字コード";
                case ExtractFailureReason.TooLarge: return "大きすぎる（既存の上限）";
                default: return "変換に失敗した";
            }
        }

        private static List<FolderFile> ResolveSelectedFiles(
            FolderSnapshot source,
            IList<string> selectedNames)
        {
            if (source == null)
            {
                throw new ArgumentException("入力が選ばれていません。", "source");
            }
            if (selectedNames == null || (selectedNames.Count == 0 && !CanWriteOnlyOmissions(source)))
            {
                throw new ArgumentException(
                    "Markdown にするファイルを1つ以上選んでください。",
                    "selectedNames");
            }

            var requested = new HashSet<string>(
                selectedNames,
                StringComparer.CurrentCultureIgnoreCase);
            if (requested.Count != selectedNames.Count)
            {
                throw new ArgumentException("同じファイルが複数回指定されています。", "selectedNames");
            }

            var files = new List<FolderFile>();
            foreach (var file in source.Files)
            {
                if (!requested.Remove(file.Name))
                {
                    continue;
                }
                if (!file.MarkdownSupported)
                {
                    throw new ArgumentException(
                        string.Format("Markdown 化の対象外です: {0}", file.Name),
                        "selectedNames");
                }
                files.Add(file);
            }

            if (requested.Count > 0)
            {
                foreach (var missing in requested)
                {
                    throw new ArgumentException(
                        string.Format("現在の入力にないファイルです: {0}", missing),
                        "selectedNames");
                }
            }
            return files;
        }

        private static MarkdownConversionResult WriteCombined(
            FolderSnapshot source,
            List<FolderFile> files,
            string outputRoot,
            FileProgressHandler progress,
            bool excludeLargeFiles)
        {
            var outputDirectory = OutputLayout.CreateRunDirectory(outputRoot, source);
            var outputPath = FindAvailableFile(Path.Combine(
                outputDirectory,
                Path.GetFileName(CombinedOutputPath(source, files))));
            var failures = new List<MarkdownFailure>();
            var omissions = new Dictionary<FolderFile, string>();
            var selected = new HashSet<FolderFile>(files);
            foreach (var file in source.Files)
            {
                if (!file.MarkdownSupported) omissions.Add(file, file.MarkdownExclusionReason);
                else if (!selected.Contains(file)) omissions.Add(file, "未選択");
                else if (excludeLargeFiles && file.Size > LargeFileBytes)
                    omissions.Add(file, "大きいファイル（1 MB 超・まとめて除外）");
            }
            if (excludeLargeFiles)
                files = files.FindAll(delegate (FolderFile file) { return file.Size <= LargeFileBytes; });
            var builder = new StringBuilder();
            builder.Append("# ");
            builder.AppendLine(EscapeHeading(SourceTitle(source, files)));

            var converted = 0;
            for (var index = 0; index < files.Count; index++)
            {
                var file = files[index];
                if (progress != null)
                {
                    progress(file, index + 1, files.Count, false);
                }
                var result = Extract.FromFile(
                    file.FullPath,
                    file.Kind.ToLowerInvariant(),
                    file.Extension);
                if (result.Succeeded)
                {
                    builder.AppendLine();
                    builder.Append("## ");
                    builder.AppendLine(EscapeHeading(file.Name));
                    builder.AppendLine();
                    AppendContent(builder, file, result);
                    converted++;
                }
                else
                {
                    var message = string.IsNullOrWhiteSpace(result.Notes)
                        ? "内容を読み取れませんでした。"
                        : result.Notes;
                    omissions[file] = ExclusionReason(result);
                    failures.Add(new MarkdownFailure(file.Name, message));
                }
                if (progress != null)
                {
                    progress(file, index + 1, files.Count, true);
                }
            }

            AppendOmissions(builder, source, omissions);
            WriteUtf8Atomically(outputPath, builder.ToString());
            return new MarkdownConversionResult(
                outputPath,
                1,
                converted,
                failures);
        }

        private static void AppendOmissions(StringBuilder builder, FolderSnapshot source,
            Dictionary<FolderFile, string> omissions)
        {
            if (omissions.Count == 0) return;
            builder.AppendLine().AppendLine("## 対象外").AppendLine();
            // Keep all names in the file; fold only their presentation for long lists.
            var fold = omissions.Count > 50;
            if (fold) builder.AppendLine("<details>").Append("<summary>")
                .Append(omissions.Count).AppendLine(" 件（相対パスと理由）</summary>").AppendLine();
            builder.AppendLine("| 相対パス | 理由 |").AppendLine("| --- | --- |");
            foreach (var file in source.Files)
            {
                string reason;
                if (!omissions.TryGetValue(file, out reason)) continue;
                builder.Append("| <code>").Append(EscapeTablePath(file.Name))
                    .Append("</code> | ").Append(reason).AppendLine(" |");
            }
            if (fold) builder.AppendLine().AppendLine("</details>");
        }

        private static string EscapeTablePath(string path)
        {
            var escaped = new StringBuilder();
            foreach (var c in path)
            {
                switch (c)
                {
                    case '&': escaped.Append("&amp;"); break;
                    case '<': escaped.Append("&lt;"); break;
                    case '>': escaped.Append("&gt;"); break;
                    case '|': case '`': case '[': case ']': case '*': case '_':
                    case '\\': case '\r': case '\n':
                        escaped.Append("&#").Append((int)c).Append(';'); break;
                    default: escaped.Append(c); break;
                }
            }
            return escaped.ToString();
        }

        private static void AppendContent(StringBuilder builder, FolderFile file, ExtractResult result)
        {
            var content = result.Content.TrimEnd('\r', '\n');
            // Keep documents and the existing prose/table formats as Markdown.
            if (result.Method != "text" || file.Extension == ".txt" || file.Extension == ".md"
                || file.Extension == ".markdown" || file.Extension == ".csv"
                || file.Extension == ".tsv" || file.Extension == ".log")
            {
                builder.AppendLine(content);
                return;
            }

            // A source file may itself contain Markdown fences (or raw string
            // literals containing them). Its enclosing fence must be longer.
            var longest = 2;
            var run = 0;
            foreach (var c in content)
            {
                run = c == '`' ? run + 1 : 0;
                longest = Math.Max(longest, run);
            }
            var fence = new string('`', longest + 1);
            builder.Append(fence).AppendLine(CodeLanguage(file));
            builder.AppendLine(content);
            builder.AppendLine(fence);
        }

        private static string CodeLanguage(FolderFile file)
        {
            // This map labels code; it never restricts which text files can enter.
            switch (file.Extension)
            {
                case ".cs": case ".csx": return "csharp";
                case ".c": case ".h": return "c";
                case ".cpp": case ".cc": case ".cxx": case ".hpp": case ".hxx": case ".hh": return "cpp";
                case ".java": return "java";
                case ".rs": return "rust";
                case ".go": return "go";
                case ".rb": case ".rake": return "ruby";
                case ".php": return "php";
                case ".swift": return "swift";
                case ".kt": case ".kts": return "kotlin";
                case ".sh": case ".bash": case ".zsh": return "bash";
                case ".toml": return "toml";
                case ".vb": case ".vbs": case ".bas": return "vb";
                case ".fs": case ".fsx": case ".fsi": return "fsharp";
                case ".lua": return "lua";
                case ".r": return "r";
                case ".css": return "css";
                case ".scss": return "scss";
                case ".sass": return "sass";
                case ".less": return "less";
                case ".vue": return "vue";
                case ".gradle": case ".groovy": return "groovy";
                case ".json": case ".jsonc": return "json";
                case ".xml": case ".csproj": case ".fsproj": case ".vbproj": case ".xaml": case ".svg": return "xml";
                case ".html": case ".htm": return "html";
                case ".yaml": case ".yml": return "yaml";
                case ".js": case ".mjs": case ".cjs": return "javascript";
                case ".jsx": return "jsx";
                case ".ts": case ".mts": case ".cts": return "typescript";
                case ".tsx": return "tsx";
                case ".py": case ".pyw": return "python";
                case ".ps1": case ".psm1": case ".psd1": return "powershell";
                case ".bat": case ".cmd": return "bat";
                case ".sql": return "sql";
                case ".ini": case ".cfg": case ".conf": return "ini";
                case ".mk": return "makefile";
            }
            switch (Path.GetFileName(file.Name).ToLowerInvariant())
            {
                case "makefile": case "gnumakefile": return "makefile";
                case "dockerfile": case "containerfile": return "dockerfile";
                default: return string.Empty;
            }
        }

        private static MarkdownConversionResult WriteSeparate(
            FolderSnapshot source,
            List<FolderFile> files)
        {
            var outputDirectory = FindAvailableDirectory(SeparateOutputDirectory(source));
            Directory.CreateDirectory(outputDirectory);

            var failures = new List<MarkdownFailure>();
            var written = 0;
            foreach (var file in files)
            {
                var result = Extract.FromFile(
                    file.FullPath,
                    file.Kind.ToLowerInvariant(),
                    file.Extension);
                if (!result.Succeeded)
                {
                    failures.Add(new MarkdownFailure(
                        file.Name,
                        string.IsNullOrWhiteSpace(result.Notes)
                            ? "内容を読み取れませんでした。"
                            : result.Notes));
                    continue;
                }

                var baseName = Path.GetFileNameWithoutExtension(file.Name);
                if (file.Extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
                    || file.Extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase))
                {
                    baseName += ".converted";
                }
                var outputPath = FindAvailableFile(
                    Path.Combine(outputDirectory, SafeFileName(baseName) + ".md"));
                var builder = new StringBuilder();
                builder.Append("# ");
                builder.AppendLine(EscapeHeading(file.Name));
                builder.AppendLine();
                AppendContent(builder, file, result);
                WriteUtf8Atomically(outputPath, builder.ToString());
                written++;
            }

            return new MarkdownConversionResult(
                outputDirectory,
                written,
                written,
                failures);
        }

        private static string CombinedOutputPath(
            FolderSnapshot source,
            IList<FolderFile> files)
        {
            if (source.SourceKind == "folder")
            {
                var directory = new DirectoryInfo(source.DirectoryPath);
                if (directory.Parent != null)
                {
                    return Path.Combine(
                        directory.Parent.FullName,
                        SafeFileName(directory.Name) + ".md");
                }
                return Path.Combine(directory.FullName, "Ferry.md");
            }

            if (files.Count == 1)
            {
                var file = files[0];
                var baseName = Path.GetFileNameWithoutExtension(file.Name);
                if (file.Extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
                    || file.Extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase))
                {
                    baseName += ".converted";
                }
                return Path.Combine(
                    source.DirectoryPath,
                    SafeFileName(baseName) + ".md");
            }

            var parent = new DirectoryInfo(source.DirectoryPath);
            var name = string.IsNullOrWhiteSpace(parent.Name) ? "selection" : parent.Name;
            return Path.Combine(parent.FullName, SafeFileName(name) + ".md");
        }

        private static string SeparateOutputDirectory(FolderSnapshot source)
        {
            if (source.SourceKind == "folder")
            {
                var directory = new DirectoryInfo(source.DirectoryPath);
                if (directory.Parent != null)
                {
                    return Path.Combine(
                        directory.Parent.FullName,
                        SafeFileName(directory.Name) + "-markdown");
                }
            }
            return Path.Combine(source.DirectoryPath, "_markdown");
        }

        private static string SourceTitle(
            FolderSnapshot source,
            IList<FolderFile> files)
        {
            if (source.SourceKind == "files" && files.Count == 1)
            {
                return Path.GetFileNameWithoutExtension(files[0].Name);
            }

            var directory = new DirectoryInfo(source.DirectoryPath);
            return string.IsNullOrWhiteSpace(directory.Name) ? "Ferry" : directory.Name;
        }

        private static string EscapeHeading(string value)
        {
            return (value ?? string.Empty)
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Replace("#", "\\#")
                .Trim();
        }

        private static string SafeFileName(string value)
        {
            var result = value ?? string.Empty;
            foreach (var character in Path.GetInvalidFileNameChars())
            {
                result = result.Replace(character, '_');
            }
            return string.IsNullOrWhiteSpace(result) ? "output" : result.Trim();
        }

        private static string FindAvailableFile(string requestedPath)
        {
            if (!File.Exists(requestedPath) && !Directory.Exists(requestedPath))
            {
                return requestedPath;
            }

            var directory = Path.GetDirectoryName(requestedPath);
            var baseName = Path.GetFileNameWithoutExtension(requestedPath);
            var extension = Path.GetExtension(requestedPath);
            for (var index = 2; index < int.MaxValue; index++)
            {
                var candidate = Path.Combine(
                    directory,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "{0} ({1}){2}",
                        baseName,
                        index,
                        extension));
                if (!File.Exists(candidate) && !Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
            throw new IOException("空いている出力ファイル名を作れませんでした。");
        }

        private static string FindAvailableDirectory(string requestedPath)
        {
            if (!File.Exists(requestedPath) && !Directory.Exists(requestedPath))
            {
                return requestedPath;
            }

            for (var index = 2; index < int.MaxValue; index++)
            {
                var candidate = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} ({1})",
                    requestedPath,
                    index);
                if (!File.Exists(candidate) && !Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
            throw new IOException("空いている出力フォルダ名を作れませんでした。");
        }

        private static void WriteUtf8Atomically(string path, string content)
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                throw new DirectoryNotFoundException(
                    string.Format("出力先のフォルダがありません: {0}", directory));
            }

            var temporaryPath = Path.Combine(
                directory,
                ".ferry-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(temporaryPath, content, new UTF8Encoding(false));
                File.Move(temporaryPath, path);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
    }

    internal sealed class MarkdownConversionResult
    {
        public MarkdownConversionResult(
            string outputPath,
            int filesWritten,
            int convertedCount,
            List<MarkdownFailure> failures)
        {
            OutputPath = outputPath;
            FilesWritten = filesWritten;
            ConvertedCount = convertedCount;
            Failures = failures;
        }

        public string OutputPath { get; private set; }
        public int FilesWritten { get; private set; }
        public int ConvertedCount { get; private set; }
        public int FailedCount { get { return Failures.Count; } }
        public List<MarkdownFailure> Failures { get; private set; }
    }

    internal sealed class MarkdownFailure
    {
        public MarkdownFailure(string name, string error)
        {
            Name = name;
            Error = error;
        }

        public string Name { get; private set; }
        public string Error { get; private set; }
    }
}
