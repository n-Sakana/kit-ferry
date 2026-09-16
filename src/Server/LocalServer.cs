using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Ferry
{
    internal sealed class LocalServer : IDisposable
    {
        private const long MaximumRequestBytes = 64 * 1024;

        private readonly SemaphoreSlim _requestSlots = new SemaphoreSlim(16, 16);
        private readonly SemaphoreSlim _operationGate = new SemaphoreSlim(1, 1);
        private readonly HttpListener _listener = new HttpListener();
        private readonly AppState _state;
        private readonly int _port;
        private readonly string _initialMode;
        private readonly string _outputRoot;
        private readonly Action _requestShutdown;
        private readonly OpticalService _optical = new OpticalService();
        private readonly OpticalReceiveService _opticalReceiver;
        private readonly RemoteControlRegistry _remotes = new RemoteControlRegistry();
        private readonly TailnetDiscovery _tailnet = new TailnetDiscovery();

        public LocalServer(
            int port,
            AppState state,
            string initialMode,
            string outputRoot,
            Action requestShutdown)
        {
            _port = port;
            _state = state;
            _initialMode = initialMode;
            _outputRoot = Path.GetFullPath(outputRoot);
            _opticalReceiver = new OpticalReceiveService(_outputRoot);
            _requestShutdown = requestShutdown;
        }

        public Uri Start()
        {
            _listener.IgnoreWriteExceptions = true;
            _listener.Prefixes.Add(string.Format("http://127.0.0.1:{0}/", _port));
            _listener.Prefixes.Add(string.Format("http://localhost:{0}/", _port));
            _listener.Start();
            return new Uri(string.Format("http://localhost:{0}/", _port));
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            using (cancellationToken.Register(delegate
            {
                try
                {
                    _listener.Close();
                }
                catch (ObjectDisposedException)
                {
                }
            }))
            {
                var pending = new List<Task>();
                while (!cancellationToken.IsCancellationRequested && _listener.IsListening)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync();
                    }
                    catch (HttpListenerException)
                    {
                        if (cancellationToken.IsCancellationRequested || !_listener.IsListening)
                        {
                            break;
                        }

                        throw;
                    }
                    catch (ObjectDisposedException)
                    {
                        if (cancellationToken.IsCancellationRequested || !_listener.IsListening)
                        {
                            break;
                        }

                        throw;
                    }

                    try
                    {
                        await _requestSlots.WaitAsync(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        context.Response.Abort();
                        break;
                    }
                    var accepted = context;
                    pending.RemoveAll(delegate(Task task) { return task.IsCompleted; });
                    pending.Add(Task.Run(async delegate
                    {
                        try { await HandleSafelyAsync(accepted); }
                        catch (Exception exception) { Console.Error.WriteLine(exception); }
                        finally { _requestSlots.Release(); }
                    }));
                }
                await Task.WhenAny(Task.WhenAll(pending), Task.Delay(2000));
            }
        }

        public void Dispose()
        {
            if (_listener.IsListening)
            {
                _listener.Stop();
            }

            _listener.Close();
        }

        private async Task HandleSafelyAsync(HttpListenerContext context)
        {
            AddSecurityHeaders(context.Response);
            ErrorBody error = null;
            var errorStatus = HttpStatusCode.InternalServerError;

            try
            {
                var path = context.Request.Url == null ? "/" : context.Request.Url.AbsolutePath;
                var exclusive = path == "/api/pick-folder" || path == "/api/pick-files"
                    || path == "/api/markdown" || path == "/api/vba/extract"
                    || path == "/api/vba/inspect" || path == "/api/optical/start";
                if (exclusive) await _operationGate.WaitAsync();
                try { await HandleAsync(context); }
                finally { if (exclusive) _operationGate.Release(); }
            }
            catch (DirectoryNotFoundException exception)
            {
                errorStatus = HttpStatusCode.BadRequest;
                error = new ErrorBody(exception.Message);
            }
            catch (FileNotFoundException exception)
            {
                errorStatus = HttpStatusCode.BadRequest;
                error = new ErrorBody(exception.Message);
            }
            catch (ArgumentException exception)
            {
                errorStatus = HttpStatusCode.BadRequest;
                error = new ErrorBody(exception.Message);
            }
            catch (InvalidDataException exception)
            {
                errorStatus = HttpStatusCode.BadRequest;
                error = new ErrorBody(exception.Message);
            }
            catch (UnauthorizedAccessException exception)
            {
                errorStatus = HttpStatusCode.Forbidden;
                error = new ErrorBody(string.Format(
                    "このフォルダは開けません: {0}",
                    exception.Message));
            }
            catch (IOException exception)
            {
                errorStatus = HttpStatusCode.BadRequest;
                error = new ErrorBody(string.Format(
                    "フォルダを読めません: {0}",
                    exception.Message));
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    "{0:O} {1} {2}: {3}",
                    DateTimeOffset.Now,
                    context.Request.HttpMethod,
                    context.Request.RawUrl,
                    exception);
                error = new ErrorBody("Ferry の処理中にエラーが起きました。");
            }

            if (error != null)
            {
                try
                {
                    if (context.Response.OutputStream.CanWrite)
                    {
                        await WriteJsonAsync(context, errorStatus, error);
                    }
                }
                catch (ObjectDisposedException)
                {
                    // The client disconnected while Ferry was reporting the error.
                }
                catch (HttpListenerException)
                {
                    // The client disconnected while a native picker was open.
                }
                catch (InvalidOperationException)
                {
                    // The response had already started before the disconnect.
                }
            }

            try
            {
                context.Response.OutputStream.Close();
            }
            catch (ObjectDisposedException)
            {
                // The client disconnected after the response was written.
            }
            catch (HttpListenerException)
            {
                // The client disconnected after the response was written.
            }
            catch (InvalidOperationException)
            {
                // The response was already closed.
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var path = request.Url == null ? "/" : request.Url.AbsolutePath;
            var role = GetRequestRole(request);

            if (request.HttpMethod == "GET" && path == "/api/status")
            {
                await WriteJsonAsync(context, HttpStatusCode.OK, BuildStatus(role));
                return;
            }

            if (request.HttpMethod == "GET" && path == "/api/devices")
            {
                var devices = await _tailnet.ReadAsync();
                await WriteJsonAsync(
                    context,
                    HttpStatusCode.OK,
                    new { Devices = devices });
                return;
            }

            if (request.HttpMethod == "GET" && path == "/api/remote-entry")
            {
                RequireLocal(role, "スマホ連携の入口はこの PC で確認してください。");
                var device = await ReadCurrentTailnetDeviceAsync();
                if (device == null)
                {
                    await WriteJsonAsync(
                        context,
                        HttpStatusCode.ServiceUnavailable,
                        new ErrorBody("Tailscale のスマホ連携 URL を取得できませんでした。"));
                    return;
                }
                await WriteJsonAsync(
                    context,
                    HttpStatusCode.OK,
                    new { Url = device.Url });
                return;
            }

            if (request.HttpMethod == "GET" && path == "/api/remote-entry/qr")
            {
                RequireLocal(role, "スマホ連携の QR はこの PC で表示してください。");
                var device = await ReadCurrentTailnetDeviceAsync();
                if (device == null)
                {
                    await WriteJsonAsync(
                        context,
                        HttpStatusCode.ServiceUnavailable,
                        new ErrorBody("Tailscale のスマホ連携 URL を取得できませんでした。"));
                    return;
                }
                await WriteBytesAsync(
                    context,
                    "image/svg+xml; charset=utf-8",
                    OpticalService.RenderTextQr(device.Url));
                return;
            }

            if (request.HttpMethod == "GET" && path == "/api/remotes")
            {
                RequireLocal(role, "リモコンの一覧はこの PC で確認してください。");
                await WriteJsonAsync(
                    context,
                    HttpStatusCode.OK,
                    new { Remotes = _remotes.ReadActive() });
                return;
            }

            if (request.HttpMethod == "POST" && path == "/api/remotes/heartbeat")
            {
                RequireRemote(role, "リモコンとして開いた画面から接続してください。");
                var body = JsonCodec.ParseObject(await ReadBodyAsync(request));
                var id = ReadString(body, "id");
                object nameValue;
                var name = body.TryGetValue("name", out nameValue) ? nameValue as string : null;
                var heartbeat = _remotes.Heartbeat(id, name);
                await WriteJsonAsync(context, HttpStatusCode.OK, heartbeat);
                return;
            }

            if (request.HttpMethod == "POST" && path == "/api/remotes/disconnect")
            {
                RequireRemote(role, "リモコンとして開いた画面から切断してください。");
                var body = JsonCodec.ParseObject(await ReadBodyAsync(request));
                _remotes.Disconnect(ReadString(body, "id"));
                await WriteJsonAsync(context, HttpStatusCode.OK, new { Disconnected = true });
                return;
            }

            if (request.HttpMethod == "POST" && path == "/api/remotes/command")
            {
                RequireLocal(role, "リモコンの操作はこの PC から実行してください。");
                var body = JsonCodec.ParseObject(await ReadBodyAsync(request));
                var action = ReadString(body, "action");
                List<string> files = null;
                string format = null;
                var frameBytes = 0;
                var framesPerSecond = 0;
                string errorCorrection = null;
                var displaySize = 0;
                if (action == "showQr")
                {
                    files = ReadStringList(body, "files");
                    format = ReadString(body, "format");
                    frameBytes = ReadInt(body, "frameBytes");
                    framesPerSecond = ReadInt(body, "framesPerSecond");
                    errorCorrection = body.ContainsKey("errorCorrection")
                        ? ReadString(body, "errorCorrection")
                        : "L";
                    displaySize = body.ContainsKey("displaySize")
                        ? ReadInt(body, "displaySize")
                        : 900;
                }
                var count = _remotes.SendCommand(
                    action,
                    files,
                    format,
                    frameBytes,
                    framesPerSecond,
                    errorCorrection,
                    displaySize);
                await WriteJsonAsync(
                    context,
                    HttpStatusCode.OK,
                    new { Sent = count });
                return;
            }

            if (request.HttpMethod == "POST" && path == "/api/shutdown")
            {
                if (role == "remote")
                {
                    await WriteJsonAsync(
                        context,
                        HttpStatusCode.Forbidden,
                        new ErrorBody("Ferry can only be stopped from this PC."));
                    return;
                }

                int expectedProcessId;
                var currentProcessId = GetCurrentProcessId();
                if (!int.TryParse(
                        request.Headers["X-Ferry-Process-Id"],
                        out expectedProcessId)
                    || expectedProcessId != currentProcessId)
                {
                    await WriteJsonAsync(
                        context,
                        HttpStatusCode.Conflict,
                        new ErrorBody("The running Ferry process changed before it could be stopped."));
                    return;
                }

                await WriteJsonAsync(
                    context,
                    HttpStatusCode.OK,
                    new { Stopping = true, ProcessId = currentProcessId });
                context.Response.OutputStream.Close();
                _requestShutdown();
                return;
            }

            if (request.HttpMethod == "POST" && path == "/api/activate")
            {
                RequireLocal(role, "右クリックから開く操作はこの PC で実行してください。");

                int expectedProcessId;
                var currentProcessId = GetCurrentProcessId();
                if (!int.TryParse(
                        request.Headers["X-Ferry-Process-Id"],
                        out expectedProcessId)
                    || expectedProcessId != currentProcessId)
                {
                    await WriteJsonAsync(
                        context,
                        HttpStatusCode.Conflict,
                        new ErrorBody("The running Ferry process changed before it could be activated."));
                    return;
                }

                var body = JsonCodec.ParseObject(await ReadBodyAsync(request));
                string mode = null;
                string selectedPath = null;
                if (body.ContainsKey("mode") || body.ContainsKey("path"))
                {
                    if (!body.ContainsKey("mode") || !body.ContainsKey("path"))
                    {
                        throw new ArgumentException(
                            "右クリックの対象と機能を両方指定してください。");
                    }
                    mode = ReadMode(ReadString(body, "mode"));
                    selectedPath = ReadString(body, "path");
                }

                var folder = mode == null
                    ? null
                    : _state.SelectPath(mode, selectedPath);
                if (!DesktopActivation.Request(mode))
                {
                    await WriteJsonAsync(
                        context,
                        HttpStatusCode.ServiceUnavailable,
                        new ErrorBody("Ferry のデスクトップ画面はまだ利用できません。"));
                    return;
                }

                await WriteJsonAsync(
                    context,
                    HttpStatusCode.OK,
                    new { Activated = true, Folder = folder });
                return;
            }

            if (request.HttpMethod == "GET" && path == "/api/folder")
            {
                var mode = ReadMode(request.QueryString["mode"]);
                if (role == "remote" && mode != "optical")
                {
                    await WriteJsonAsync(
                        context,
                        HttpStatusCode.Forbidden,
                        new ErrorBody("リモコンで使えるのは光学転送だけです。"));
                    return;
                }
                await WriteJsonAsync(context, HttpStatusCode.OK, _state.ReadFolder(mode));
                return;
            }

            if (request.HttpMethod == "POST" && path == "/api/folder")
            {
                if (role == "remote")
                {
                    await WriteJsonAsync(
                        context,
                        HttpStatusCode.Forbidden,
                        new ErrorBody("フォルダは PC 側で選んでください。"));
                    return;
                }

                var body = JsonCodec.ParseObject(await ReadBodyAsync(request));
                var mode = ReadMode(body);
                object pathValue;
                var selectedPath = body.TryGetValue("path", out pathValue) ? pathValue as string : null;
                if (string.IsNullOrWhiteSpace(selectedPath))
                {
                    throw new ArgumentException("選ぶフォルダが指定されていません。");
                }

                await WriteJsonAsync(
                    context,
                    HttpStatusCode.OK,
                    _state.SelectFolder(mode, selectedPath));
                return;
            }

            if (request.HttpMethod == "POST" && path == "/api/pick-folder")
            {
                if (role == "remote")
                {
                    await WriteJsonAsync(
                        context,
                        HttpStatusCode.Forbidden,
                        new ErrorBody("フォルダは PC 側で選んでください。"));
                    return;
                }

                if (!NativePicker.IsAvailable)
                {
                    await WriteJsonAsync(
                        context,
                        HttpStatusCode.NotImplemented,
                        new ErrorBody("OS のフォルダ選択ダイアログはこの環境では使えません。"));
                    return;
                }

                var mode = ReadMode(request.QueryString["mode"]);
                var selectedPath = await NativePicker.PickFolderAsync(_state.FolderPath(mode));
                if (selectedPath == null)
                {
                    await WriteJsonAsync(
                        context,
                        HttpStatusCode.OK,
                        new { Cancelled = true, Folder = (FolderSnapshot)null });
                    return;
                }

                await WriteJsonAsync(
                    context,
                    HttpStatusCode.OK,
                    new
                    {
                        Cancelled = false,
                        Folder = _state.SelectFolder(mode, selectedPath)
                    });
                return;
            }

            if (request.HttpMethod == "POST" && path == "/api/pick-files")
            {
                if (role == "remote")
                {
                    await WriteJsonAsync(
                        context,
                        HttpStatusCode.Forbidden,
                        new ErrorBody("ファイルは PC 側で選んでください。"));
                    return;
                }

                if (!NativePicker.IsAvailable)
                {
                    await WriteJsonAsync(
                        context,
                        HttpStatusCode.NotImplemented,
                        new ErrorBody("OS のファイル選択ダイアログはこの環境では使えません。"));
                    return;
                }

                var body = JsonCodec.ParseObject(await ReadBodyAsync(request));
                var mode = ReadMode(body);
                var selectedPaths = await NativePicker.PickFilesAsync(_state.FolderPath(mode), mode);
                if (selectedPaths == null || selectedPaths.Count == 0)
                {
                    await WriteJsonAsync(
                        context,
                        HttpStatusCode.OK,
                        new { Cancelled = true, Folder = (FolderSnapshot)null });
                    return;
                }

                ValidatePickedFiles(selectedPaths, mode);
                await WriteJsonAsync(
                    context,
                    HttpStatusCode.OK,
                    new
                    {
                        Cancelled = false,
                        Folder = _state.SelectFiles(mode, selectedPaths)
                    });
                return;
            }

            if (request.HttpMethod == "POST" && path == "/api/markdown")
            {
                if (role == "remote")
                {
                    await WriteJsonAsync(
                        context,
                        HttpStatusCode.Forbidden,
                        new ErrorBody("Markdown 化は PC 側で実行してください。"));
                    return;
                }

                var body = JsonCodec.ParseObject(await ReadBodyAsync(request));
                var selectedNames = ReadStringList(body, "files");
                object combineValue;
                var combine = !body.TryGetValue("combine", out combineValue)
                    || !(combineValue is bool)
                    || (bool)combineValue;
                var snapshot = _state.ReadFolder("markdown");
                var result = await Task.Run(delegate
                {
                    return MarkdownService.Convert(
                        snapshot,
                        selectedNames,
                        combine,
                        _outputRoot);
                });
                _state.RememberOutput("markdown", result.OutputPath);
                _state.ClearSelection("markdown");
                await WriteJsonAsync(context, HttpStatusCode.OK, result);
                return;
            }

            if (request.HttpMethod == "POST" && path == "/api/vba/inspect")
            {
                RequireLocal(role, "VBA 抽出は PC 側で実行してください。");
                var body = JsonCodec.ParseObject(await ReadBodyAsync(request));
                var fileName = ReadString(body, "file");
                var snapshot = _state.ReadFolder("vba");
                var result = await Task.Run(delegate
                {
                    return VbaService.Inspect(snapshot, fileName);
                });
                await WriteJsonAsync(context, HttpStatusCode.OK, result);
                return;
            }

            if (request.HttpMethod == "POST" && path == "/api/vba/extract")
            {
                RequireLocal(role, "VBA 抽出は PC 側で実行してください。");
                var body = JsonCodec.ParseObject(await ReadBodyAsync(request));
                var selectedNames = ReadStringList(body, "files");
                var snapshot = _state.ReadFolder("vba");
                var result = await Task.Run(delegate
                {
                    return VbaService.Extract(snapshot, selectedNames, _outputRoot);
                });
                _state.RememberOutput("vba", result.OutputPath);
                _state.ClearSelection("vba");
                await WriteJsonAsync(context, HttpStatusCode.OK, result);
                return;
            }

            if (request.HttpMethod == "POST" && path == "/api/open-output")
            {
                RequireLocal(role, "出力先は PC 側で開いてください。");
                var body = JsonCodec.ParseObject(await ReadBodyAsync(request));
                var kind = ReadString(body, "kind");
                var outputPath = _state.ReadOutput(kind);
                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    throw new ArgumentException("先に出力を作ってください。");
                }
                var openedPath = await Task.Run(delegate
                {
                    return OutputLauncher.OpenFolder(outputPath);
                });
                await WriteJsonAsync(
                    context,
                    HttpStatusCode.OK,
                    new { Opened = true, Path = openedPath });
                return;
            }

            if (request.HttpMethod == "POST" && path == "/api/optical/start")
            {
                var body = JsonCodec.ParseObject(await ReadBodyAsync(request));
                var format = ReadString(body, "format");
                var selectedNames = ReadStringList(body, "files");
                var frameBytes = ReadInt(body, "frameBytes");
                var framesPerSecond = ReadInt(body, "framesPerSecond");
                var errorCorrection = body.ContainsKey("errorCorrection")
                    ? ReadString(body, "errorCorrection")
                    : "L";
                var snapshot = _state.ReadFolder("optical");
                var result = await Task.Run(delegate
                {
                    return StartOptical(
                        snapshot,
                        selectedNames,
                        format,
                        frameBytes,
                        framesPerSecond,
                        errorCorrection);
                });
                await WriteJsonAsync(context, HttpStatusCode.OK, result);
                return;
            }

            if (request.HttpMethod == "POST" && path == "/api/optical/receive/start")
            {
                var body = JsonCodec.ParseObject(await ReadBodyAsync(request));
                _opticalReceiver.Reset(ReadString(body, "receiverId"));
                await WriteJsonAsync(context, HttpStatusCode.OK, new { Started = true });
                return;
            }

            if (request.HttpMethod == "POST" && path == "/api/optical/receive/frames")
            {
                var body = JsonCodec.ParseObject(await ReadBodyAsync(request));
                var receiverId = ReadString(body, "receiverId");
                var encodedFrames = ReadStringList(body, "frames");
                if (encodedFrames.Count < 1 || encodedFrames.Count > 8)
                    throw new ArgumentException("一度に受け取れる QR は 1〜8 枚です。");
                var frames = new List<byte[]>();
                foreach (var encoded in encodedFrames)
                {
                    try { frames.Add(Convert.FromBase64String(encoded)); }
                    catch (System.FormatException exception)
                    { throw new ArgumentException("読み取った QR の内容が壊れています。", exception); }
                }
                var openWhenDone = ReadOptionalBool(body, "openWhenDone", true);
                var result = OpticalReceiveResult.NotRecognized();
                foreach (var frame in frames)
                {
                    var current = _opticalReceiver.AddFrame(receiverId, frame, openWhenDone);
                    if (current.Recognized) result = current;
                    if (result.Complete) break;
                }
                await WriteJsonAsync(context, HttpStatusCode.OK, result);
                return;
            }

            if (request.HttpMethod == "POST" && path == "/api/optical/receive/frame")
            {
                var body = JsonCodec.ParseObject(await ReadBodyAsync(request));
                var receiverId = ReadString(body, "receiverId");
                var encodedFrame = ReadString(body, "frame");
                byte[] frame;
                try
                {
                    frame = Convert.FromBase64String(encodedFrame);
                }
                catch (System.FormatException exception)
                {
                    throw new ArgumentException("読み取った QR の内容が壊れています。", exception);
                }
                var result = _opticalReceiver.AddFrame(
                    receiverId,
                    frame,
                    ReadOptionalBool(body, "openWhenDone", true));
                await WriteJsonAsync(context, HttpStatusCode.OK, result);
                return;
            }

            if (request.HttpMethod == "POST" && path == "/api/optical/receive/stop")
            {
                var body = JsonCodec.ParseObject(await ReadBodyAsync(request));
                _opticalReceiver.Stop(ReadString(body, "receiverId"));
                await WriteJsonAsync(context, HttpStatusCode.OK, new { Stopped = true });
                return;
            }

            if (request.HttpMethod == "GET" && path == "/api/optical/frames")
            {
                uint sequence;
                int count;
                if (!uint.TryParse(request.QueryString["seq"],
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out sequence)
                    || !int.TryParse(request.QueryString["count"], out count))
                    throw new ArgumentException("QR の先読み指定が正しくありません。");
                byte[] packed;
                if (!_optical.TryRenderPackedFrames(request.QueryString["token"], sequence, count, out packed))
                {
                    await WriteJsonAsync(context, HttpStatusCode.NotFound,
                        new ErrorBody("光学転送の表示は終了しました。"));
                    return;
                }
                await WriteBytesAsync(context, "application/octet-stream", packed);
                return;
            }

            if (request.HttpMethod == "GET" && path == "/api/optical/frame")
            {
                var token = request.QueryString["token"];
                uint sequence;
                byte[] frame = null;
                var raster = string.Equals(
                    request.QueryString["format"],
                    "raster",
                    StringComparison.OrdinalIgnoreCase);
                var validSequence = uint.TryParse(
                        request.QueryString["seq"],
                        System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out sequence);
                var rendered = validSequence && (raster
                    ? _optical.TryRenderRasterFrame(token, sequence, out frame)
                    : _optical.TryRenderFrame(token, sequence, out frame));
                if (!rendered)
                {
                    await WriteJsonAsync(
                        context,
                        HttpStatusCode.NotFound,
                        new ErrorBody("光学転送の表示は終了しました。"));
                    return;
                }
                await WriteBytesAsync(
                    context,
                    raster ? "application/octet-stream" : "image/svg+xml; charset=utf-8",
                    frame);
                return;
            }

            if (request.HttpMethod == "POST" && path == "/api/optical/stop")
            {
                var body = JsonCodec.ParseObject(await ReadBodyAsync(request));
                _optical.Stop(ReadString(body, "token"));
                await WriteJsonAsync(context, HttpStatusCode.OK, new { Stopped = true });
                return;
            }

            Asset asset;
            if ((request.HttpMethod == "GET" || request.HttpMethod == "HEAD")
                && StaticAssets.TryGet(path, out asset))
            {
                await WriteAssetAsync(context, asset, request.HttpMethod == "HEAD");
                return;
            }

            await WriteJsonAsync(
                context,
                HttpStatusCode.NotFound,
                new ErrorBody("Not found"));
        }

        private OpticalStartResult StartOptical(
            FolderSnapshot source,
            List<string> selectedNames,
            string format,
            int frameBytes,
            int framesPerSecond,
            string errorCorrection)
        {
            if (string.Equals(format, "original", StringComparison.Ordinal))
            {
                return _optical.Start(
                    source,
                    selectedNames,
                    frameBytes,
                    framesPerSecond,
                    errorCorrection);
            }
            if (format != "markdown" && format != "vba")
            {
                throw new ArgumentException("送る形が正しくありません。", "format");
            }

            var convertibleNames = SelectConvertibleFiles(
                source,
                selectedNames,
                format);
            var temporaryRoot = Path.Combine(
                Path.GetTempPath(),
                "Ferry-transfer-" + Guid.NewGuid().ToString("N"));
            try
            {
                string outputPath;
                if (format == "markdown")
                {
                    // A transfer's omission list must stay within the selected files.
                    var requested = new HashSet<string>(selectedNames, StringComparer.CurrentCultureIgnoreCase);
                    var selectedFiles = source.Files.FindAll(delegate (FolderFile file) { return requested.Contains(file.Name); });
                    var selectedSource = new FolderSnapshot(source.Path, source.DirectoryPath, source.SourceKind, selectedFiles);
                    outputPath = MarkdownService.Convert(
                        selectedSource,
                        convertibleNames,
                        true,
                        temporaryRoot).OutputPath;
                }
                else
                {
                    outputPath = VbaService.Extract(
                        source,
                        convertibleNames,
                        temporaryRoot).OutputPath;
                }

                var generated = FolderCatalog.InspectFiles(new[] { outputPath });
                var transferSource = new FolderSnapshot(
                    source.Path,
                    source.DirectoryPath,
                    "files",
                    generated.Files);
                return _optical.Start(
                    transferSource,
                    new[] { generated.Files[0].Name },
                    frameBytes,
                    framesPerSecond,
                    errorCorrection);
            }
            finally
            {
                DeleteTemporaryDirectory(temporaryRoot);
            }
        }

        private static List<string> SelectConvertibleFiles(
            FolderSnapshot source,
            IList<string> selectedNames,
            string format)
        {
            var available = new Dictionary<string, FolderFile>(
                StringComparer.CurrentCultureIgnoreCase);
            foreach (var file in source.Files)
            {
                available[file.Name] = file;
            }

            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
            foreach (var name in selectedNames)
            {
                FolderFile file;
                if (string.IsNullOrWhiteSpace(name) || !available.TryGetValue(name, out file))
                {
                    throw new FileNotFoundException(
                        string.Format("選んだファイルが見つかりません: {0}", name));
                }

                var supported = format == "markdown"
                    ? file.MarkdownSupported
                    : file.VbaWorkbook;
                if (supported && seen.Add(file.Name))
                {
                    result.Add(file.Name);
                }
            }

            if (result.Count == 0)
            {
                throw new ArgumentException(format == "markdown"
                    ? "選んだ中に Markdown 化できるファイルがありません。"
                    : "選んだ中に VBA を取り出せるブックがありません。");
            }
            return result;
        }

        private static void DeleteTemporaryDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch (IOException)
            {
                // A completed transfer must not fail because temporary cleanup was delayed.
            }
            catch (UnauthorizedAccessException)
            {
                // A completed transfer must not fail because temporary cleanup was delayed.
            }
        }

        private object BuildStatus(string role)
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return new
            {
                Device = Environment.MachineName,
                ProcessId = GetCurrentProcessId(),
                Platform = PlatformInfo.Name,
                Role = role,
                Version = version == null ? "0.0.0" : version.ToString(3),
                InitialMode = _initialMode,
                RemoteCount = _remotes.ReadActive().Count,
                Capabilities = new
                {
                    Word = PlatformInfo.IsWindows && WordIsRegistered(),
                    WindowsOcr = PlatformInfo.IsWindows && Environment.OSVersion.Version.Major >= 10,
                    NativePicker = NativePicker.IsAvailable,
                    Camera = "browser"
                }
            };
        }

        private async Task<TailnetDevice> ReadCurrentTailnetDeviceAsync()
        {
            var devices = await _tailnet.ReadAsync();
            foreach (var device in devices)
            {
                if (device.Current && !string.IsNullOrWhiteSpace(device.Url))
                {
                    return device;
                }
            }
            return null;
        }

        private static string ReadMode(Dictionary<string, object> body)
        {
            object value;
            var mode = body.TryGetValue("mode", out value) ? value as string : "optical";
            return ReadMode(mode);
        }

        private static string ReadMode(string mode)
        {
            if (string.IsNullOrWhiteSpace(mode))
            {
                mode = "optical";
            }
            if (mode != "optical" && mode != "markdown" && mode != "vba")
            {
                throw new ArgumentException("ファイルを選ぶ機能が正しく指定されていません。");
            }
            return mode;
        }

        private static List<string> ReadStringList(
            Dictionary<string, object> body,
            string propertyName)
        {
            object value;
            var raw = body.TryGetValue(propertyName, out value)
                ? value as List<object>
                : null;
            if (raw == null)
            {
                throw new ArgumentException("ファイルの一覧が指定されていません。");
            }

            var result = new List<string>();
            foreach (var item in raw)
            {
                var text = item as string;
                if (string.IsNullOrWhiteSpace(text))
                {
                    throw new ArgumentException("ファイル名が正しくありません。");
                }
                result.Add(text);
            }
            return result;
        }

        private static string ReadString(
            Dictionary<string, object> body,
            string propertyName)
        {
            object value;
            var result = body.TryGetValue(propertyName, out value) ? value as string : null;
            if (string.IsNullOrWhiteSpace(result))
            {
                throw new ArgumentException("必要な指定がありません: " + propertyName);
            }
            return result;
        }

        private static int ReadInt(
            Dictionary<string, object> body,
            string propertyName)
        {
            object value;
            if (!body.TryGetValue(propertyName, out value) || value == null)
            {
                throw new ArgumentException("必要な指定がありません: " + propertyName);
            }

            try
            {
                return Convert.ToInt32(
                    value,
                    System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception exception)
            {
                throw new ArgumentException(
                    "数値の指定が正しくありません: " + propertyName,
                    exception);
            }
        }

        private static bool ReadOptionalBool(
            Dictionary<string, object> body,
            string propertyName,
            bool fallback)
        {
            object value;
            return body.TryGetValue(propertyName, out value) && value is bool
                ? (bool)value
                : fallback;
        }

        private static void RequireLocal(string role, string message)
        {
            if (role == "remote")
            {
                throw new UnauthorizedAccessException(message);
            }
        }

        private static void RequireRemote(string role, string message)
        {
            if (role != "remote")
            {
                throw new UnauthorizedAccessException(message);
            }
        }

        private static void ValidatePickedFiles(IList<string> paths, string mode)
        {
            foreach (var path in paths)
            {
                // Markdown keeps excluded selections for the names-only report.
                if (mode == "vba" && !FolderCatalog.SupportsVba(path))
                {
                    throw new ArgumentException(string.Format(
                        "VBA 抽出の対象外です: {0}",
                        Path.GetFileName(path)));
                }
            }
        }

        private static int GetCurrentProcessId()
        {
            using (var process = Process.GetCurrentProcess())
            {
                return process.Id;
            }
        }

        private static string GetRequestRole(HttpListenerRequest request)
        {
            var host = request.Headers["Host"];
            if (string.IsNullOrWhiteSpace(host))
            {
                return "remote";
            }

            host = host.Trim();
            if (host.Length > 0 && host[0] == '[')
            {
                var closingBracket = host.IndexOf(']');
                if (closingBracket > 0)
                {
                    host = host.Substring(1, closingBracket - 1);
                }
            }
            else
            {
                var colon = host.LastIndexOf(':');
                if (colon > 0 && host.IndexOf(':') == colon)
                {
                    host = host.Substring(0, colon);
                }
            }

            return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                || string.Equals(host, "127.0.0.1", StringComparison.Ordinal)
                ? "local"
                : "remote";
        }

        private static bool WordIsRegistered()
        {
            if (!PlatformInfo.IsWindows)
            {
                return false;
            }

            try
            {
                return Type.GetTypeFromProgID("Word.Application", false) != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static async Task<byte[]> ReadBodyAsync(HttpListenerRequest request)
        {
            if (request.ContentLength64 > MaximumRequestBytes)
            {
                throw new ArgumentException("Request body is too large.");
            }

            using (var buffer = new MemoryStream())
            {
                var chunk = new byte[4096];
                while (true)
                {
                    var read = await request.InputStream.ReadAsync(chunk, 0, chunk.Length);
                    if (read == 0)
                    {
                        return buffer.ToArray();
                    }

                    buffer.Write(chunk, 0, read);
                    if (buffer.Length > MaximumRequestBytes)
                    {
                        throw new ArgumentException("Request body is too large.");
                    }
                }
            }
        }

        private static async Task WriteAssetAsync(
            HttpListenerContext context,
            Asset asset,
            bool headOnly)
        {
            var response = context.Response;
            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentType = asset.ContentType;
            response.ContentLength64 = asset.Bytes.LongLength;
            response.Headers[HttpResponseHeader.CacheControl] = asset.Cacheable
                ? "public, max-age=3600"
                : "no-cache";

            if (!headOnly)
            {
                await response.OutputStream.WriteAsync(asset.Bytes, 0, asset.Bytes.Length);
            }
        }

        private static async Task WriteJsonAsync(
            HttpListenerContext context,
            HttpStatusCode status,
            object body)
        {
            var bytes = JsonCodec.Serialize(body);
            var response = context.Response;
            response.StatusCode = (int)status;
            response.ContentType = "application/json; charset=utf-8";
            response.ContentEncoding = Encoding.UTF8;
            response.ContentLength64 = bytes.LongLength;
            response.Headers[HttpResponseHeader.CacheControl] = "no-store";
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        }

        private static async Task WriteBytesAsync(
            HttpListenerContext context,
            string contentType,
            byte[] bytes)
        {
            var response = context.Response;
            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentType = contentType;
            response.ContentLength64 = bytes.LongLength;
            response.Headers[HttpResponseHeader.CacheControl] = "no-store";
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        }

        private static void AddSecurityHeaders(HttpListenerResponse response)
        {
            response.Headers["Content-Security-Policy"] =
                "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; worker-src 'self'; " +
                "style-src 'self'; img-src 'self' data:; media-src 'self' blob:; " +
                "connect-src 'self'; frame-ancestors 'none'; " +
                "base-uri 'none'; form-action 'self'";
            response.Headers["Permissions-Policy"] =
                "camera=(self), microphone=(), geolocation=()";
            response.Headers["Referrer-Policy"] = "no-referrer";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers["X-Frame-Options"] = "DENY";
        }

        private sealed class ErrorBody
        {
            public ErrorBody(string error)
            {
                Error = error;
            }

            public string Error { get; private set; }
        }
    }
}
