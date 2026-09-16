using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace KnowledgeStudio
{
    // Text out of a PDF without Acrobat, without Word, and without a native
    // library to ship.
    //
    // This is deliberately a reader and not a renderer: it finds the page
    // content streams, decompresses them, runs the text-showing operators and
    // maps the character codes back through each font's ToUnicode table. It
    // gets ordinary text documents right and says so honestly when it cannot -
    // and because every PDF is also turned into page images for the AI, an
    // imperfect text layer never becomes a silent loss.
    public static class PdfText
    {
        private const int MaxObjects = 200000;
        private const long MaxFileBytes = 512L * 1024L * 1024L;

        private sealed class PdfObject
        {
            public int Number;
            public string Dictionary = string.Empty;
            public byte[] StreamData;
        }

        private sealed class PdfFont
        {
            public bool TwoByte;
            public Dictionary<int, string> ToUnicode = new Dictionary<int, string>();
        }

        public static ExtractResult Read(string path)
        {
            FileInfo info = new FileInfo(path);
            if (info.Length > MaxFileBytes)
            {
                return ExtractResult.Failure(
                    "pdf-builtin",
                    "PDF が大きすぎます（512 MiB まで）。", ExtractFailureReason.TooLarge);
            }

            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length < 5 ||
                bytes[0] != '%' || bytes[1] != 'P' || bytes[2] != 'D' || bytes[3] != 'F')
            {
                return ExtractResult.Failure("pdf-builtin", "PDF として読めません。");
            }

            string latin = Latin1(bytes);
            Dictionary<int, PdfObject> objects = ScanObjects(bytes, latin);
            ExpandObjectStreams(objects);

            if (IsEncrypted(objects, latin))
            {
                return ExtractResult.Failure(
                    "pdf-builtin",
                    "暗号化された PDF はテキスト抽出に対応していません。");
            }

            List<PdfObject> pages = OrderPages(objects);
            if (pages.Count == 0)
            {
                return ExtractResult.Failure(
                    "pdf-builtin",
                    "ページが見つかりませんでした。");
            }

            StringBuilder builder = new StringBuilder();
            int pageNumber = 0;
            int emptyPages = 0;

            foreach (PdfObject page in pages)
            {
                pageNumber++;
                Dictionary<string, PdfFont> fonts = LoadFonts(objects, page);
                byte[] content = LoadPageContent(objects, page);

                string text = content == null ?
                    string.Empty :
                    RenderTextOperators(Latin1(content), fonts);

                if (builder.Length > 0)
                {
                    builder.AppendLine();
                }
                builder.AppendLine("## ページ " +
                    pageNumber.ToString(CultureInfo.InvariantCulture));
                if (string.IsNullOrWhiteSpace(text))
                {
                    emptyPages++;
                    builder.AppendLine("(このページからテキストは取れませんでした)");
                }
                else
                {
                    builder.AppendLine(text.TrimEnd());
                }
            }

            ExtractResult result = new ExtractResult();
            result.Content = builder.ToString().TrimEnd('\r', '\n', ' ', '\t');
            result.Method = "pdf-builtin";
            result.Succeeded = emptyPages < pageNumber;
            result.Notes = "ページ: " + pageNumber.ToString(CultureInfo.InvariantCulture);
            if (emptyPages > 0)
            {
                result.Notes += "; テキストを取れなかったページ: " +
                    emptyPages.ToString(CultureInfo.InvariantCulture) +
                    "（ページ画像で補います）";
            }
            return result;
        }

        // Latin-1 keeps one byte as one char, so a regex can walk the file
        // without a decoder inventing or dropping anything.
        private static string Latin1(byte[] bytes)
        {
            return Encoding.GetEncoding(28591).GetString(bytes);
        }

        private static byte[] Latin1Bytes(string text)
        {
            return Encoding.GetEncoding(28591).GetBytes(text);
        }

        // The cross-reference table is skipped on purpose. A file that has
        // been appended to, or repaired, or written by a generator with a
        // slightly wrong xref still has its objects where they say they are,
        // and scanning for them reads all of those.
        private static Dictionary<int, PdfObject> ScanObjects(byte[] bytes, string latin)
        {
            Dictionary<int, PdfObject> objects = new Dictionary<int, PdfObject>();
            Regex objectPattern = new Regex(
                @"(?<![0-9])(\d{1,9})\s+(\d{1,5})\s+obj\b",
                RegexOptions.CultureInvariant);

            MatchCollection matches = objectPattern.Matches(latin);
            int index;
            for (index = 0; index < matches.Count && objects.Count < MaxObjects; index++)
            {
                Match match = matches[index];
                int number;
                if (!int.TryParse(match.Groups[1].Value, out number))
                {
                    continue;
                }

                int bodyStart = match.Index + match.Length;
                int bodyEnd = latin.IndexOf("endobj", bodyStart, StringComparison.Ordinal);
                if (bodyEnd < 0)
                {
                    bodyEnd = index + 1 < matches.Count ?
                        matches[index + 1].Index :
                        latin.Length;
                }

                PdfObject item = new PdfObject();
                item.Number = number;

                int streamStart = latin.IndexOf("stream", bodyStart, StringComparison.Ordinal);
                if (streamStart >= 0 && streamStart < bodyEnd)
                {
                    item.Dictionary = latin.Substring(bodyStart, streamStart - bodyStart);
                    int dataStart = streamStart + "stream".Length;
                    if (dataStart < latin.Length && latin[dataStart] == '\r')
                    {
                        dataStart++;
                    }
                    if (dataStart < latin.Length && latin[dataStart] == '\n')
                    {
                        dataStart++;
                    }

                    int dataEnd = FindStreamEnd(latin, item.Dictionary, dataStart, bodyEnd);
                    if (dataEnd > dataStart && dataEnd <= bytes.Length)
                    {
                        byte[] raw = new byte[dataEnd - dataStart];
                        Array.Copy(bytes, dataStart, raw, 0, raw.Length);
                        item.StreamData = raw;
                    }
                }
                else
                {
                    item.Dictionary = latin.Substring(bodyStart, Math.Max(0, bodyEnd - bodyStart));
                }

                objects[number] = item;
            }

            return objects;
        }

        private static int FindStreamEnd(
            string latin,
            string dictionary,
            int dataStart,
            int bodyEnd)
        {
            // A direct /Length is the fast and correct answer. It is still
            // checked against the endstream marker, because a wrong length is
            // exactly the kind of damage this reader is meant to survive.
            Match lengthMatch = Regex.Match(
                dictionary, @"/Length\s+(\d+)(?!\s+\d+\s+R)", RegexOptions.CultureInvariant);
            if (lengthMatch.Success)
            {
                int length;
                if (int.TryParse(lengthMatch.Groups[1].Value, out length) &&
                    length >= 0 &&
                    dataStart + length <= latin.Length)
                {
                    int after = dataStart + length;
                    int probe = after;
                    while (probe < latin.Length && probe < after + 4 &&
                        (latin[probe] == '\r' || latin[probe] == '\n' ||
                         latin[probe] == ' ' || latin[probe] == '\t'))
                    {
                        probe++;
                    }
                    if (probe + 9 <= latin.Length &&
                        string.CompareOrdinal(latin, probe, "endstream", 0, 9) == 0)
                    {
                        return after;
                    }
                }
            }

            int marker = latin.IndexOf("endstream", dataStart, StringComparison.Ordinal);
            if (marker < 0)
            {
                return Math.Min(bodyEnd, latin.Length);
            }
            int end = marker;
            while (end > dataStart && (latin[end - 1] == '\n' || latin[end - 1] == '\r'))
            {
                end--;
            }
            return end;
        }

        // PDF 1.5 and later pack most dictionaries into compressed object
        // streams, and Word writes those. Without unpacking them the page tree
        // and the fonts simply are not there.
        private static void ExpandObjectStreams(Dictionary<int, PdfObject> objects)
        {
            List<PdfObject> streams = new List<PdfObject>();
            foreach (KeyValuePair<int, PdfObject> pair in objects)
            {
                if (pair.Value.StreamData != null &&
                    Regex.IsMatch(pair.Value.Dictionary, @"/Type\s*/ObjStm\b"))
                {
                    streams.Add(pair.Value);
                }
            }

            foreach (PdfObject container in streams)
            {
                byte[] decoded = Decode(container);
                if (decoded == null)
                {
                    continue;
                }

                int count = ReadIntKey(container.Dictionary, "N");
                int first = ReadIntKey(container.Dictionary, "First");
                if (count <= 0 || first <= 0 || first > decoded.Length)
                {
                    continue;
                }

                string body = Latin1(decoded);
                string header = body.Substring(0, first);
                MatchCollection pairs = Regex.Matches(
                    header, @"(\d+)\s+(\d+)", RegexOptions.CultureInvariant);

                int index;
                for (index = 0; index < pairs.Count && index < count; index++)
                {
                    int number;
                    int offset;
                    if (!int.TryParse(pairs[index].Groups[1].Value, out number) ||
                        !int.TryParse(pairs[index].Groups[2].Value, out offset))
                    {
                        continue;
                    }
                    if (objects.ContainsKey(number))
                    {
                        continue;
                    }

                    int start = first + offset;
                    if (start < 0 || start >= body.Length)
                    {
                        continue;
                    }
                    int end = body.Length;
                    if (index + 1 < pairs.Count && index + 1 < count)
                    {
                        int nextOffset;
                        if (int.TryParse(pairs[index + 1].Groups[2].Value, out nextOffset))
                        {
                            int candidate = first + nextOffset;
                            if (candidate > start && candidate <= body.Length)
                            {
                                end = candidate;
                            }
                        }
                    }

                    PdfObject item = new PdfObject();
                    item.Number = number;
                    item.Dictionary = body.Substring(start, end - start);
                    objects[number] = item;
                }
            }
        }

        private static bool IsEncrypted(Dictionary<int, PdfObject> objects, string latin)
        {
            if (Regex.IsMatch(latin, @"trailer[\s\S]{0,2000}?/Encrypt\b"))
            {
                return true;
            }
            foreach (KeyValuePair<int, PdfObject> pair in objects)
            {
                if (Regex.IsMatch(pair.Value.Dictionary, @"/Type\s*/XRef\b") &&
                    Regex.IsMatch(pair.Value.Dictionary, @"/Encrypt\b"))
                {
                    return true;
                }
            }
            return false;
        }

        private static List<PdfObject> OrderPages(Dictionary<int, PdfObject> objects)
        {
            List<PdfObject> ordered = new List<PdfObject>();
            HashSet<int> seen = new HashSet<int>();

            // The page tree gives the reading order. When it is missing or
            // broken, object order is a poor but predictable substitute, and
            // the page headings still say which page each block came from.
            foreach (KeyValuePair<int, PdfObject> pair in objects)
            {
                if (Regex.IsMatch(pair.Value.Dictionary, @"/Type\s*/Catalog\b"))
                {
                    int rootPages = ReadReference(pair.Value.Dictionary, "Pages");
                    if (rootPages > 0)
                    {
                        CollectPages(objects, rootPages, ordered, seen, 0);
                    }
                    break;
                }
            }

            if (ordered.Count == 0)
            {
                List<int> numbers = new List<int>(objects.Keys);
                numbers.Sort();
                foreach (int number in numbers)
                {
                    PdfObject item = objects[number];
                    if (Regex.IsMatch(item.Dictionary, @"/Type\s*/Page\b(?!s)"))
                    {
                        ordered.Add(item);
                    }
                }
            }

            return ordered;
        }

        private static void CollectPages(
            Dictionary<int, PdfObject> objects,
            int number,
            List<PdfObject> ordered,
            HashSet<int> seen,
            int depth)
        {
            if (depth > 64 || !seen.Add(number))
            {
                return;
            }

            PdfObject node;
            if (!objects.TryGetValue(number, out node))
            {
                return;
            }

            if (Regex.IsMatch(node.Dictionary, @"/Type\s*/Page\b(?!s)"))
            {
                ordered.Add(node);
                return;
            }

            Match kids = Regex.Match(node.Dictionary, @"/Kids\s*\[([^\]]*)\]");
            if (!kids.Success)
            {
                return;
            }

            MatchCollection references = Regex.Matches(
                kids.Groups[1].Value, @"(\d+)\s+\d+\s+R");
            foreach (Match reference in references)
            {
                int child;
                if (int.TryParse(reference.Groups[1].Value, out child))
                {
                    CollectPages(objects, child, ordered, seen, depth + 1);
                }
            }
        }

        private static byte[] LoadPageContent(
            Dictionary<int, PdfObject> objects,
            PdfObject page)
        {
            List<int> contentNumbers = new List<int>();

            Match single = Regex.Match(page.Dictionary, @"/Contents\s+(\d+)\s+\d+\s+R");
            if (single.Success)
            {
                int number;
                if (int.TryParse(single.Groups[1].Value, out number))
                {
                    contentNumbers.Add(number);
                }
            }
            else
            {
                Match array = Regex.Match(page.Dictionary, @"/Contents\s*\[([^\]]*)\]");
                if (array.Success)
                {
                    MatchCollection references = Regex.Matches(
                        array.Groups[1].Value, @"(\d+)\s+\d+\s+R");
                    foreach (Match reference in references)
                    {
                        int number;
                        if (int.TryParse(reference.Groups[1].Value, out number))
                        {
                            contentNumbers.Add(number);
                        }
                    }
                }
            }

            if (contentNumbers.Count == 0)
            {
                return null;
            }

            using (MemoryStream combined = new MemoryStream())
            {
                foreach (int number in contentNumbers)
                {
                    PdfObject item;
                    if (!objects.TryGetValue(number, out item) || item.StreamData == null)
                    {
                        continue;
                    }
                    byte[] decoded = Decode(item);
                    if (decoded == null)
                    {
                        continue;
                    }
                    combined.Write(decoded, 0, decoded.Length);
                    combined.WriteByte((byte)'\n');
                }
                return combined.ToArray();
            }
        }

        private static Dictionary<string, PdfFont> LoadFonts(
            Dictionary<int, PdfObject> objects,
            PdfObject page)
        {
            Dictionary<string, PdfFont> fonts = new Dictionary<string, PdfFont>(
                StringComparer.Ordinal);

            string resources = ReadResources(objects, page);
            if (resources.Length == 0)
            {
                return fonts;
            }

            string fontDictionary = ReadSubDictionary(objects, resources, "Font");
            if (fontDictionary.Length == 0)
            {
                return fonts;
            }

            MatchCollection entries = Regex.Matches(
                fontDictionary, @"/([^\s/<>\[\]()]+)\s+(\d+)\s+\d+\s+R");
            foreach (Match entry in entries)
            {
                int number;
                if (!int.TryParse(entry.Groups[2].Value, out number))
                {
                    continue;
                }
                PdfObject fontObject;
                if (!objects.TryGetValue(number, out fontObject))
                {
                    continue;
                }

                PdfFont font = new PdfFont();
                font.TwoByte =
                    Regex.IsMatch(fontObject.Dictionary, @"/Subtype\s*/Type0\b") ||
                    Regex.IsMatch(fontObject.Dictionary, @"/Encoding\s*/Identity-[HV]\b");

                int toUnicode = ReadReference(fontObject.Dictionary, "ToUnicode");
                if (toUnicode > 0)
                {
                    PdfObject cmapObject;
                    if (objects.TryGetValue(toUnicode, out cmapObject) &&
                        cmapObject.StreamData != null)
                    {
                        byte[] decoded = Decode(cmapObject);
                        if (decoded != null)
                        {
                            ParseToUnicode(Latin1(decoded), font);
                        }
                    }
                }

                fonts["/" + entry.Groups[1].Value] = font;
            }

            return fonts;
        }

        private static string ReadResources(
            Dictionary<int, PdfObject> objects,
            PdfObject page)
        {
            int reference = ReadReference(page.Dictionary, "Resources");
            if (reference > 0)
            {
                PdfObject item;
                if (objects.TryGetValue(reference, out item))
                {
                    return item.Dictionary;
                }
                return string.Empty;
            }
            return ReadSubDictionary(objects, page.Dictionary, "Resources");
        }

        // Reads `/Key << ... >>` with nesting, or follows `/Key n 0 R`.
        private static string ReadSubDictionary(
            Dictionary<int, PdfObject> objects,
            string dictionary,
            string key)
        {
            int reference = ReadReference(dictionary, key);
            if (reference > 0)
            {
                PdfObject item;
                if (objects.TryGetValue(reference, out item))
                {
                    return item.Dictionary;
                }
                return string.Empty;
            }

            Match start = Regex.Match(dictionary, @"/" + key + @"\s*<<");
            if (!start.Success)
            {
                return string.Empty;
            }

            int position = start.Index + start.Length;
            int depth = 1;
            int begin = position;
            while (position < dictionary.Length - 1 && depth > 0)
            {
                if (dictionary[position] == '<' && dictionary[position + 1] == '<')
                {
                    depth++;
                    position += 2;
                    continue;
                }
                if (dictionary[position] == '>' && dictionary[position + 1] == '>')
                {
                    depth--;
                    position += 2;
                    continue;
                }
                position++;
            }
            int length = Math.Max(0, position - begin - 2);
            return dictionary.Substring(begin, Math.Min(length, dictionary.Length - begin));
        }

        private static int ReadReference(string dictionary, string key)
        {
            Match match = Regex.Match(
                dictionary, @"/" + key + @"\s+(\d+)\s+\d+\s+R\b");
            if (!match.Success)
            {
                return 0;
            }
            int number;
            return int.TryParse(match.Groups[1].Value, out number) ? number : 0;
        }

        private static int ReadIntKey(string dictionary, string key)
        {
            Match match = Regex.Match(dictionary, @"/" + key + @"\s+(\d+)\b");
            if (!match.Success)
            {
                return 0;
            }
            int value;
            return int.TryParse(match.Groups[1].Value, out value) ? value : 0;
        }

        private static void ParseToUnicode(string cmap, PdfFont font)
        {
            MatchCollection charBlocks = Regex.Matches(
                cmap, @"beginbfchar([\s\S]*?)endbfchar", RegexOptions.CultureInvariant);
            foreach (Match block in charBlocks)
            {
                MatchCollection pairs = Regex.Matches(
                    block.Groups[1].Value, @"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]*)>");
                foreach (Match pair in pairs)
                {
                    int code = ParseHexInt(pair.Groups[1].Value);
                    if (code >= 0)
                    {
                        font.ToUnicode[code] = HexToUnicode(pair.Groups[2].Value);
                    }
                }
            }

            MatchCollection rangeBlocks = Regex.Matches(
                cmap, @"beginbfrange([\s\S]*?)endbfrange", RegexOptions.CultureInvariant);
            foreach (Match block in rangeBlocks)
            {
                string body = block.Groups[1].Value;

                MatchCollection simple = Regex.Matches(
                    body, @"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]*)>");
                foreach (Match range in simple)
                {
                    int low = ParseHexInt(range.Groups[1].Value);
                    int high = ParseHexInt(range.Groups[2].Value);
                    string startValue = range.Groups[3].Value;
                    if (low < 0 || high < low || high - low > 65535)
                    {
                        continue;
                    }
                    int baseValue = ParseHexInt(startValue);
                    int code;
                    for (code = low; code <= high; code++)
                    {
                        if (startValue.Length > 4)
                        {
                            // Surrogate pairs and ligatures: only the first
                            // code of the range can be trusted verbatim.
                            font.ToUnicode[code] = code == low ?
                                HexToUnicode(startValue) :
                                string.Empty;
                            continue;
                        }
                        int value = baseValue + (code - low);
                        if (value >= 0 && value <= 0xFFFF)
                        {
                            font.ToUnicode[code] = ((char)value).ToString();
                        }
                    }
                }

                MatchCollection arrays = Regex.Matches(
                    body, @"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>\s*\[([^\]]*)\]");
                foreach (Match range in arrays)
                {
                    int low = ParseHexInt(range.Groups[1].Value);
                    if (low < 0)
                    {
                        continue;
                    }
                    MatchCollection values = Regex.Matches(
                        range.Groups[3].Value, @"<([0-9A-Fa-f]*)>");
                    int index;
                    for (index = 0; index < values.Count; index++)
                    {
                        font.ToUnicode[low + index] =
                            HexToUnicode(values[index].Groups[1].Value);
                    }
                }
            }
        }

        private static int ParseHexInt(string hex)
        {
            if (string.IsNullOrEmpty(hex) || hex.Length > 8)
            {
                return -1;
            }
            int value;
            if (!int.TryParse(
                hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
            {
                return -1;
            }
            return value;
        }

        private static string HexToUnicode(string hex)
        {
            if (string.IsNullOrEmpty(hex))
            {
                return string.Empty;
            }
            StringBuilder builder = new StringBuilder();
            int index;
            for (index = 0; index + 3 < hex.Length + 3 && index < hex.Length; index += 4)
            {
                string piece = index + 4 <= hex.Length ?
                    hex.Substring(index, 4) :
                    hex.Substring(index).PadRight(4, '0');
                int value = ParseHexInt(piece);
                if (value >= 0)
                {
                    builder.Append((char)value);
                }
            }
            return builder.ToString();
        }

        private static byte[] Decode(PdfObject item)
        {
            if (item.StreamData == null)
            {
                return null;
            }

            string filters = string.Empty;
            Match filterMatch = Regex.Match(item.Dictionary, @"/Filter\s*(/\w+|\[[^\]]*\])");
            if (filterMatch.Success)
            {
                filters = filterMatch.Groups[1].Value;
            }

            byte[] data = item.StreamData;

            if (filters.Length == 0)
            {
                return data;
            }
            if (filters.IndexOf("ASCIIHexDecode", StringComparison.Ordinal) >= 0)
            {
                data = AsciiHexDecode(data);
            }
            if (filters.IndexOf("ASCII85Decode", StringComparison.Ordinal) >= 0)
            {
                data = Ascii85Decode(data);
            }
            if (filters.IndexOf("FlateDecode", StringComparison.Ordinal) >= 0)
            {
                data = FlateDecode(data);
                if (data == null)
                {
                    return null;
                }
                data = ApplyPredictor(item.Dictionary, data);
            }
            else if (filters.IndexOf("LZWDecode", StringComparison.Ordinal) >= 0 ||
                filters.IndexOf("RunLengthDecode", StringComparison.Ordinal) >= 0 ||
                filters.IndexOf("DCTDecode", StringComparison.Ordinal) >= 0 ||
                filters.IndexOf("JPXDecode", StringComparison.Ordinal) >= 0 ||
                filters.IndexOf("CCITTFaxDecode", StringComparison.Ordinal) >= 0)
            {
                // Not text, or a compression this reader does not implement.
                // Returning nothing is honest; the page images still carry it.
                return null;
            }

            return data;
        }

        private static byte[] FlateDecode(byte[] data)
        {
            int offset = 0;
            // Skip the two-byte zlib header when it is there; some writers
            // emit raw deflate.
            if (data.Length >= 2 && (data[0] & 0x0F) == 8 &&
                ((data[0] << 8) | data[1]) % 31 == 0)
            {
                offset = 2;
            }

            byte[] attempt = TryInflate(data, offset);
            if (attempt != null)
            {
                return attempt;
            }
            if (offset != 0)
            {
                return TryInflate(data, 0);
            }
            return TryInflate(data, 2);
        }

        private static byte[] TryInflate(byte[] data, int offset)
        {
            if (offset < 0 || offset >= data.Length)
            {
                return null;
            }
            try
            {
                using (MemoryStream input = new MemoryStream(data, offset, data.Length - offset))
                using (DeflateStream inflate = new DeflateStream(
                    input, CompressionMode.Decompress))
                using (MemoryStream output = new MemoryStream())
                {
                    byte[] buffer = new byte[16384];
                    int read;
                    long total = 0;
                    while ((read = inflate.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        total += read;
                        if (total > 512L * 1024L * 1024L)
                        {
                            return null;
                        }
                        output.Write(buffer, 0, read);
                    }
                    return output.ToArray();
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static byte[] ApplyPredictor(string dictionary, byte[] data)
        {
            Match parms = Regex.Match(dictionary, @"/DecodeParms\s*<<([\s\S]*?)>>");
            if (!parms.Success)
            {
                return data;
            }

            int predictor = ReadIntKey(parms.Groups[1].Value, "Predictor");
            if (predictor < 10)
            {
                return data;
            }

            int columns = ReadIntKey(parms.Groups[1].Value, "Columns");
            if (columns <= 0)
            {
                columns = 1;
            }
            int colors = ReadIntKey(parms.Groups[1].Value, "Colors");
            if (colors <= 0)
            {
                colors = 1;
            }
            int bits = ReadIntKey(parms.Groups[1].Value, "BitsPerComponent");
            if (bits <= 0)
            {
                bits = 8;
            }

            int bytesPerPixel = Math.Max(1, colors * bits / 8);
            int rowLength = columns * colors * bits / 8;
            if (rowLength <= 0)
            {
                return data;
            }

            List<byte> output = new List<byte>(data.Length);
            byte[] previous = new byte[rowLength];
            int position = 0;
            while (position + 1 + rowLength <= data.Length)
            {
                int tag = data[position];
                position++;
                byte[] row = new byte[rowLength];
                Array.Copy(data, position, row, 0, rowLength);
                position += rowLength;

                int index;
                for (index = 0; index < rowLength; index++)
                {
                    int left = index >= bytesPerPixel ? row[index - bytesPerPixel] : 0;
                    int up = previous[index];
                    int upLeft = index >= bytesPerPixel ? previous[index - bytesPerPixel] : 0;
                    int value = row[index];

                    switch (tag)
                    {
                        case 1: value += left; break;
                        case 2: value += up; break;
                        case 3: value += (left + up) / 2; break;
                        case 4: value += Paeth(left, up, upLeft); break;
                    }
                    row[index] = (byte)(value & 0xFF);
                }

                output.AddRange(row);
                previous = row;
            }

            return output.Count > 0 ? output.ToArray() : data;
        }

        private static int Paeth(int a, int b, int c)
        {
            int p = a + b - c;
            int pa = Math.Abs(p - a);
            int pb = Math.Abs(p - b);
            int pc = Math.Abs(p - c);
            if (pa <= pb && pa <= pc)
            {
                return a;
            }
            return pb <= pc ? b : c;
        }

        private static byte[] AsciiHexDecode(byte[] data)
        {
            List<byte> output = new List<byte>();
            int high = -1;
            int index;
            for (index = 0; index < data.Length; index++)
            {
                char ch = (char)data[index];
                if (ch == '>')
                {
                    break;
                }
                int value = HexDigit(ch);
                if (value < 0)
                {
                    continue;
                }
                if (high < 0)
                {
                    high = value;
                }
                else
                {
                    output.Add((byte)((high << 4) | value));
                    high = -1;
                }
            }
            if (high >= 0)
            {
                output.Add((byte)(high << 4));
            }
            return output.ToArray();
        }

        private static int HexDigit(char ch)
        {
            if (ch >= '0' && ch <= '9') { return ch - '0'; }
            if (ch >= 'a' && ch <= 'f') { return ch - 'a' + 10; }
            if (ch >= 'A' && ch <= 'F') { return ch - 'A' + 10; }
            return -1;
        }

        private static byte[] Ascii85Decode(byte[] data)
        {
            List<byte> output = new List<byte>();
            uint tuple = 0;
            int count = 0;
            int index = 0;

            if (data.Length >= 2 && data[0] == '<' && data[1] == '~')
            {
                index = 2;
            }

            for (; index < data.Length; index++)
            {
                char ch = (char)data[index];
                if (ch == '~')
                {
                    break;
                }
                if (ch == 'z' && count == 0)
                {
                    output.Add(0); output.Add(0); output.Add(0); output.Add(0);
                    continue;
                }
                if (ch < '!' || ch > 'u')
                {
                    continue;
                }

                tuple = tuple * 85 + (uint)(ch - '!');
                count++;
                if (count == 5)
                {
                    output.Add((byte)(tuple >> 24));
                    output.Add((byte)(tuple >> 16));
                    output.Add((byte)(tuple >> 8));
                    output.Add((byte)tuple);
                    tuple = 0;
                    count = 0;
                }
            }

            if (count > 1)
            {
                int missing;
                for (missing = count; missing < 5; missing++)
                {
                    tuple = tuple * 85 + 84;
                }
                int emit;
                for (emit = 0; emit < count - 1; emit++)
                {
                    output.Add((byte)(tuple >> (24 - emit * 8)));
                }
            }

            return output.ToArray();
        }

        // The content stream, run for its text-showing operators only. Nothing
        // is painted: positioning is used just to decide where a line ends.
        private static string RenderTextOperators(
            string content,
            Dictionary<string, PdfFont> fonts)
        {
            StringBuilder output = new StringBuilder();
            StringBuilder line = new StringBuilder();
            List<object> operands = new List<object>();
            PdfFont currentFont = null;
            double lastY = double.NaN;
            int position = 0;

            while (position < content.Length)
            {
                char ch = content[position];

                if (ch == '%')
                {
                    while (position < content.Length &&
                        content[position] != '\n' && content[position] != '\r')
                    {
                        position++;
                    }
                    continue;
                }
                if (ch == ' ' || ch == '\r' || ch == '\n' || ch == '\t' || ch == '\0')
                {
                    position++;
                    continue;
                }
                if (ch == '(')
                {
                    string value = ReadLiteralString(content, ref position);
                    operands.Add(new PdfString(value));
                    continue;
                }
                if (ch == '<' && position + 1 < content.Length && content[position + 1] != '<')
                {
                    string value = ReadHexString(content, ref position);
                    operands.Add(new PdfString(value));
                    continue;
                }
                if (ch == '<' || ch == '>')
                {
                    position += 2;
                    continue;
                }
                if (ch == '[')
                {
                    List<object> array = new List<object>();
                    position++;
                    while (position < content.Length && content[position] != ']')
                    {
                        char inner = content[position];
                        if (inner == '(')
                        {
                            array.Add(new PdfString(ReadLiteralString(content, ref position)));
                            continue;
                        }
                        if (inner == '<')
                        {
                            array.Add(new PdfString(ReadHexString(content, ref position)));
                            continue;
                        }
                        if (inner == ' ' || inner == '\r' || inner == '\n' || inner == '\t')
                        {
                            position++;
                            continue;
                        }
                        int numberStart = position;
                        while (position < content.Length &&
                            content[position] != ']' &&
                            content[position] != '(' &&
                            content[position] != '<' &&
                            content[position] != ' ' &&
                            content[position] != '\r' &&
                            content[position] != '\n' &&
                            content[position] != '\t')
                        {
                            position++;
                        }
                        double parsed;
                        string token = content.Substring(numberStart, position - numberStart);
                        if (double.TryParse(
                            token,
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out parsed))
                        {
                            array.Add(parsed);
                        }
                    }
                    position++;
                    operands.Add(array);
                    continue;
                }
                if (ch == '/')
                {
                    int nameStart = position;
                    position++;
                    while (position < content.Length && !IsDelimiter(content[position]))
                    {
                        position++;
                    }
                    operands.Add(content.Substring(nameStart, position - nameStart));
                    continue;
                }
                if ((ch >= '0' && ch <= '9') || ch == '-' || ch == '+' || ch == '.')
                {
                    int numberStart = position;
                    while (position < content.Length && !IsDelimiter(content[position]))
                    {
                        position++;
                    }
                    double parsed;
                    string token = content.Substring(numberStart, position - numberStart);
                    if (double.TryParse(
                        token, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                    {
                        operands.Add(parsed);
                    }
                    continue;
                }

                int operatorStart = position;
                while (position < content.Length && !IsDelimiter(content[position]))
                {
                    position++;
                }
                if (position == operatorStart)
                {
                    position++;
                    continue;
                }
                string op = content.Substring(operatorStart, position - operatorStart);

                switch (op)
                {
                    case "BT":
                        lastY = double.NaN;
                        break;
                    case "ET":
                        FlushLine(output, line);
                        break;
                    case "Tf":
                        if (operands.Count >= 2)
                        {
                            string name = operands[operands.Count - 2] as string;
                            PdfFont found;
                            currentFont = name != null && fonts.TryGetValue(name, out found) ?
                                found :
                                null;
                        }
                        break;
                    case "Td":
                    case "TD":
                        if (operands.Count >= 2 && operands[operands.Count - 1] is double)
                        {
                            double ty = (double)operands[operands.Count - 1];
                            if (Math.Abs(ty) > 0.01)
                            {
                                FlushLine(output, line);
                            }
                        }
                        break;
                    case "T*":
                        FlushLine(output, line);
                        break;
                    case "Tm":
                        if (operands.Count >= 6 && operands[operands.Count - 1] is double)
                        {
                            double y = (double)operands[operands.Count - 1];
                            if (!double.IsNaN(lastY) && Math.Abs(y - lastY) > 0.01)
                            {
                                FlushLine(output, line);
                            }
                            lastY = y;
                        }
                        break;
                    case "Tj":
                    case "'":
                    case "\"":
                        if (op != "Tj")
                        {
                            FlushLine(output, line);
                        }
                        if (operands.Count >= 1)
                        {
                            PdfString text = operands[operands.Count - 1] as PdfString;
                            if (text != null)
                            {
                                line.Append(DecodeShown(text.Value, currentFont));
                            }
                        }
                        break;
                    case "TJ":
                        if (operands.Count >= 1)
                        {
                            List<object> array = operands[operands.Count - 1] as List<object>;
                            if (array != null)
                            {
                                foreach (object element in array)
                                {
                                    PdfString piece = element as PdfString;
                                    if (piece != null)
                                    {
                                        line.Append(DecodeShown(piece.Value, currentFont));
                                        continue;
                                    }
                                    if (element is double && (double)element < -180)
                                    {
                                        // A wide negative adjustment is how a
                                        // word gap is written when there is no
                                        // space glyph.
                                        if (line.Length > 0 &&
                                            line[line.Length - 1] != ' ')
                                        {
                                            line.Append(' ');
                                        }
                                    }
                                }
                            }
                        }
                        break;
                }

                operands.Clear();
            }

            FlushLine(output, line);
            return output.ToString();
        }

        private sealed class PdfString
        {
            public string Value;
            public PdfString(string value) { Value = value; }
        }

        private static void FlushLine(StringBuilder output, StringBuilder line)
        {
            string text = line.ToString();
            line.Length = 0;
            if (text.Trim().Length == 0)
            {
                return;
            }
            output.Append(text.TrimEnd());
            output.Append('\n');
        }

        private static bool IsDelimiter(char ch)
        {
            return ch == ' ' || ch == '\r' || ch == '\n' || ch == '\t' || ch == '\0' ||
                ch == '/' || ch == '[' || ch == ']' || ch == '(' || ch == ')' ||
                ch == '<' || ch == '>' || ch == '{' || ch == '}' || ch == '%';
        }

        private static string ReadLiteralString(string content, ref int position)
        {
            StringBuilder builder = new StringBuilder();
            position++;
            int depth = 1;

            while (position < content.Length && depth > 0)
            {
                char ch = content[position];
                if (ch == '\\')
                {
                    position++;
                    if (position >= content.Length)
                    {
                        break;
                    }
                    char escaped = content[position];
                    switch (escaped)
                    {
                        case 'n': builder.Append('\n'); position++; break;
                        case 'r': builder.Append('\r'); position++; break;
                        case 't': builder.Append('\t'); position++; break;
                        case 'b': builder.Append('\b'); position++; break;
                        case 'f': builder.Append('\f'); position++; break;
                        case '(': builder.Append('('); position++; break;
                        case ')': builder.Append(')'); position++; break;
                        case '\\': builder.Append('\\'); position++; break;
                        case '\r':
                            position++;
                            if (position < content.Length && content[position] == '\n')
                            {
                                position++;
                            }
                            break;
                        case '\n': position++; break;
                        default:
                            if (escaped >= '0' && escaped <= '7')
                            {
                                int value = 0;
                                int digits = 0;
                                while (position < content.Length && digits < 3 &&
                                    content[position] >= '0' && content[position] <= '7')
                                {
                                    value = value * 8 + (content[position] - '0');
                                    position++;
                                    digits++;
                                }
                                builder.Append((char)(value & 0xFF));
                            }
                            else
                            {
                                builder.Append(escaped);
                                position++;
                            }
                            break;
                    }
                    continue;
                }
                if (ch == '(')
                {
                    depth++;
                    builder.Append(ch);
                    position++;
                    continue;
                }
                if (ch == ')')
                {
                    depth--;
                    position++;
                    if (depth > 0)
                    {
                        builder.Append(ch);
                    }
                    continue;
                }
                builder.Append(ch);
                position++;
            }

            return builder.ToString();
        }

        private static string ReadHexString(string content, ref int position)
        {
            StringBuilder builder = new StringBuilder();
            position++;
            int high = -1;

            while (position < content.Length && content[position] != '>')
            {
                int value = HexDigit(content[position]);
                position++;
                if (value < 0)
                {
                    continue;
                }
                if (high < 0)
                {
                    high = value;
                }
                else
                {
                    builder.Append((char)((high << 4) | value));
                    high = -1;
                }
            }
            if (high >= 0)
            {
                builder.Append((char)(high << 4));
            }
            if (position < content.Length)
            {
                position++;
            }

            return builder.ToString();
        }

        private static string DecodeShown(string raw, PdfFont font)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return string.Empty;
            }

            StringBuilder builder = new StringBuilder();

            if (font != null && font.TwoByte)
            {
                int index;
                for (index = 0; index + 1 < raw.Length; index += 2)
                {
                    int code = ((raw[index] & 0xFF) << 8) | (raw[index + 1] & 0xFF);
                    string mapped;
                    if (font.ToUnicode.TryGetValue(code, out mapped))
                    {
                        builder.Append(mapped);
                    }
                    else if (code >= 32 && code <= 0xFFFF)
                    {
                        builder.Append((char)code);
                    }
                }
                return builder.ToString();
            }

            int position;
            for (position = 0; position < raw.Length; position++)
            {
                int code = raw[position] & 0xFF;
                string mapped;
                if (font != null && font.ToUnicode.TryGetValue(code, out mapped))
                {
                    builder.Append(mapped);
                    continue;
                }
                builder.Append((char)code);
            }
            return builder.ToString();
        }
    }
}
