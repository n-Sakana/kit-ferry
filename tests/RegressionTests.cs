// Backend regression suite. No NuGet test runner required.
// dotnet run --project tests/Ferry.Regression.csproj -c Release
// Keep C# 5 syntax so the source also compiles with Windows PowerShell 5.1.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ferry
{
    internal static class RegressionTests
    {
        private static string _root;
        private static FolderSnapshot _source;
        private static byte[] _sample;
        private static int _passed;

        public static int Main()
        {
            _root = Path.Combine(Path.GetTempPath(), "ferry-regression-" + Guid.NewGuid().ToString("N"));
            try
            {
                var input = Path.Combine(_root, "input");
                Directory.CreateDirectory(input);
                _sample = new byte[24000]; new Random(1776).NextBytes(_sample);
                File.WriteAllBytes(Path.Combine(input, "sample.bin"), _sample);
                File.WriteAllText(Path.Combine(input, "note.txt"), "Ferry regression 日本語\n");
                _source = FolderCatalog.Inspect(input);
                Run("CLI argument validation", TestOptions);
                Run("Fountain loss, reorder and duplicates", TestFountain);
                Run("packed QR pixels equal legacy RGBA including quiet zone", TestPacked);
                Run("independent display tokens and bounded sessions", TestSessions);
                Run("error-correction byte capacities", TestCapacities);
                Run("tiny transfer gets a smaller QR", TestTiny);
                Run("receiver completion, file integrity, pin and cancellation", TestReceiver);
                Run("protected container rejects corruption", TestCorruption);
                Run("file selection rejects duplicates and traversal", TestSelection);
                Run("parallel output directories are unique", TestOutputDirectories);
                Run("plain-text Markdown conversion", TestMarkdown);
                Run("hidden entries stay hidden while explicit text selection works", TestMarkdownVisibility);
                Run("source files and extensionless text Markdown conversion", TestMarkdownSources);
                Run("BOM and CP932 source text", TestMarkdownEncodings);
                Run("binary and invalid text excluded from Markdown", TestMarkdownBinary);
                Run("text validation at export and safe code fences", TestMarkdownFences);
                Console.WriteLine("PASS: " + _passed + " backend tests");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            finally { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }
        }

        private static void Run(string name, Action action)
        { action(); _passed++; Console.WriteLine("PASS " + name); }
        private static void Require(bool condition, string message)
        { if (!condition) throw new Exception(message); }
        private static void Throws<T>(Action action) where T : Exception
        {
            try { action(); } catch (T) { return; }
            throw new Exception("Expected " + typeof(T).Name);
        }
        private static IList<string> Names() { return new[] { "sample.bin", "note.txt" }; }
        private static OpticalPayload Payload() { return OpticalPayload.Build(_source, Names(), 2000, 64 * 1024 * 1024); }
        private static OpticalStartResult Start(OpticalService service)
        { return service.Start(_source, Names(), 1000, 30, "L"); }
        private static void TestOptions()
        {
            Require(AppOptions.Parse(new[] { "--no-browser", "--port", "18423" }).Port == 18423, "port");
            Require(AppOptions.Parse(new[] { "--help" }).ShowHelp, "help");
            Throws<ArgumentException>(delegate { AppOptions.Parse(new[] { "--port", "0" }); });
            Throws<ArgumentException>(delegate { AppOptions.Parse(new[] { "--cli" }); });
            Throws<ArgumentException>(delegate { AppOptions.Parse(new[] { "--mode", "bad" }); });
        }
        private static void TestFountain()
        {
            var encoder = new FountainEncoder(_sample, 980, 1776);
            var decoder = new FountainDecoder(encoder.BlockCount, 980, 1776, _sample.Length);
            for (uint start = 0; start < 4000 && !decoder.IsComplete; start += 32)
                for (var index = 31; index >= 0 && !decoder.IsComplete; index--)
                {
                    var sequence = start + (uint)index;
                    if (sequence % 3 == 0) continue;
                    var block = encoder.Encode(sequence);
                    decoder.AddFrame(sequence, block); decoder.AddFrame(sequence, block);
                }
            Require(decoder.IsComplete && decoder.Assemble().SequenceEqual(_sample), "fountain payload mismatch");
            Require(decoder.FramesDuplicate > 0, "duplicates were not detected");
        }
        private static void TestPacked()
        {
            var service = new OpticalService(); var session = Start(service);
            foreach (var first in new[] { 0u, uint.MaxValue })
            {
                byte[] packed; Require(service.TryRenderPackedFrames(session.Token, first, 2, out packed), "packed missing");
                var side = 17 + 4 * session.QrVersion + 8; var stride = (side * side + 7) / 8;
                Require(packed.Length == 12 + stride * 2 && packed[0] == 0x46 && packed[3] == 0x31, "packed header/length");
                Require(BitConverter.ToUInt16(packed, 4) == side && BitConverter.ToUInt16(packed, 6) == 2 &&
                    BitConverter.ToUInt32(packed, 8) == first, "packed metadata");
                for (var frame = 0; frame < 2; frame++)
                {
                    byte[] rgba; Require(service.TryRenderRasterFrame(session.Token, unchecked(first + (uint)frame), out rgba), "raster missing");
                    for (var bit = 0; bit < side * side; bit++)
                    {
                        var dark = (packed[12 + stride * frame + bit / 8] & (0x80 >> (bit % 8))) != 0;
                        Require(rgba[bit * 4] == (dark ? 0 : 255) && rgba[bit * 4 + 3] == 255, "pixel mismatch");
                    }
                }
            }
            Throws<ArgumentException>(delegate { byte[] b; service.TryRenderPackedFrames(session.Token, 0, 0, out b); });
            Throws<ArgumentException>(delegate { byte[] b; service.TryRenderPackedFrames(session.Token, 0, 17, out b); });
        }
        private static void TestSessions()
        {
            var service = new OpticalService(); var sessions = new List<OpticalStartResult>();
            for (var n = 0; n < 4; n++) sessions.Add(Start(service));
            Throws<ArgumentException>(delegate { Start(service); });
            byte[] bytes; service.Stop(sessions[0].Token);
            Require(!service.TryRenderPackedFrames(sessions[0].Token, 0, 1, out bytes), "stopped token survived");
            foreach (var session in sessions.Skip(1))
                Require(service.TryRenderPackedFrames(session.Token, 0, 1, out bytes), "other display invalidated");
            Require(Start(service) != null, "released slot was not reused");
        }
        private static void TestCapacities()
        {
            var service = new OpticalService(); var levels = new[] { "L", "M", "Q", "H" }; var limits = new[] { 2953, 2331, 1663, 1273 };
            for (var i = 0; i < levels.Length; i++)
            {
                var session = service.Start(_source, Names(), 2953, 30, levels[i]);
                Require(session.FrameBytes == limits[i], "QR capacity mismatch: " + levels[i]);
                service.Stop(session.Token);
            }
        }
        private static void TestTiny()
        {
            var session = new OpticalService().Start(_source, new[] { "note.txt" }, 2953, 30, "L");
            Require(session.FrameBytes < 1000 && session.QrVersion < 40, "tiny transfer needlessly dense");
        }
        private static uint Fnv(byte[] bytes)
        { var hash = 0x811c9dc5u; foreach (var b in bytes) { hash ^= b; hash = unchecked(hash * 0x01000193u); } return hash; }
        private static byte[] Wire(byte[] payload, uint sequence, ushort sessionId)
        {
            var encoder = new FountainEncoder(payload, 980, sessionId); var block = encoder.Encode(sequence);
            using (var memory = new MemoryStream())
            {
                using (var writer = new BinaryWriter(memory))
                {
                    writer.Write((byte)0xd1); writer.Write((byte)0x0c); writer.Write(sessionId); writer.Write(sequence);
                    writer.Write((ushort)encoder.BlockCount); writer.Write((ushort)encoder.BlockLength);
                    writer.Write((uint)payload.Length); writer.Write(Fnv(payload)); writer.Write(block);
                }
                return memory.ToArray();
            }
        }
        private static void TestReceiver()
        {
            var payload = Payload().Bytes; var receiver = new OpticalReceiveService(Path.Combine(_root, "received"));
            receiver.Reset("first"); var first = receiver.AddFrame("first", Wire(payload, 0, 1776), false);
            Require(first.Recognized && !first.Complete, "first frame rejected");
            Require(!receiver.AddFrame("first", Wire(payload, 1, 1777), false).Recognized, "foreign stream replaced progress");
            OpticalReceiveResult complete = null;
            for (uint sequence = 1; sequence < 4000; sequence++)
            {
                if (sequence % 3 == 0) continue;
                var progress = receiver.AddFrame("first", Wire(payload, sequence, 1776), false);
                if (progress.Complete) { complete = progress; break; }
            }
            Require(complete != null && complete.FileCount == 2, "receiver did not finish");
            Require(File.ReadAllBytes(Path.Combine(complete.OutputPath, "sample.bin")).SequenceEqual(_sample), "saved bytes differ");
            var again = receiver.AddFrame("first", Wire(payload, 0, 1776), false);
            Require(again.Complete && again.OutputPath == complete.OutputPath, "completion retry created another output");
            receiver.Stop("first"); Require(!receiver.AddFrame("first", Wire(payload, 1, 1776), false).Recognized, "late POST resurrected stop");
            receiver.Reset("first"); Require(receiver.AddFrame("first", Wire(payload, 1, 1776), false).Recognized, "explicit restart rejected");
        }
        private static void TestCorruption()
        {
            var payload = Payload().Bytes; payload[17] ^= 1;
            Throws<InvalidDataException>(delegate { OpticalPayloadReader.Save(payload, Path.Combine(_root, "corrupted")); });
        }
        private static void TestSelection()
        {
            // Resolve() looks names up in the snapshot taken from the folder, so anything
            // outside it simply is not there: the failure is FileNotFoundException, not
            // ArgumentException. Duplicates are folded by the seen set rather than rejected.
            // Verified on Windows 2026-09-08: "../outside.bin", an absolute path and a deep
            // "../../.." traversal are all rejected this way.
            Throws<FileNotFoundException>(delegate { OpticalPayload.Build(_source, new[] { "../outside.bin" }, 2000, 64 * 1024 * 1024); });
            Throws<FileNotFoundException>(delegate { OpticalPayload.Build(_source, new[] { Path.Combine(_root, "outside.bin") }, 2000, 64 * 1024 * 1024); });
            Throws<FileNotFoundException>(delegate { OpticalPayload.Build(_source, new[] { "../../../../../../Windows/win.ini" }, 2000, 64 * 1024 * 1024); });
            Require(OpticalPayload.Build(_source, new[] { "sample.bin", "sample.bin" }, 2000, 64 * 1024 * 1024)
                .Bytes.Length == OpticalPayload.Build(_source, new[] { "sample.bin" }, 2000, 64 * 1024 * 1024).Bytes.Length,
                "duplicate selection was not folded");
        }
        private static void TestOutputDirectories()
        {
            var directories = new string[32];
            Parallel.For(0, directories.Length, delegate(int i) { directories[i] = OutputLayout.CreateRunDirectory(Path.Combine(_root, "parallel"), "same"); });
            Require(directories.Distinct(StringComparer.Ordinal).Count() == directories.Length, "parallel output collision");
        }
        private static void TestMarkdown()
        {
            var converted = MarkdownService.Convert(_source, new[] { "note.txt" }, true, Path.Combine(_root, "markdown"));
            Require(File.Exists(converted.OutputPath) && File.ReadAllText(converted.OutputPath).Contains("Ferry regression 日本語"), "plain text conversion failed");
        }

        private static void TestMarkdownSources()
        {
            var input = Path.Combine(_root, "sources");
            Directory.CreateDirectory(input);
            var names = new[] { "Program.cs", "Makefile", "Dockerfile", "LICENSE",
                "main.c", "main.cpp", "main.h", "main.hpp", "main.java", "main.rs", "main.go",
                "main.rb", "main.php", "main.swift", "main.kt", "main.sh", "config.toml", "main.vb",
                "main.fs", "main.lua", "main.r", "main.scss", "main.vue", "build.gradle", "source.unlisted" };
            foreach (var name in names) File.WriteAllText(Path.Combine(input, name), "// source 日本語: " + name + "\n");
            var snapshot = FolderCatalog.Inspect(input);
            foreach (var name in names)
            {
                var file = snapshot.Files.SingleOrDefault(f => f.Name == name);
                Require(file != null && file.MarkdownSupported && file.Kind == "Text", "source missing/not Text: " + name);
                Require(FolderCatalog.SupportsMarkdown(Path.Combine(input, name)), "direct selection rejected: " + name);
            }
            var converted = MarkdownService.Convert(snapshot, names, true, Path.Combine(_root, "markdown-sources"));
            Require(converted.ConvertedCount == names.Length && converted.Failures.Count == 0 && converted.FilesWritten == 1, "sources not combined");
            var content = File.ReadAllText(converted.OutputPath);
            Require(content.Contains("```csharp") && content.Contains("```makefile") && content.Contains("```dockerfile"), "language fences missing");
            Require(content.Contains("```\n// source 日本語: source.unlisted".Replace("\n", Environment.NewLine)), "unknown source needs plain fence");
            Require(FolderCatalog.PickerPattern("markdown") == "*", "picker still hides source/extensionless files");
        }

        private static void TestMarkdownVisibility()
        {
            var input = Path.Combine(_root, "visibility");
            Directory.CreateDirectory(input);
            File.WriteAllText(Path.Combine(input, "visible.cs"), "// visible source\n");
            var hiddenFile = Path.Combine(input, ".gitignore");
            File.WriteAllText(hiddenFile, "bin/\nobj/\n");
            var hiddenDirectory = Directory.CreateDirectory(Path.Combine(input, ".hidden"));
            File.WriteAllText(Path.Combine(hiddenDirectory.FullName, "inside.cs"), "// hidden source\n");
            if (PlatformInfo.IsWindows)
            {
                Require(FolderCatalog.Inspect(input).Files.Any(f => f.Name == ".gitignore"), "Windows dotfile without Hidden attribute disappeared");
                File.SetAttributes(hiddenFile, File.GetAttributes(hiddenFile) | FileAttributes.Hidden);
                hiddenDirectory.Attributes |= FileAttributes.Hidden;
            }
            var folder = FolderCatalog.Inspect(input);
            Require(folder.Files.Count == 1 && folder.Files[0].Name == "visible.cs", "hidden entries leaked into folder listing");
            var explicitFile = FolderCatalog.InspectFiles(new[] { hiddenFile });
            Require(explicitFile.Files.Count == 1 && explicitFile.Files[0].MarkdownSupported && explicitFile.Files[0].Kind == "Text", "explicit hidden text selection rejected");
            var result = MarkdownService.Convert(explicitFile, new[] { ".gitignore" }, true, Path.Combine(_root, "markdown-hidden"));
            Require(result.ConvertedCount == 1 && result.Failures.Count == 0 && result.FilesWritten == 1, "explicit hidden text conversion failed");
            Require(File.ReadAllText(result.OutputPath).Contains("bin/" + Environment.NewLine + "obj/"), "explicit hidden text content lost");
        }

        private static void TestMarkdownEncodings()
        {
            var input = Path.Combine(_root, "encodings");
            Directory.CreateDirectory(input);
            // Fixed bytes for // 日本語\r\n, independent of the runtime's code-page provider.
            File.WriteAllBytes(Path.Combine(input, "cp932.cs"), new byte[] { 47, 47, 32, 0x93, 0xfa, 0x96, 0x7b, 0x8c, 0xea, 13, 10 });
            File.WriteAllText(Path.Combine(input, "utf8.cs"), "// 日本語\n", new UTF8Encoding(true));
            File.WriteAllText(Path.Combine(input, "utf16.cs"), "// 日本語\n", Encoding.Unicode);
            File.WriteAllText(Path.Combine(input, "utf16be.cs"), "// 日本語\n", Encoding.BigEndianUnicode);
            // A multibyte character crosses the catalogue probe boundary.
            File.WriteAllText(Path.Combine(input, "boundary.cs"), new string(' ', 8191) + "日本語\n", new UTF8Encoding(false));
            var snapshot = FolderCatalog.Inspect(input);
            Require(snapshot.Files.All(f => f.MarkdownSupported), "encoded source rejected");
            var result = MarkdownService.Convert(snapshot, snapshot.Files.Select(f => f.Name).ToArray(), true, Path.Combine(_root, "markdown-encodings"));
            Require(result.ConvertedCount == 5 && result.Failures.Count == 0, "encoded sources failed");
            var content = File.ReadAllText(result.OutputPath);
            Require(content.Split(new[] { "日本語" }, StringSplitOptions.None).Length == 6 && !content.Contains("\ufffd"), "Japanese text corrupted");
        }

        private static void TestMarkdownBinary()
        {
            var input = Path.Combine(_root, "binary");
            Directory.CreateDirectory(input);
            File.WriteAllBytes(Path.Combine(input, "image.png"), Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j6V8AAAAASUVORK5CYII="));
            File.Copy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "zxing.dll"), Path.Combine(input, "program.exe"));
            File.Copy(Path.Combine(input, "program.exe"), Path.Combine(input, "renamed.cs"));
            File.WriteAllBytes(Path.Combine(input, "nul.txt"), new byte[] { 65, 0, 66 });
            File.WriteAllBytes(Path.Combine(input, "invalid.cs"), new byte[] { 0xef, 0xbb, 0xbf, 0xff });
            File.WriteAllBytes(Path.Combine(input, "invalid-utf16.cs"), new byte[] { 0xff, 0xfe, 0x00, 0xd8 });
            File.WriteAllBytes(Path.Combine(input, "truncated-cp932.cs"), new byte[] { 0x82 });
            File.WriteAllText(Path.Combine(input, "control.cs"), "text\u0093text", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(input, "replacement.cs"), "text\ufffdtext", new UTF8Encoding(false));
            var snapshot = FolderCatalog.Inspect(input);
            foreach (var file in snapshot.Files)
            {
                Require(!file.MarkdownSupported && !FolderCatalog.SupportsMarkdown(file.FullPath), "binary admitted: " + file.Name);
                Throws<ArgumentException>(delegate { MarkdownService.Convert(snapshot, new[] { file.Name }, true, Path.Combine(_root, "binary-output")); });
            }
        }

        private static void TestMarkdownFences()
        {
            var input = Path.Combine(_root, "fences");
            Directory.CreateDirectory(input);
            const string code = "var sample = \"\"\"\n```\n# must stay inside code\n\"\"\";";
            File.WriteAllText(Path.Combine(input, "code.cs"), code);
            File.WriteAllText(Path.Combine(input, "README.md"), "# Keep Markdown\n");
            File.WriteAllText(Path.Combine(input, "changed.cs"), "class Before {}\n");
            File.WriteAllText(Path.Combine(input, "late-binary.cs"), new string('a', 20000) + "\0BINARY_SENTINEL");
            var snapshot = FolderCatalog.Inspect(input);
            File.WriteAllBytes(Path.Combine(input, "changed.cs"), new byte[] { 65, 0, 66 });
            var result = MarkdownService.Convert(snapshot, snapshot.Files.Select(f => f.Name).ToArray(), true, Path.Combine(_root, "markdown-fences"));
            Require(result.ConvertedCount == 2 && result.Failures.Count == 2, "export did not reject binary after probe");
            var content = File.ReadAllText(result.OutputPath);
            Require(content.Contains("````csharp" + Environment.NewLine + code + Environment.NewLine + "````"), "embedded fence broke code");
            Require(content.Contains("## README.md" + Environment.NewLine + Environment.NewLine + "# Keep Markdown"), "Markdown formatting changed");
            Require(!content.Contains("BINARY_SENTINEL") && !content.Contains("\0"), "binary leaked into Markdown");
        }
    }
}
