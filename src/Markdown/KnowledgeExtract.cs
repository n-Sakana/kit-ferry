using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace KnowledgeStudio
{
    public sealed class ExtractResult
    {
        public string Content;
        public string Method;
        public string Notes;
        public bool Succeeded;

        public static ExtractResult Failure(string method, string notes)
        {
            ExtractResult result = new ExtractResult();
            result.Content = string.Empty;
            result.Method = method;
            result.Notes = notes;
            result.Succeeded = false;
            return result;
        }
    }

    // Text out of files, with no Office and no network involved.
    //
    // The OpenXML readers are a port of the ones in ToolRack's md-extract,
    // which have been in daily use; the limits they carry (entry counts, part
    // sizes, no DTDs, no external resolver) are the reason a hostile .docx
    // cannot turn a scan into a hang or a fetch, so they are ported with the
    // limits intact rather than trimmed.
    public static class Extract
    {
        private const long MaxXmlEntryBytes = 64L * 1024L * 1024L;
        private const long MaxOpenXmlBytes = 256L * 1024L * 1024L;
        private const int MaxOpenXmlEntries = 10000;
        private const int MaxExcelColumn = 16384;

        public static ExtractResult FromFile(string fullPath, string kind, string extension)
        {
            string ext = (extension ?? Path.GetExtension(fullPath) ?? string.Empty)
                .ToLowerInvariant();

            try
            {
                switch (kind)
                {
                    case "markdown":
                    case "text":
                        return ReadTextFile(fullPath);
                    case "word":
                        if (ext == ".docx" || ext == ".docm")
                        {
                            return ReadWordOpenXml(fullPath);
                        }
                        return ExtractResult.Failure(
                            "none",
                            "旧形式の Word (" + ext + ") はテキスト抽出に対応していません。");
                    case "excel":
                        if (ext == ".xlsx" || ext == ".xlsm")
                        {
                            return ReadExcelOpenXml(fullPath);
                        }
                        return ExtractResult.Failure(
                            "none",
                            "旧形式の Excel (" + ext + ") はテキスト抽出に対応していません。");
                    case "powerpoint":
                        if (ext == ".pptx" || ext == ".pptm")
                        {
                            return ReadPowerPointOpenXml(fullPath);
                        }
                        return ExtractResult.Failure(
                            "none",
                            "旧形式の PowerPoint (" + ext + ") はテキスト抽出に対応していません。");
                    case "pdf":
                        return PdfText.Read(fullPath);
                    case "image":
                        return ExtractResult.Failure(
                            "none",
                            "画像はそのまま添付します（テキスト抽出はしません）。");
                    default:
                        return ExtractResult.Failure(
                            "none",
                            "テキスト抽出には対応していない種類です。");
                }
            }
            catch (Exception ex)
            {
                return ExtractResult.Failure(
                    "error",
                    "抽出に失敗しました: " + ex.GetType().Name + " " + ex.Message);
            }
        }

        // Catalogue only a bounded prefix. The full contents are validated again
        // at export, so a binary tail or a file changed after selection cannot leak.
        public static bool IsTextFile(string path)
        {
            try
            {
                using (FileStream stream = File.OpenRead(path))
                {
                    byte[] sample = new byte[8192];
                    int count = 0;
                    int read;
                    while (count < sample.Length && (read = stream.Read(sample, count, sample.Length - count)) > 0)
                        count += read;
                    return DecodeText(sample, count, stream.ReadByte() == -1).Succeeded;
                }
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        // Preserve the existing BOM -> UTF-8 -> CP932 order, but never substitute
        // malformed bytes. All text admission and extraction use this same decoder.
        public static ExtractResult ReadTextFile(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            return DecodeText(bytes, bytes.Length, true);
        }

        private static ExtractResult DecodeText(byte[] bytes, int count, bool complete)
        {
            if (count >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return DecodeText(bytes, count, 3, new UTF8Encoding(false, true), "UTF-8 BOM", complete);
            if (count >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return DecodeText(bytes, count, 2, new UnicodeEncoding(false, false, true), "UTF-16 LE", complete);
            if (count >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return DecodeText(bytes, count, 2, new UnicodeEncoding(true, false, true), "UTF-16 BE", complete);

            var utf8 = DecodeText(bytes, count, 0, new UTF8Encoding(false, true), "UTF-8", complete);
            if (utf8.Succeeded) return utf8;
            return DecodeText(bytes, count, 0, Cp932.Strict, "CP932", complete);
        }

        private static ExtractResult DecodeText(byte[] bytes, int count, int offset,
            Encoding encoding, string name, bool complete)
        {
            try
            {
                char[] chars = new char[encoding.GetMaxCharCount(count - offset)];
                // Do not mistake a multibyte character cut by the catalogue sample
                // boundary for bad input. EOF and export always flush the decoder.
                int length = encoding.GetDecoder().GetChars(bytes, offset, count - offset, chars, 0, complete);
                for (int i = 0; i < length; i++)
                {
                    char c = chars[i];
                    if (c == '\ufffd' || (char.IsControl(c) && c != '\t' && c != '\r' && c != '\n' && c != '\f'))
                        return ExtractResult.Failure("text", "バイナリまたは不正な文字を含むため、テキストとして読み取れません。");
                }
                return new ExtractResult { Content = new string(chars, 0, length), Method = "text", Notes = name, Succeeded = true };
            }
            catch (DecoderFallbackException)
            {
                return ExtractResult.Failure("text", "文字コードを正しく読み取れません (" + name + ")。");
            }
        }

        private static class Cp932
        {
            public static readonly Encoding Strict = Create();

            private static Encoding Create()
            {
                // .NET Framework already has CP932; modern .NET registers its
                // built-in provider, just as the VBA reader does.
                var provider = Type.GetType("System.Text.CodePagesEncodingProvider, System.Text.Encoding.CodePages", false);
                if (provider != null)
                {
                    var register = typeof(Encoding).GetMethod("RegisterProvider", new[] { provider.BaseType });
                    if (register != null) register.Invoke(null, new[] { provider.GetProperty("Instance").GetValue(null, null) });
                }
                return Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            }
        }

        public static ExtractResult ReadWordOpenXml(string path)
        {
            StringBuilder builder = new StringBuilder();
            int partCount = 0;

            using (FileStream stream = File.OpenRead(path))
            using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                ValidateOpenXmlArchive(archive);
                string[] entries = new string[]
                {
                    "word/document.xml",
                    "word/footnotes.xml",
                    "word/endnotes.xml",
                    "word/comments.xml"
                };

                int i;
                for (i = 0; i < entries.Length; i++)
                {
                    ZipArchiveEntry entry = GetEntry(archive, entries[i]);
                    if (entry == null)
                    {
                        continue;
                    }
                    AppendWordPart(archive, entry.FullName, GetWordPartLabel(entry.FullName), builder);
                    partCount++;
                }

                partCount += AppendMatchingWordParts(
                    archive, @"^word/headers/header[0-9]+\.xml$", "Header", builder);
                partCount += AppendMatchingWordParts(
                    archive, @"^word/footers/footer[0-9]+\.xml$", "Footer", builder);
            }

            ExtractResult result = new ExtractResult();
            result.Content = TrimEndLines(builder.ToString());
            result.Method = "word-openxml";
            result.Notes = "パート数: " + partCount.ToString(CultureInfo.InvariantCulture);
            result.Succeeded = true;
            if (string.IsNullOrWhiteSpace(result.Content))
            {
                result.Notes += "; テキストは取れませんでした";
                result.Succeeded = false;
            }
            return result;
        }

        public static ExtractResult ReadPowerPointOpenXml(string path)
        {
            StringBuilder builder = new StringBuilder();
            int slideCount = 0;
            int noteCount = 0;

            using (FileStream stream = File.OpenRead(path))
            using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                ValidateOpenXmlArchive(archive);
                List<ZipArchiveEntry> slides = GetMatchingEntries(
                    archive, @"^ppt/slides/slide[0-9]+\.xml$");
                slides.Sort(CompareEntryNumber);

                foreach (ZipArchiveEntry entry in slides)
                {
                    int number = GetLastNumber(entry.FullName);
                    if (builder.Length > 0)
                    {
                        builder.AppendLine();
                    }
                    builder.AppendLine("## スライド " +
                        number.ToString(CultureInfo.InvariantCulture));
                    AppendPresentationXml(entry, builder);
                    slideCount++;
                }

                List<ZipArchiveEntry> notes = GetMatchingEntries(
                    archive, @"^ppt/notesSlides/notesSlide[0-9]+\.xml$");
                notes.Sort(CompareEntryNumber);

                foreach (ZipArchiveEntry entry in notes)
                {
                    int number = GetLastNumber(entry.FullName);
                    if (builder.Length > 0)
                    {
                        builder.AppendLine();
                    }
                    builder.AppendLine("## ノート " +
                        number.ToString(CultureInfo.InvariantCulture));
                    AppendPresentationXml(entry, builder);
                    noteCount++;
                }
            }

            ExtractResult result = new ExtractResult();
            result.Content = TrimEndLines(builder.ToString());
            result.Method = "powerpoint-openxml";
            result.Notes =
                "スライド: " + slideCount.ToString(CultureInfo.InvariantCulture) +
                "; ノート: " + noteCount.ToString(CultureInfo.InvariantCulture);
            result.Succeeded = true;
            if (string.IsNullOrWhiteSpace(result.Content))
            {
                result.Notes += "; テキストは取れませんでした";
                result.Succeeded = false;
            }
            return result;
        }

        public static ExtractResult ReadExcelOpenXml(string path)
        {
            StringBuilder builder = new StringBuilder();
            int sheetCount = 0;

            using (FileStream stream = File.OpenRead(path))
            using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                ValidateOpenXmlArchive(archive);
                List<string> sharedStrings = LoadSharedStrings(archive);
                List<SheetInfo> sheets = LoadWorkbookSheets(archive);

                if (sheets.Count == 0)
                {
                    List<ZipArchiveEntry> sheetEntries = GetMatchingEntries(
                        archive, @"^xl/worksheets/sheet[0-9]+\.xml$");
                    sheetEntries.Sort(CompareEntryNumber);
                    foreach (ZipArchiveEntry entry in sheetEntries)
                    {
                        sheets.Add(new SheetInfo(
                            entry.FullName,
                            Path.GetFileNameWithoutExtension(entry.FullName)));
                    }
                }

                foreach (SheetInfo sheet in sheets)
                {
                    ZipArchiveEntry entry = GetEntry(archive, sheet.Path);
                    if (entry == null)
                    {
                        continue;
                    }
                    if (builder.Length > 0)
                    {
                        builder.AppendLine();
                    }
                    builder.AppendLine("## シート: " + sheet.Name);
                    AppendWorksheetXml(entry, sharedStrings, builder);
                    sheetCount++;
                }
            }

            ExtractResult result = new ExtractResult();
            result.Content = TrimEndLines(builder.ToString());
            result.Method = "excel-openxml";
            result.Notes = "シート: " + sheetCount.ToString(CultureInfo.InvariantCulture);
            result.Succeeded = true;
            if (string.IsNullOrWhiteSpace(result.Content))
            {
                result.Notes += "; テキストは取れませんでした";
                result.Succeeded = false;
            }
            return result;
        }

        private sealed class SheetInfo
        {
            public string Path;
            public string Name;

            public SheetInfo(string path, string name)
            {
                Path = NormalizePackagePath(path);
                Name = name;
            }
        }

        private static void AppendWordPart(
            ZipArchive archive,
            string entryName,
            string label,
            StringBuilder builder)
        {
            ZipArchiveEntry entry = GetEntry(archive, entryName);
            if (entry == null)
            {
                return;
            }
            if (builder.Length > 0)
            {
                builder.AppendLine();
            }
            builder.AppendLine("## " + label);
            XmlDocument document = LoadEntryXml(entry);
            AppendWordNode(document.DocumentElement, builder);
        }

        private static int AppendMatchingWordParts(
            ZipArchive archive,
            string pattern,
            string labelPrefix,
            StringBuilder builder)
        {
            int count = 0;
            List<ZipArchiveEntry> entries = GetMatchingEntries(archive, pattern);
            entries.Sort(CompareEntryNumber);
            foreach (ZipArchiveEntry entry in entries)
            {
                string label = labelPrefix + " " +
                    GetLastNumber(entry.FullName).ToString(CultureInfo.InvariantCulture);
                AppendWordPart(archive, entry.FullName, label, builder);
                count++;
            }
            return count;
        }

        private static string GetWordPartLabel(string entryName)
        {
            if (entryName.EndsWith("/document.xml", StringComparison.OrdinalIgnoreCase))
            {
                return "本文";
            }
            if (entryName.EndsWith("/footnotes.xml", StringComparison.OrdinalIgnoreCase))
            {
                return "脚注";
            }
            if (entryName.EndsWith("/endnotes.xml", StringComparison.OrdinalIgnoreCase))
            {
                return "文末脚注";
            }
            if (entryName.EndsWith("/comments.xml", StringComparison.OrdinalIgnoreCase))
            {
                return "コメント";
            }
            return entryName;
        }

        private static void AppendWordNode(XmlNode node, StringBuilder builder)
        {
            if (node == null)
            {
                return;
            }

            string localName = node.LocalName;
            if (localName == "t" || localName == "instrText" || localName == "delText")
            {
                builder.Append(node.InnerText);
                return;
            }
            if (localName == "tab")
            {
                builder.Append('\t');
                return;
            }
            if (localName == "br" || localName == "cr")
            {
                builder.AppendLine();
                return;
            }

            foreach (XmlNode child in node.ChildNodes)
            {
                AppendWordNode(child, builder);
            }

            if (localName == "p" || localName == "tr")
            {
                AppendLineIfNeeded(builder);
            }
            else if (localName == "tc")
            {
                builder.Append('\t');
            }
        }

        private static void AppendPresentationXml(ZipArchiveEntry entry, StringBuilder builder)
        {
            XmlDocument document = LoadEntryXml(entry);
            AppendPresentationNode(document.DocumentElement, builder);
        }

        private static void AppendPresentationNode(XmlNode node, StringBuilder builder)
        {
            if (node == null)
            {
                return;
            }

            string localName = node.LocalName;
            if (localName == "t")
            {
                builder.Append(node.InnerText);
                return;
            }
            if (localName == "br")
            {
                builder.AppendLine();
                return;
            }

            foreach (XmlNode child in node.ChildNodes)
            {
                AppendPresentationNode(child, builder);
            }

            if (localName == "p")
            {
                AppendLineIfNeeded(builder);
            }
        }

        private static List<string> LoadSharedStrings(ZipArchive archive)
        {
            List<string> values = new List<string>();
            ZipArchiveEntry entry = GetEntry(archive, "xl/sharedStrings.xml");
            if (entry == null)
            {
                return values;
            }

            XmlDocument document = LoadEntryXml(entry);
            List<XmlNode> items = new List<XmlNode>();
            CollectNodesByLocalName(document.DocumentElement, "si", items);
            foreach (XmlNode item in items)
            {
                values.Add(CollapseCellText(CollectTextFromNode(item)));
            }
            return values;
        }

        private static List<SheetInfo> LoadWorkbookSheets(ZipArchive archive)
        {
            List<SheetInfo> sheets = new List<SheetInfo>();
            ZipArchiveEntry workbookEntry = GetEntry(archive, "xl/workbook.xml");
            if (workbookEntry == null)
            {
                return sheets;
            }

            Dictionary<string, string> relationships = LoadWorkbookRelationships(archive);
            XmlDocument document = LoadEntryXml(workbookEntry);
            List<XmlNode> sheetNodes = new List<XmlNode>();
            CollectNodesByLocalName(document.DocumentElement, "sheet", sheetNodes);

            foreach (XmlNode sheetNode in sheetNodes)
            {
                string name = GetAttributeValue(sheetNode, "name");
                string relationshipId = GetAttributeValue(sheetNode, "id");
                if (string.IsNullOrEmpty(name))
                {
                    name = "Sheet " + (sheets.Count + 1).ToString(CultureInfo.InvariantCulture);
                }

                string target;
                if (!string.IsNullOrEmpty(relationshipId) &&
                    relationships.TryGetValue(relationshipId, out target))
                {
                    sheets.Add(new SheetInfo(target, name));
                }
            }

            return sheets;
        }

        private static Dictionary<string, string> LoadWorkbookRelationships(ZipArchive archive)
        {
            Dictionary<string, string> map = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            ZipArchiveEntry relsEntry = GetEntry(archive, "xl/_rels/workbook.xml.rels");
            if (relsEntry == null)
            {
                return map;
            }

            XmlDocument document = LoadEntryXml(relsEntry);
            List<XmlNode> relationships = new List<XmlNode>();
            CollectNodesByLocalName(document.DocumentElement, "Relationship", relationships);
            foreach (XmlNode relationship in relationships)
            {
                string id = GetAttributeValue(relationship, "Id");
                string target = GetAttributeValue(relationship, "Target");
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(target))
                {
                    continue;
                }
                map[id] = NormalizeWorkbookTarget(target);
            }
            return map;
        }

        private static void AppendWorksheetXml(
            ZipArchiveEntry entry,
            List<string> sharedStrings,
            StringBuilder builder)
        {
            XmlDocument document = LoadEntryXml(entry);
            List<XmlNode> rows = new List<XmlNode>();
            CollectNodesByLocalName(document.DocumentElement, "row", rows);

            foreach (XmlNode row in rows)
            {
                SortedDictionary<int, string> cells = new SortedDictionary<int, string>();
                int fallbackColumn = 1;
                foreach (XmlNode child in row.ChildNodes)
                {
                    if (child.LocalName != "c")
                    {
                        continue;
                    }

                    string reference = GetAttributeValue(child, "r");
                    int columnIndex = GetColumnIndex(reference);
                    if (columnIndex <= 0)
                    {
                        columnIndex = fallbackColumn;
                    }

                    cells[columnIndex] = ReadCellValue(child, sharedStrings);
                    fallbackColumn = columnIndex + 1;
                }

                if (cells.Count == 0)
                {
                    builder.AppendLine();
                    continue;
                }

                int maxColumn = 0;
                foreach (int column in cells.Keys)
                {
                    if (column > maxColumn)
                    {
                        maxColumn = column;
                    }
                }

                string[] values = new string[maxColumn];
                int i;
                for (i = 0; i < values.Length; i++)
                {
                    values[i] = string.Empty;
                }
                foreach (KeyValuePair<int, string> cell in cells)
                {
                    if (cell.Key > 0 && cell.Key <= values.Length)
                    {
                        values[cell.Key - 1] = CollapseCellText(cell.Value);
                    }
                }
                builder.AppendLine(string.Join("\t", values));
            }
        }

        private static string ReadCellValue(XmlNode cell, List<string> sharedStrings)
        {
            string type = GetAttributeValue(cell, "t");
            if (string.Equals(type, "inlineStr", StringComparison.OrdinalIgnoreCase))
            {
                return CollectTextFromNode(cell);
            }

            XmlNode valueNode = FindFirstDescendantByLocalName(cell, "v");
            string rawValue = valueNode == null ? string.Empty : valueNode.InnerText;

            if (string.Equals(type, "s", StringComparison.OrdinalIgnoreCase))
            {
                int index;
                if (int.TryParse(rawValue, out index) &&
                    index >= 0 && index < sharedStrings.Count)
                {
                    return sharedStrings[index];
                }
                return rawValue;
            }

            return rawValue;
        }

        private static string CollectTextFromNode(XmlNode node)
        {
            StringBuilder builder = new StringBuilder();
            CollectTextFromNode(node, builder);
            return builder.ToString();
        }

        private static void CollectTextFromNode(XmlNode node, StringBuilder builder)
        {
            if (node == null)
            {
                return;
            }
            if (node.LocalName == "t")
            {
                builder.Append(node.InnerText);
                return;
            }
            foreach (XmlNode child in node.ChildNodes)
            {
                CollectTextFromNode(child, builder);
            }
        }

        private static void CollectNodesByLocalName(
            XmlNode node,
            string localName,
            List<XmlNode> results)
        {
            if (node == null)
            {
                return;
            }
            if (node.LocalName == localName)
            {
                results.Add(node);
            }
            foreach (XmlNode child in node.ChildNodes)
            {
                CollectNodesByLocalName(child, localName, results);
            }
        }

        private static XmlNode FindFirstDescendantByLocalName(XmlNode node, string localName)
        {
            if (node == null)
            {
                return null;
            }
            if (node.LocalName == localName)
            {
                return node;
            }
            foreach (XmlNode child in node.ChildNodes)
            {
                XmlNode found = FindFirstDescendantByLocalName(child, localName);
                if (found != null)
                {
                    return found;
                }
            }
            return null;
        }

        private static string GetAttributeValue(XmlNode node, string localName)
        {
            if (node == null || node.Attributes == null)
            {
                return string.Empty;
            }
            foreach (XmlAttribute attribute in node.Attributes)
            {
                if (attribute.LocalName == localName || attribute.Name == localName)
                {
                    return attribute.Value;
                }
            }
            return string.Empty;
        }

        private static int GetColumnIndex(string cellReference)
        {
            if (string.IsNullOrEmpty(cellReference))
            {
                return 0;
            }

            int result = 0;
            foreach (char ch in cellReference.ToUpperInvariant())
            {
                if (ch < 'A' || ch > 'Z')
                {
                    break;
                }
                if (result > (MaxExcelColumn - (ch - 'A' + 1)) / 26)
                {
                    throw new InvalidDataException(
                        "Excel cell reference exceeds column XFD: " + cellReference);
                }
                result = (result * 26) + (ch - 'A' + 1);
            }
            return result;
        }

        private static string CollapseCellText(string text)
        {
            if (text == null)
            {
                return string.Empty;
            }
            return text.Replace("\r\n", " ").Replace("\n", " ")
                .Replace("\r", " ").Replace("\t", " ").Trim();
        }

        private static string NormalizeWorkbookTarget(string target)
        {
            if (string.IsNullOrEmpty(target))
            {
                return target;
            }
            target = target.Replace('\\', '/');
            if (target.StartsWith("/", StringComparison.Ordinal))
            {
                target = target.TrimStart('/');
            }
            else if (!target.StartsWith("xl/", StringComparison.OrdinalIgnoreCase))
            {
                target = "xl/" + target;
            }
            return NormalizePackagePath(target);
        }

        private static string NormalizePackagePath(string path)
        {
            return (path ?? string.Empty).Replace('\\', '/').TrimStart('/');
        }

        private static XmlDocument LoadEntryXml(ZipArchiveEntry entry)
        {
            if (entry == null)
            {
                throw new ArgumentNullException("entry");
            }
            if (entry.Length < 0 || entry.Length > MaxXmlEntryBytes)
            {
                throw new InvalidDataException(
                    "OpenXML part exceeds the 64 MiB XML limit: " + entry.FullName);
            }

            XmlDocument document = new XmlDocument();
            document.PreserveWhitespace = false;
            document.XmlResolver = null;
            using (Stream stream = entry.Open())
            using (XmlReader reader = XmlReader.Create(stream, CreateSecureXmlSettings()))
            {
                document.Load(reader);
            }
            return document;
        }

        private static XmlReaderSettings CreateSecureXmlSettings()
        {
            XmlReaderSettings settings = new XmlReaderSettings();
            settings.DtdProcessing = DtdProcessing.Prohibit;
            settings.XmlResolver = null;
            settings.MaxCharactersFromEntities = 0;
            settings.MaxCharactersInDocument = MaxXmlEntryBytes;
            settings.CloseInput = false;
            return settings;
        }

        private static void ValidateOpenXmlArchive(ZipArchive archive)
        {
            if (archive.Entries.Count > MaxOpenXmlEntries)
            {
                throw new InvalidDataException("OpenXML package exceeds the 10000-entry limit.");
            }

            long xmlBytes = 0;
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string name = NormalizePackagePath(entry.FullName);
                if (!names.Add(name))
                {
                    throw new InvalidDataException(
                        "OpenXML package contains a duplicate part name: " + name);
                }
                if (!name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (entry.Length < 0 || entry.Length > MaxXmlEntryBytes)
                {
                    throw new InvalidDataException(
                        "OpenXML part exceeds the 64 MiB XML limit: " + name);
                }
                if (xmlBytes > MaxOpenXmlBytes - entry.Length)
                {
                    throw new InvalidDataException("OpenXML package exceeds the 256 MiB XML limit.");
                }
                xmlBytes += entry.Length;
            }
        }

        private static ZipArchiveEntry GetEntry(ZipArchive archive, string name)
        {
            string normalizedName = NormalizePackagePath(name);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (string.Equals(
                    NormalizePackagePath(entry.FullName),
                    normalizedName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
                }
            }
            return null;
        }

        private static List<ZipArchiveEntry> GetMatchingEntries(ZipArchive archive, string pattern)
        {
            Regex regex = new Regex(
                pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            List<ZipArchiveEntry> entries = new List<ZipArchiveEntry>();
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string name = NormalizePackagePath(entry.FullName);
                if (regex.IsMatch(name))
                {
                    entries.Add(entry);
                }
            }
            return entries;
        }

        private static int CompareEntryNumber(ZipArchiveEntry left, ZipArchiveEntry right)
        {
            int leftNumber = GetLastNumber(left.FullName);
            int rightNumber = GetLastNumber(right.FullName);
            int numberCompare = leftNumber.CompareTo(rightNumber);
            if (numberCompare != 0)
            {
                return numberCompare;
            }
            return StringComparer.OrdinalIgnoreCase.Compare(left.FullName, right.FullName);
        }

        private static int GetLastNumber(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }
            MatchCollection matches = Regex.Matches(text, "[0-9]+");
            if (matches.Count == 0)
            {
                return 0;
            }
            int value;
            if (int.TryParse(matches[matches.Count - 1].Value, out value))
            {
                return value;
            }
            return 0;
        }

        private static void AppendLineIfNeeded(StringBuilder builder)
        {
            if (builder.Length == 0)
            {
                return;
            }
            if (builder[builder.Length - 1] != '\n')
            {
                builder.AppendLine();
            }
        }

        private static string TrimEndLines(string text)
        {
            if (text == null)
            {
                return string.Empty;
            }
            return text.TrimEnd('\r', '\n', '\t', ' ');
        }
    }
}
