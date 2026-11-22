using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Arr411.Core.Config;
using Arr411.Core.Update;

namespace Arr411.Core.Tests;

/// <summary>A clock that only moves when a test moves it, and whose timers only fire when a test fires them.</summary>
internal sealed class FakeClock : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<FakeTimer> _timers = [];
    private DateTimeOffset _now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    /// <summary>Every timer ever made, the ones that were disposed included.</summary>
    public IReadOnlyList<FakeTimer> Timers
    {
        get
        {
            lock (_gate) return [.. _timers];
        }
    }

    public IReadOnlyList<FakeTimer> LiveTimers => [.. Timers.Where(timer => !timer.Disposed)];

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _now;
    }

    public void Advance(TimeSpan by)
    {
        lock (_gate) _now += by;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(callback, state, dueTime, period);
        lock (_gate) _timers.Add(timer);
        return timer;
    }
}

internal sealed class FakeTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) : ITimer
{
    public TimeSpan DueTime { get; private set; } = dueTime;

    public TimeSpan Period { get; private set; } = period;

    public bool Disposed { get; private set; }

    public bool Change(TimeSpan newDueTime, TimeSpan newPeriod)
    {
        DueTime = newDueTime;
        Period = newPeriod;
        return true;
    }

    public void Fire()
    {
        if (!Disposed) callback(state);
    }

    public void Dispose() => Disposed = true;

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>A body that cannot tell its length beforehand, so that nothing announces what it weighs.</summary>
internal abstract class NonSeekableStream : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Gives its bytes and ends; the size is not announced.</summary>
internal sealed class OneWayStream(byte[] bytes) : NonSeekableStream
{
    private int _position;

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var count = Math.Min(buffer.Length, bytes.Length - _position);
        bytes.AsMemory(_position, count).CopyTo(buffer);
        _position += count;
        return ValueTask.FromResult(count);
    }
}

/// <summary>Gives a first piece, then nothing more, for as long as it is waited for: a connection that went silent.</summary>
internal sealed class StallingStream(byte[] firstPiece) : NonSeekableStream
{
    private bool _gave;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!_gave)
        {
            _gave = true;
            var count = Math.Min(buffer.Length, firstPiece.Length);
            firstPiece.AsMemory(0, count).CopyTo(buffer);
            return count;
        }

        await Task.Delay(Timeout.Infinite, cancellationToken);
        return 0;
    }
}

/// <summary>Gives a first piece, then breaks the connection.</summary>
internal sealed class BreakingStream(byte[] firstPiece) : NonSeekableStream
{
    private bool _gave;

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_gave) throw new IOException("the connection was closed");

        _gave = true;
        var count = Math.Min(buffer.Length, firstPiece.Length);
        firstPiece.AsMemory(0, count).CopyTo(buffer);
        return ValueTask.FromResult(count);
    }
}

/// <summary>What GitHub answers for the repository of the app, from memory: the release, the program and the sums file behind their redirects.</summary>
internal sealed class FakeGitHub
{
    public const string ApiAddress = "https://api.github.com/repos/HugoMeyer/arr411-desktop/releases/latest";
    public const string ProgramCdn = "https://release-assets.githubusercontent.com/github-production-release-asset/42/Arr411.exe";
    public const string SumsCdn = "https://release-assets.githubusercontent.com/github-production-release-asset/42/SHA256SUMS.txt";

    private readonly object _gate = new();
    private readonly List<RecordedRequest> _requests = [];

    public string Version { get; set; } = "0.3.0";

    /// <summary>The tag when it is not "v" and the version.</summary>
    public string? Tag { get; set; }

    public bool Draft { get; set; }

    public bool Prerelease { get; set; }

    public string Notes { get; set; } = "Corrections et améliorations.";

    public string Etag { get; set; } = "W/\"release-1\"";

    /// <summary>What the API answers: 200 is the release, anything else is the error.</summary>
    public int ApiStatus { get; set; } = 200;

    public Exception? ApiThrows { get; set; }

    public bool HangApi { get; set; }

    /// <summary>The program the release is about: its size and its hash are what the release announces.</summary>
    public byte[] Program { get; set; } = Bytes(200 * 1024, 7);

    /// <summary>What is actually sent for the program, when it is not the program.</summary>
    public byte[]? Served { get; set; }

    /// <summary>The size the release announces, when it is not that of the program.</summary>
    public long? Size { get; set; }

    public bool HasDigest { get; set; } = true;

    /// <summary>The digest the release announces, when it is not the one of the program.</summary>
    public string? Digest { get; set; }

    /// <summary>The text of SHA256SUMS.txt; null when the release has no such file.</summary>
    public string? Sums { get; set; }

    /// <summary>The address of the program in the release, when it is not the one of GitHub.</summary>
    public string? ProgramUrl { get; set; }

    /// <summary>Where the address of the program leads, when it is not the CDN of GitHub.</summary>
    public string? AssetRedirect { get; set; }

    /// <summary>Redirects between the address of the program and the CDN.</summary>
    public int Hops { get; set; }

    public int AssetStatus { get; set; } = 200;

    public int SumsStatus { get; set; } = 200;

    public bool NeverAnswerDownload { get; set; }

    public bool StallDownloadBody { get; set; }

    public bool BreakDownloadBody { get; set; }

    public bool ServeWithoutLength { get; set; }

    /// <summary>Answers a request in place of the rest of this class; null leaves it to it.</summary>
    public Func<RecordedRequest, HttpResponseMessage?>? Override { get; set; }

    public string TagName => Tag ?? "v" + Version;

    public string PageAddress => $"https://github.com/HugoMeyer/arr411-desktop/releases/tag/{TagName}";

    public string ProgramAddress => $"https://github.com/HugoMeyer/arr411-desktop/releases/download/{TagName}/Arr411.exe";

    public string SumsAddress => $"https://github.com/HugoMeyer/arr411-desktop/releases/download/{TagName}/SHA256SUMS.txt";

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate) return [.. _requests];
        }
    }

    public string[] Targets => [.. Requests.Select(request => request.Target)];

    public int ApiRequests => Requests.Count(request => request.Target == ApiAddress);

    /// <summary>How many times the program was asked for at its address of the release: the redirects that follow are not counted.</summary>
    public int ProgramRequests => Requests.Count(request => request.Target.StartsWith("https://github.com/", StringComparison.Ordinal) && request.Target.EndsWith("/Arr411.exe", StringComparison.Ordinal));

    public static byte[] Bytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    public static string HashOf(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static string SumsFor(byte[] bytes, string name = "Arr411.exe") => $"{HashOf(bytes)}  {name}\n";

    /// <summary>A handler for one service: a service disposes the one it is given.</summary>
    public HttpMessageHandler CreateHandler() => new StubHandler(RespondAsync);

    private async Task<HttpResponseMessage> RespondAsync(RecordedRequest request, CancellationToken token)
    {
        lock (_gate) _requests.Add(request);
        if (Override?.Invoke(request) is { } custom) return custom;

        var uri = new Uri(request.Target);
        var path = uri.AbsolutePath;
        switch (uri.Host)
        {
            case "api.github.com":
                return await ApiAsync(request, token);
            case "github.com" when path.EndsWith("/Arr411.exe", StringComparison.Ordinal):
                return Redirect(AssetRedirect ?? (Hops > 0 ? "https://github.com/hop/1" : ProgramCdn));
            case "github.com" when path.StartsWith("/hop/", StringComparison.Ordinal):
                return Redirect(NextHop(int.Parse(path["/hop/".Length..])));
            case "github.com" when path.EndsWith("/SHA256SUMS.txt", StringComparison.Ordinal):
                return Redirect(SumsCdn);
            case "release-assets.githubusercontent.com" when path.EndsWith("/Arr411.exe", StringComparison.Ordinal):
                return await DownloadAsync(token);
            case "release-assets.githubusercontent.com" when path.EndsWith("/SHA256SUMS.txt", StringComparison.Ordinal):
                return StubHandler.Reply((HttpStatusCode)SumsStatus, Encoding.UTF8.GetBytes(Sums ?? ""), "text/plain");
            default:
                return StubHandler.Reply(HttpStatusCode.NotFound);
        }
    }

    private async Task<HttpResponseMessage> ApiAsync(RecordedRequest request, CancellationToken token)
    {
        if (ApiThrows is not null) throw ApiThrows;

        if (HangApi) await Task.Delay(Timeout.Infinite, token);

        if (ApiStatus != 200) return StubHandler.Reply((HttpStatusCode)ApiStatus);

        if (request.Header("If-None-Match") == Etag) return StubHandler.Reply(HttpStatusCode.NotModified);

        var response = StubHandler.Reply(HttpStatusCode.OK, Encoding.UTF8.GetBytes(ReleaseJson()), "application/json");
        response.Headers.ETag = EntityTagHeaderValue.Parse(Etag);
        return response;
    }

    private async Task<HttpResponseMessage> DownloadAsync(CancellationToken token)
    {
        if (AssetStatus != 200) return StubHandler.Reply((HttpStatusCode)AssetStatus);

        if (NeverAnswerDownload) await Task.Delay(Timeout.Infinite, token);

        var bytes = Served ?? Program;
        HttpContent content = StallDownloadBody ? new StreamContent(new StallingStream(bytes[..4096]))
            : BreakDownloadBody ? new StreamContent(new BreakingStream(bytes[..4096]))
            : ServeWithoutLength ? new StreamContent(new OneWayStream(bytes))
            : new ByteArrayContent(bytes);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static HttpResponseMessage Redirect(string location) => StubHandler.Reply(HttpStatusCode.Found, location: location);

    private string NextHop(int hop) => hop < Hops ? $"https://github.com/hop/{hop + 1}" : ProgramCdn;

    private string ReleaseJson()
    {
        var assets = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "Arr411.exe",
                ["browser_download_url"] = ProgramUrl ?? ProgramAddress,
                ["size"] = Size ?? Program.Length,
                ["digest"] = HasDigest ? (Digest ?? "sha256:" + HashOf(Program)) : null,
            },
        };
        if (Sums is not null)
        {
            assets.Add(new JsonObject { ["name"] = "SHA256SUMS.txt", ["browser_download_url"] = SumsAddress, ["size"] = Sums.Length });
        }

        return new JsonObject
        {
            ["tag_name"] = TagName,
            ["draft"] = Draft,
            ["prerelease"] = Prerelease,
            ["html_url"] = PageAddress,
            ["body"] = Notes,
            ["assets"] = assets,
        }.ToJsonString();
    }
}

internal static class Wait
{
    public static async Task Until(Func<bool> condition, int milliseconds = 10_000)
    {
        var deadline = Environment.TickCount64 + milliseconds;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException("the condition was not met in time");

            await Task.Delay(10);
        }
    }
}

/// <summary>An update service on a disk of its own, with a fake GitHub, a fake clock and a fake way to start the applier.</summary>
internal sealed class UpdateHarness : IDisposable
{
    public static readonly byte[] OldProgram = FakeGitHub.Bytes(4096, 1);

    private readonly TempDir _dir = new();
    private readonly List<UpdateNotice> _notices = [];
    private readonly List<UpdateState> _states = [];
    private readonly List<ProcessStartInfo> _started = [];
    private int _restartRequests;

    public UpdateHarness(string exeName = "Arr411.exe")
    {
        Paths = new AppPaths(_dir.File("data"), _dir.File("downloads"));
        AppFolder = _dir.File("app");
        Directory.CreateDirectory(AppFolder);
        ExePath = Path.Combine(AppFolder, exeName);
        File.WriteAllBytes(ExePath, OldProgram);
        Config = OpenConfig();
    }

    public FakeGitHub GitHub { get; } = new();

    public FakeClock Clock { get; } = new();

    public AppPaths Paths { get; }

    public string AppFolder { get; }

    public string ExePath { get; }

    public ConfigStore Config { get; private set; }

    public UpdateService? Service { get; private set; }

    public string Current { get; set; } = "0.2.1";

    public int ProcessId { get; set; } = 4242;

    public bool Disabled { get; set; }

    public bool AutoApply { get; set; } = true;

    public bool CanWrite { get; set; } = true;

    public bool StartResult { get; set; } = true;

    public bool StartThrows { get; set; }

    /// <summary>Called as the applier is started: what the service had done by then can be looked at.</summary>
    public Action<ProcessStartInfo>? OnStart { get; set; }

    public TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan ApiTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public string UpdatesFolder => Path.Combine(Paths.Root, "updates");

    public string StatePath => Path.Combine(UpdatesFolder, "state.json");

    public string ErrorPath => Path.Combine(UpdatesFolder, "last-error.txt");

    public IReadOnlyList<UpdateNotice> Notices
    {
        get
        {
            lock (_notices) return [.. _notices];
        }
    }

    public IReadOnlyList<UpdateState> States
    {
        get
        {
            lock (_states) return [.. _states];
        }
    }

    public IReadOnlyList<ProcessStartInfo> Started
    {
        get
        {
            lock (_started) return [.. _started];
        }
    }

    public int RestartRequests => Volatile.Read(ref _restartRequests);

    public string StagedFile(string version) => Path.Combine(UpdatesFolder, $"Arr411-{version}.exe");

    /// <summary>The downloads that were not finished (or not cleaned up).</summary>
    public string[] Partials => Directory.Exists(UpdatesFolder) ? Directory.GetFiles(UpdatesFolder, "*.part") : [];

    /// <summary>What the service wrote of itself on disk.</summary>
    public UpdateRecord Record => UpdateRecord.Parse(File.ReadAllText(StatePath));

    public void SetMode(string mode) => Config.Merge($$"""{"autoUpdate":"{{mode}}"}""", "test");

    /// <summary>A new service, with nothing done to it yet.</summary>
    public UpdateService Create(Action<UpdateService>? subscribeFirst = null)
    {
        var service = new UpdateService(new UpdateOptions
        {
            Paths = Paths,
            Config = Config,
            CurrentVersion = Current,
            ExePath = ExePath,
            ProcessId = ProcessId,
            Handler = GitHub.CreateHandler(),
            Clock = Clock,
            StartProcess = StartApplier,
            CanWriteFolder = _ => CanWrite,
            Disabled = Disabled,
            AutoApply = AutoApply,
            StallTimeout = StallTimeout,
            ApiTimeout = ApiTimeout,
            ProgressInterval = TimeSpan.Zero,
        });
        subscribeFirst?.Invoke(service);
        service.Notice += (_, notice) =>
        {
            lock (_notices) _notices.Add(notice);
        };
        service.StateChanged += (_, state) =>
        {
            lock (_states) _states.Add(state);
        };
        service.RestartRequested += (_, _) => Interlocked.Increment(ref _restartRequests);
        Service = service;
        return service;
    }

    /// <summary>A new service, started the way the app does it.</summary>
    public UpdateService Boot(string? updatedFrom = null, Action<UpdateService>? subscribeFirst = null)
    {
        var service = Create(subscribeFirst);
        service.Initialize();
        service.Start(updatedFrom);
        return service;
    }

    /// <summary>A started service that found the release and downloaded it: the update is ready.</summary>
    public async Task<UpdateService> BootReadyAsync(string? updatedFrom = null)
    {
        var service = Boot(updatedFrom);
        await service.CheckAsync(manual: false);
        if (service.State.Status != UpdateStatus.Ready)
        {
            throw new InvalidOperationException($"the update is not ready: {service.State.Status} ({service.State.Error})");
        }

        return service;
    }

    /// <summary>The app is closed and started again, on the same disk (and, when asked, as another version).</summary>
    public UpdateService Reboot(string? current = null, string? updatedFrom = null)
    {
        Service?.Dispose();
        if (current is not null) Current = current;

        Config = OpenConfig();
        return Boot(updatedFrom);
    }

    public void Dispose()
    {
        Service?.Dispose();
        _dir.Dispose();
    }

    private ConfigStore OpenConfig() => new(Paths.ConfigFile, new ReversibleProtector());

    private bool StartApplier(ProcessStartInfo info)
    {
        OnStart?.Invoke(info);
        if (StartThrows) throw new System.ComponentModel.Win32Exception(2);

        lock (_started) _started.Add(info);
        return StartResult;
    }
}
