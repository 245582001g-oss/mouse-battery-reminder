using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SoraV2BatteryTip;

internal interface IAppEventLog : IDisposable
{
    void Write(
        string level,
        string eventName,
        string component,
        string outcome,
        object? data = null,
        Exception? exception = null,
        BatteryReading? reading = null);

    IDisposable BeginOperation(string name);
    string DeviceToken(BatteryReading reading);
    string TokenFor(string value, string prefix);
    bool Flush(TimeSpan timeout);
    long SnapshotRecentLogs(string target, long maxBytes);
    string? CrashSnapshot(
        Exception exception,
        string component = "Program",
        string eventName = "app.unhandled_exception",
        long maxBytes = 2 * 1024 * 1024);
}

internal sealed class FlightRecorder : IAppEventLog
{
    private const int QueueCapacity = 2048;
    private const long RotationBytes = 4L * 1024 * 1024;
    private const int RetentionDays = 14;
    private const int MaximumArchiveCount = 12;
    private const long MaximumArchiveBytes = 32L * 1024 * 1024;
    private const int MaximumEventBytes = 16 * 1024;
    private const int MaximumStringLength = 256;
    private const int MaximumArrayLength = 32;
    private const int MaximumObjectProperties = 64;
    private const int MaximumJsonDepth = 7;
    private const long MaximumSnapshotBytes = 32L * 1024 * 1024;
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly JsonSerializerOptions DataJsonOptions = new()
    {
        MaxDepth = MaximumJsonDepth
    };

    private readonly BlockingCollection<QueueItem> _queue = new(
        new ConcurrentQueue<QueueItem>(),
        QueueCapacity);
    private readonly object _enqueueGate = new();
    private readonly AsyncLocal<OperationContext?> _operation = new();
    private readonly string _logsDirectory;
    private readonly string _activePath;
    private readonly string _keyPath;
    private readonly string _legacyKeyPath;
    private readonly string _runId;
    private readonly string _runShort;
    private readonly string _appVersion;
    private readonly byte[] _installationKey;
    private readonly bool _previousRunUnclean;
    private readonly long _startedTimestamp = Stopwatch.GetTimestamp();
    private readonly Thread? _writerThread;
    private long _sequence;
    private long _operationSequence;
    private long _droppedEvents;
    private int _disposeState;
    private int _resourcesReleased;
    private PendingEvent? _deferredStopEvent;
    private TaskCompletionSource<bool>? _stopCompletion;

    // The fields below are owned by the single writer thread.
    private FileStream? _stream;
    private StreamWriter? _writer;
    private DateTime _activeUtcDate;
    private DateTime _lastBufferedFlushUtc = DateTime.MinValue;
    private DateTime _nextOpenAttemptUtc = DateTime.MinValue;
    private long _activeBytes;
    private int _archiveSequence;
    private bool _dirty;
    private bool _firstOpen = true;
    private DateTime _nextRotationAttemptUtc = DateTime.MinValue;
    private int _rotationFailureCount;

    public FlightRecorder(string? logsDirectory = null, string? keyPath = null)
    {
        _runId = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
        _runShort = _runId.Replace("-", string.Empty, StringComparison.Ordinal)[..8];
        _logsDirectory = ResolveLogsDirectory(logsDirectory);
        _activePath = Path.Combine(_logsDirectory, "flight-current.jsonl");
        _keyPath = ResolveKeyPath(_logsDirectory, keyPath);
        _legacyKeyPath = Path.Combine(_logsDirectory, ".flight-recorder.key");
        _appVersion = ReadAppVersion();
        _installationKey = LoadOrCreateInstallationKey(_keyPath, _legacyKeyPath, _runId);
        _previousRunUnclean = IsPreviousRunUnclean(_activePath);

        Thread? thread = null;
        try
        {
            thread = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "SoraV2BatteryTip.FlightRecorder"
            };
            thread.Start();
        }
        catch
        {
            try { _queue.CompleteAdding(); }
            catch { }
            thread = null;
        }

        _writerThread = thread;
        Write(
            "info",
            "logger.started",
            nameof(FlightRecorder),
            thread == null ? "degraded" : "success",
            new { queue_capacity = QueueCapacity, rotation_bytes = RotationBytes });
        if (_previousRunUnclean)
            Write("warn", "app.previous_run_unclean", nameof(FlightRecorder), "degraded");
    }

    public void Write(
        string level,
        string eventName,
        string component,
        string outcome,
        object? data = null,
        Exception? exception = null,
        BatteryReading? reading = null)
    {
        try
        {
            lock (_enqueueGate)
            {
                if (Volatile.Read(ref _disposeState) != 0)
                    return;

                var pending = CaptureEvent(level, eventName, component, outcome, data, exception, reading);
                if (!_queue.TryAdd(QueueItem.ForEvent(pending)))
                    Interlocked.Increment(ref _droppedEvents);
            }
        }
        catch
        {
            Interlocked.Increment(ref _droppedEvents);
        }
    }

    public IDisposable BeginOperation(string name)
    {
        try
        {
            var previous = _operation.Value;
            var prefix = SanitizeEnvelopeValue(name, "op", 24, "op");
            var sequence = Interlocked.Increment(ref _operationSequence);
            var current = new OperationContext($"{prefix}-{sequence:D6}");
            _operation.Value = current;
            return new OperationScope(this, current, previous);
        }
        catch
        {
            return NoopScope.Instance;
        }
    }

    public string DeviceToken(BatteryReading reading)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(reading.AssociationReceiverHistoryKey))
            {
                var receiverSerial = DeviceIdentity.NormalizeSerial(reading.AssociationReceiverSerial)
                    ?? (reading.ConnectionTransport == DeviceConnectionTransport.Receiver
                        ? DeviceIdentity.NormalizeSerial(reading.DeviceSerial)
                        : null);
                return TokenFor(
                    receiverSerial == null
                        ? $"receiver:{reading.AssociationReceiverHistoryKey}"
                        : $"receiver:{reading.AssociationReceiverHistoryKey}:serial:{receiverSerial}",
                    "dev");
            }

            if (!string.IsNullOrWhiteSpace(reading.LogicalDeviceId))
            {
                var logicalDeviceId = reading.LogicalDeviceId.Trim();
                var receiverSerial = logicalDeviceId.StartsWith(
                        "ninjutso-sora-v2:receiver:",
                        StringComparison.OrdinalIgnoreCase)
                    ? DeviceIdentity.NormalizeSerial(reading.DeviceSerial)
                    : null;
                return TokenFor(
                    receiverSerial == null
                        ? $"logical:{logicalDeviceId}"
                        : $"logical:{logicalDeviceId}:receiver-serial:{receiverSerial}",
                    "dev");
            }

            var serial = DeviceIdentity.NormalizeSerial(reading.DeviceSerial);
            if (!string.IsNullOrEmpty(serial))
                return TokenFor($"serial:{reading.VendorId}:{reading.ProductId}:{serial}", "dev");
            if (!string.IsNullOrWhiteSpace(reading.DeviceId))
                return TokenFor($"path:{reading.DeviceId}", "dev");

            var fallback = string.Join(
                "|",
                reading.VendorId ?? string.Empty,
                reading.ProductId ?? string.Empty,
                reading.DeviceName ?? string.Empty,
                reading.Source ?? string.Empty);
            return TokenFor($"fallback:{fallback}", "dev");
        }
        catch
        {
            return "dev_unavailable";
        }
    }

    public string TokenFor(string value, string prefix)
    {
        var safePrefix = SanitizeLabel(prefix, "tok", 12);
        if (string.IsNullOrEmpty(value))
            return $"{safePrefix}_none";

        byte[]? input = null;
        try
        {
            input = Utf8NoBom.GetBytes(value);
            using var hmac = new HMACSHA256(_installationKey);
            var digest = hmac.ComputeHash(input);
            return $"{safePrefix}_{Convert.ToHexString(digest.AsSpan(0, 12)).ToLowerInvariant()}";
        }
        catch
        {
            try
            {
                var fallbackBytes = Utf8NoBom.GetBytes($"{_runId}|{value}");
                var digest = SHA256.HashData(fallbackBytes);
                CryptographicOperations.ZeroMemory(fallbackBytes);
                return $"{safePrefix}_{Convert.ToHexString(digest.AsSpan(0, 12)).ToLowerInvariant()}";
            }
            catch
            {
                return $"{safePrefix}_unavailable";
            }
        }
        finally
        {
            if (input != null)
                CryptographicOperations.ZeroMemory(input);
        }
    }

    public bool Flush(TimeSpan timeout)
    {
        if (_writerThread == null || Volatile.Read(ref _disposeState) != 0)
            return false;

        try
        {
            var milliseconds = ClampTimeoutMilliseconds(timeout);
            var stopwatch = Stopwatch.StartNew();
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var item = QueueItem.ForFlush(completion);
            if (!_queue.TryAdd(item, milliseconds))
                return false;

            var remaining = Math.Max(0, milliseconds - (int)Math.Ceiling(stopwatch.Elapsed.TotalMilliseconds));
            if (completion.Task.IsCompleted)
                return completion.Task.GetAwaiter().GetResult();
            return remaining > 0
                && completion.Task.Wait(remaining)
                && completion.Task.GetAwaiter().GetResult();
        }
        catch
        {
            return false;
        }
    }

    public long SnapshotRecentLogs(string target, long maxBytes)
    {
        string? temporaryPath = null;
        try
        {
            if (string.IsNullOrWhiteSpace(target) || maxBytes <= 0)
                return 0;

            maxBytes = Math.Min(maxBytes, MaximumSnapshotBytes);
            Flush(TimeSpan.FromSeconds(2));

            var targetPath = Path.GetFullPath(target);
            var targetDirectory = Path.GetDirectoryName(targetPath);
            if (string.IsNullOrWhiteSpace(targetDirectory))
                return 0;
            Directory.CreateDirectory(targetDirectory);

            var sources = EnumerateLogFiles()
                .Where(path => !PathsEqual(path, targetPath))
                .OrderBy(SafeLastWriteUtc)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (sources.Length == 0)
                return 0;

            var retained = new Queue<SnapshotLine>();
            long retainedBytes = 0;
            foreach (var source in sources)
            {
                foreach (var line in ReadCompleteLines(source, maxBytes))
                {
                    if (!TryProjectSnapshotLine(line, out var projected))
                        continue;

                    var bytes = Utf8NoBom.GetByteCount(projected) + 1L;
                    if (bytes > maxBytes)
                        continue;

                    retained.Enqueue(new SnapshotLine(projected, bytes));
                    retainedBytes += bytes;
                    while (retainedBytes > maxBytes && retained.Count > 0)
                        retainedBytes -= retained.Dequeue().Bytes;
                }
            }

            if (retained.Count == 0)
                return 0;

            temporaryPath = targetPath + $".tmp-{Guid.NewGuid():N}";
            using (var targetStream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.Read,
                       16 * 1024,
                       FileOptions.SequentialScan))
            using (var targetWriter = new StreamWriter(targetStream, Utf8NoBom, 16 * 1024, leaveOpen: true)
                   {
                       NewLine = "\n"
                   })
            {
                foreach (var line in retained)
                    targetWriter.WriteLine(line.Text);
                targetWriter.Flush();
                targetStream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, targetPath, overwrite: true);
            temporaryPath = null;
            return SafeLength(targetPath);
        }
        catch
        {
            return 0;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temporaryPath))
            {
                try { File.Delete(temporaryPath); }
                catch { }
            }
        }
    }

    public string? CrashSnapshot(
        Exception exception,
        string component = "Program",
        string eventName = "app.unhandled_exception",
        long maxBytes = 2 * 1024 * 1024)
    {
        string? temporarySnapshot = null;
        try
        {
            Directory.CreateDirectory(_logsDirectory);
            var crashPath = Path.Combine(
                _logsDirectory,
                $"crash-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{_runShort}.jsonl");

            // Persist the facts of the crash before relying on the asynchronous writer.
            // This ordering keeps a useful artifact even if flushing or snapshotting hangs.
            var fatalEvent = CaptureEvent(
                "fatal",
                eventName,
                component,
                "failure",
                null,
                exception,
                null);
            var fatalLine = BuildLine(fatalEvent);
            if (fatalLine != null)
                AppendEmergencyLine(crashPath, fatalLine);

            var markerEvent = CaptureEvent(
                "fatal",
                "logger.crash_snapshot",
                component,
                "failure",
                new { source_event = SanitizeEnvelopeValue(eventName, "app.unhandled_exception", 96, "event") },
                exception,
                null);
            var markerLine = BuildLine(markerEvent);
            if (markerLine != null)
                AppendEmergencyLine(crashPath, markerLine);

            Write("fatal", eventName, component, "failure", exception: exception);
            Flush(TimeSpan.FromMilliseconds(1200));

            temporarySnapshot = crashPath + $".history-{Guid.NewGuid():N}.tmp";
            SnapshotRecentLogs(
                temporarySnapshot,
                Math.Clamp(maxBytes, 64 * 1024, MaximumSnapshotBytes));
            AppendFileWriteThrough(crashPath, temporarySnapshot);
            TryDelete(temporarySnapshot);
            temporarySnapshot = null;
            CleanupArchives();
            return File.Exists(crashPath) ? crashPath : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temporarySnapshot))
                TryDelete(temporarySnapshot);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;

        lock (_enqueueGate)
        {
            var stopping = CaptureEvent(
                "info",
                "logger.stopping",
                nameof(FlightRecorder),
                "success",
                null,
                null,
                null);
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _deferredStopEvent = stopping;
            _stopCompletion = completion;

            try { _queue.TryAdd(QueueItem.ForStop(stopping, completion), 500); }
            catch { }
        }

        try { _queue.CompleteAdding(); }
        catch { }

        if (_writerThread == null)
        {
            CompleteStopAfterWriterExit();
            return;
        }

        // Disposal is deliberately bounded. If storage is stalled, the background
        // writer retains ownership of the queue and HMAC key and releases them only
        // after it really exits; disposing those resources here would corrupt its tail.
        try
        {
            if (_writerThread.Join(TimeSpan.FromSeconds(3)))
                CompleteStopAfterWriterExit();
        }
        catch { }
    }

    private void WriterLoop()
    {
        try
        {
            TryOpenWriter();
            while (true)
            {
                QueueItem? item = null;
                try { _queue.TryTake(out item, 250); }
                catch { }

                if (item != null)
                    ProcessQueueItem(item);

                if (_dirty && DateTime.UtcNow - _lastBufferedFlushUtc >= TimeSpan.FromSeconds(1))
                    FlushWriter(flushToDisk: false);

                if (_queue.IsCompleted)
                    break;
            }

            FlushWriter(flushToDisk: true);
        }
        catch
        {
            // Logging must never take down the tray application.
        }
        finally
        {
            CloseWriter();
            CompleteStopAfterWriterExit();
        }
    }

    private void ProcessQueueItem(QueueItem item)
    {
        if (item.FlushCompletion != null)
        {
            var flushSucceeded = false;
            try
            {
                WriteDroppedSummaryIfNeeded();
                flushSucceeded = FlushWriter(flushToDisk: true);
            }
            catch { }
            item.FlushCompletion.TrySetResult(flushSucceeded);
            return;
        }

        if (item.Event == null)
            return;

        WriteDroppedSummaryIfNeeded();
        var line = BuildLine(item.Event);
        var succeeded = line != null && TryWriteLine(line);
        if (!succeeded)
            Interlocked.Increment(ref _droppedEvents);

        if (item.StopCompletion != null)
        {
            if (succeeded)
                Interlocked.CompareExchange(ref _deferredStopEvent, null, item.Event);
            item.StopCompletion.TrySetResult(succeeded);
        }
    }

    private void WriteDroppedSummaryIfNeeded()
    {
        var dropped = Interlocked.Exchange(ref _droppedEvents, 0);
        if (dropped <= 0)
            return;

        var line = BuildLine(CaptureEvent(
            "warn",
            "logger.events_dropped",
            nameof(FlightRecorder),
            "degraded",
            new { count = dropped },
            null,
            null));
        if (line == null || !TryWriteLine(line))
            Interlocked.Add(ref _droppedEvents, dropped);
    }

    private bool TryWriteLine(string line)
    {
        try
        {
            var lineBytes = Utf8NoBom.GetByteCount(line) + 1;
            EnsureWriterAndRotation(lineBytes);
            if (_writer == null)
                return false;

            _writer.Write(line);
            _writer.Write('\n');
            _activeBytes += lineBytes;
            _dirty = true;
            return true;
        }
        catch
        {
            CloseWriter();
            _nextOpenAttemptUtc = DateTime.UtcNow.AddSeconds(10);
            return false;
        }
    }

    private void EnsureWriterAndRotation(int nextLineBytes)
    {
        if (_writer == null)
        {
            if (DateTime.UtcNow >= _nextOpenAttemptUtc)
                TryOpenWriter();
            return;
        }

        var todayUtc = DateTime.UtcNow.Date;
        if (_activeUtcDate == todayUtc && _activeBytes + nextLineBytes <= RotationBytes)
            return;
        if (DateTime.UtcNow < _nextRotationAttemptUtc)
            return;

        FlushWriter(flushToDisk: true);
        CloseWriter();
        if (ArchiveActiveFile())
            CleanupArchives();
        TryOpenWriter();
    }

    private void TryOpenWriter()
    {
        try
        {
            Directory.CreateDirectory(_logsDirectory);
            if (_firstOpen)
            {
                _firstOpen = false;
                if (ArchiveActiveFile())
                    CleanupArchives();
            }

            _stream = new FileStream(
                _activePath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read | FileShare.Delete,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            _writer = new StreamWriter(_stream, Utf8NoBom, 16 * 1024, leaveOpen: true)
            {
                AutoFlush = false,
                NewLine = "\n"
            };
            _activeBytes = _stream.Length;
            _activeUtcDate = DateTime.UtcNow.Date;
            _lastBufferedFlushUtc = DateTime.UtcNow;
            _nextOpenAttemptUtc = DateTime.MinValue;
        }
        catch
        {
            CloseWriter();
            _nextOpenAttemptUtc = DateTime.UtcNow.AddSeconds(10);
        }
    }

    private bool FlushWriter(bool flushToDisk)
    {
        try
        {
            if (_writer == null || _stream == null)
            {
                if (DateTime.UtcNow >= _nextOpenAttemptUtc)
                    TryOpenWriter();
                return _writer != null;
            }

            _writer.Flush();
            _stream.Flush(flushToDisk);
            _dirty = false;
            _lastBufferedFlushUtc = DateTime.UtcNow;
            return true;
        }
        catch
        {
            CloseWriter();
            _nextOpenAttemptUtc = DateTime.UtcNow.AddSeconds(10);
            return false;
        }
    }

    private void CloseWriter()
    {
        try { _writer?.Dispose(); }
        catch { }
        try { _stream?.Dispose(); }
        catch { }
        _writer = null;
        _stream = null;
        _dirty = false;
    }

    private bool ArchiveActiveFile()
    {
        try
        {
            if (!File.Exists(_activePath) || SafeLength(_activePath) == 0)
            {
                ResetRotationBackoff();
                return true;
            }

            var stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture);
            string archivePath;
            do
            {
                var suffix = Interlocked.Increment(ref _archiveSequence);
                archivePath = Path.Combine(_logsDirectory, $"flight-{stamp}-{_runShort}-{suffix:D2}.jsonl");
            }
            while (File.Exists(archivePath));

            File.Move(_activePath, archivePath);
            ResetRotationBackoff();
            return true;
        }
        catch
        {
            // A third-party reader without delete sharing may temporarily block rotation.
            _rotationFailureCount = Math.Min(_rotationFailureCount + 1, 5);
            var seconds = Math.Min(60, 5 * (1 << (_rotationFailureCount - 1)));
            _nextRotationAttemptUtc = DateTime.UtcNow.AddSeconds(seconds);
            return false;
        }
    }

    private void ResetRotationBackoff()
    {
        _rotationFailureCount = 0;
        _nextRotationAttemptUtc = DateTime.MinValue;
    }

    private void CleanupArchives()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
            foreach (var file in EnumerateArchives().Where(file => SafeLastWriteUtc(file) < cutoff))
                TryDelete(file);

            var archives = EnumerateArchives()
                .OrderBy(SafeLastWriteUtc)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            long totalBytes = archives.Sum(SafeLength);
            while (archives.Count > MaximumArchiveCount || totalBytes > MaximumArchiveBytes)
            {
                var oldest = archives[0];
                archives.RemoveAt(0);
                var length = SafeLength(oldest);
                if (TryDelete(oldest))
                    totalBytes = Math.Max(0, totalBytes - length);
                else if (archives.Count == 0)
                    break;
            }
        }
        catch { }
    }

    private IEnumerable<string> EnumerateArchives()
    {
        try
        {
            if (!Directory.Exists(_logsDirectory))
                return Array.Empty<string>();
            return Directory.GetFiles(_logsDirectory, "flight-*.jsonl", SearchOption.TopDirectoryOnly)
                .Concat(Directory.GetFiles(_logsDirectory, "crash-*.jsonl", SearchOption.TopDirectoryOnly))
                .Where(path => !PathsEqual(path, _activePath))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private IEnumerable<string> EnumerateLogFiles()
    {
        try
        {
            if (!Directory.Exists(_logsDirectory))
                return Array.Empty<string>();
            return Directory.GetFiles(_logsDirectory, "flight-*.jsonl", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private IEnumerable<string> ReadCompleteLines(string path, long maximumBytes)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                16 * 1024,
                FileOptions.SequentialScan);
            if (stream.Length <= 0)
                return Array.Empty<string>();

            var readLimit = Math.Min(stream.Length, maximumBytes + MaximumEventBytes);
            var start = Math.Max(0, stream.Length - readLimit);
            stream.Position = start;
            var buffer = new byte[checked((int)readLimit)];
            var totalRead = 0;
            while (totalRead < buffer.Length)
            {
                var read = stream.Read(buffer, totalRead, buffer.Length - totalRead);
                if (read <= 0)
                    break;
                totalRead += read;
            }

            var first = 0;
            if (start > 0)
            {
                var firstNewline = Array.IndexOf(buffer, (byte)'\n', 0, totalRead);
                if (firstNewline < 0)
                    return Array.Empty<string>();
                first = firstNewline + 1;
            }

            var lastNewline = Array.LastIndexOf(buffer, (byte)'\n', Math.Max(0, totalRead - 1), totalRead);
            if (lastNewline < first)
                return Array.Empty<string>();

            var text = Utf8NoBom.GetString(buffer, first, lastNewline - first + 1);
            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd('\r'))
                .Where(line => line.Length > 0)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private bool TryProjectSnapshotLine(string line, out string projected)
    {
        projected = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("schema", out var schema)
                || !string.Equals(schema.GetString(), "sora.flight.v1", StringComparison.Ordinal))
                return false;

            projected = BuildProjectedSnapshotLine(root, includeData: true);
            if (Utf8NoBom.GetByteCount(projected) > MaximumEventBytes)
                projected = BuildProjectedSnapshotLine(root, includeData: false);
            return Utf8NoBom.GetByteCount(projected) <= MaximumEventBytes;
        }
        catch
        {
            return false;
        }
    }

    private string BuildProjectedSnapshotLine(JsonElement root, bool includeData)
    {
        var fallbackTimestamp = DateTimeOffset.UtcNow;
        using var memory = new MemoryStream(1024);
        using (var writer = new Utf8JsonWriter(memory, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", "sora.flight.v1");
            writer.WriteString(
                "ts_utc",
                ReadTimestamp(root, "ts_utc", fallbackTimestamp).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString(
                "ts_local",
                ReadTimestamp(root, "ts_local", fallbackTimestamp.ToLocalTime()).ToString("O", CultureInfo.InvariantCulture));
            writer.WriteNumber("mono_ms", ReadNonNegativeInt64(root, "mono_ms", 0));
            writer.WriteNumber("seq", Math.Max(1, ReadNonNegativeInt64(root, "seq", 1)));
            writer.WriteString("level", NormalizeLevel(ReadString(root, "level")));
            writer.WriteString(
                "event",
                SanitizeEnvelopeValue(ReadString(root, "event"), "logger.unknown", 96, "event"));

            var rawRunId = ReadString(root, "run_id");
            writer.WriteString(
                "run_id",
                Guid.TryParse(rawRunId, out var parsedRunId)
                    ? parsedRunId.ToString("D", CultureInfo.InvariantCulture)
                    : _runId);

            var operationId = ReadString(root, "op_id");
            if (string.IsNullOrWhiteSpace(operationId))
                writer.WriteNull("op_id");
            else
                writer.WriteString("op_id", SanitizeEnvelopeValue(operationId, "op", 48, "op"));

            writer.WriteString(
                "component",
                SanitizeEnvelopeValue(ReadString(root, "component"), "Unknown", 96, "component"));
            writer.WriteNumber("thread_id", Math.Max(1, ReadInt32(root, "thread_id", 1)));
            writer.WriteString(
                "app_version",
                SanitizeEnvelopeValue(ReadString(root, "app_version"), "unknown", 64, "version"));
            writer.WriteString(
                "outcome",
                SanitizeEnvelopeValue(ReadString(root, "outcome"), "unknown", 32, "outcome"));

            writer.WritePropertyName("device");
            if (root.TryGetProperty("device", out var device))
                WriteProjectedDevice(writer, device);
            else
                writer.WriteNullValue();

            writer.WritePropertyName("data");
            if (includeData && root.TryGetProperty("data", out var data))
                WriteSanitizedElement(writer, data, null, 0);
            else if (!includeData)
            {
                writer.WriteStartObject();
                writer.WriteBoolean("snapshot_truncated", true);
                writer.WriteEndObject();
            }
            else
                writer.WriteNullValue();

            writer.WritePropertyName("error");
            if (root.TryGetProperty("error", out var error))
                WriteProjectedError(writer, error);
            else
                writer.WriteNullValue();
            writer.WriteEndObject();
            writer.Flush();
        }

        return Utf8NoBom.GetString(memory.ToArray());
    }

    private void WriteProjectedDevice(Utf8JsonWriter writer, JsonElement device)
    {
        if (device.ValueKind != JsonValueKind.Object)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        var rawId = ReadString(device, "id");
        writer.WriteString("id", PreserveOpaqueTokenOrTokenize(rawId, "dev"));
        writer.WriteString("name", SanitizeString(ReadString(device, "name"), 512));
        writer.WriteString("logical_id", SanitizeString(ReadString(device, "logical_id"), 2048));
        writer.WriteString("history_key", SanitizeString(ReadString(device, "history_key"), 2048));
        writer.WriteString(
            "receiver_association_key",
            SanitizeString(ReadString(device, "receiver_association_key"), 2048));
        writer.WriteString(
            "receiver_association_serial",
            SanitizeString(ReadString(device, "receiver_association_serial"), 512));
        writer.WriteString(
            "history_anchor_evidence",
            SanitizeEnvelopeValue(ReadString(device, "history_anchor_evidence"), "none", 48, "history_anchor_evidence"));
        writer.WriteString(
            "transport",
            SanitizeEnvelopeValue(ReadString(device, "transport"), "unknown", 32, "transport"));
        writer.WriteString("path", SanitizeString(ReadString(device, "path"), 2048));
        writer.WriteStartArray("resolved_paths");
        if (device.TryGetProperty("resolved_paths", out var resolvedPaths)
            && resolvedPaths.ValueKind == JsonValueKind.Array)
        {
            foreach (var resolvedPath in resolvedPaths.EnumerateArray())
            {
                if (resolvedPath.ValueKind == JsonValueKind.String)
                    writer.WriteStringValue(SanitizeString(resolvedPath.GetString(), 2048));
            }
        }
        writer.WriteEndArray();
        writer.WriteString("serial", SanitizeString(ReadString(device, "serial"), 512));
        if (!string.IsNullOrWhiteSpace(rawId) && !IsOpaqueToken(rawId, "dev"))
            writer.WriteString("legacy_id", SanitizeString(rawId, 2048));
        writer.WriteString("vendor_id", SanitizeUsbIdentifier(ReadString(device, "vendor_id")));
        writer.WriteString("product_id", SanitizeUsbIdentifier(ReadString(device, "product_id")));
        var projectedBatteryPercentage = Math.Clamp(ReadInt32(device, "battery_percentage", 0), 0, 100);
        var projectedHasBatteryPercentage = ReadBoolean(
            device,
            "has_battery_percentage",
            projectedBatteryPercentage is >= 1 and <= 100);
        writer.WriteBoolean("has_battery_percentage", projectedHasBatteryPercentage);
        writer.WriteNumber("battery_percentage", projectedBatteryPercentage);
        WriteProjectedBooleanOrNull(writer, device, "is_online");
        WriteProjectedBooleanOrNull(writer, device, "is_charging");
        WriteProjectedBooleanOrNull(writer, device, "is_fully_charged");
        WriteProjectedBooleanOrNull(writer, device, "is_cable_connected");
        WriteProjectedBooleanOrNull(writer, device, "external_power_connected");
        if (projectedHasBatteryPercentage)
            writer.WriteString("last_successful_read_utc", SanitizeString(ReadString(device, "last_successful_read_utc"), 64));
        else
            writer.WriteNull("last_successful_read_utc");
        writer.WriteNumber("consecutive_failures", Math.Max(0, ReadInt32(device, "consecutive_failures", 0)));
        writer.WriteString(
            "power_state",
            SanitizeEnvelopeValue(ReadString(device, "power_state"), "unknown", 32, "power"));
        writer.WriteString(
            "freshness",
            SanitizeEnvelopeValue(ReadString(device, "freshness"), "unknown", 32, "freshness"));
        writer.WriteString(
            "source",
            SanitizeEnvelopeValue(ReadString(device, "source"), "unknown", 96, "source"));
        writer.WriteEndObject();
    }

    private static void WriteProjectedError(Utf8JsonWriter writer, JsonElement error)
    {
        if (error.ValueKind != JsonValueKind.Object)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        WriteOptionalSafeErrorString(writer, error, "code", 48);
        WriteOptionalSafeErrorString(writer, error, "type", 160);
        WriteOptionalDiagnosticErrorString(writer, error, "message", 1024);
        WriteOptionalSafeErrorString(writer, error, "hresult", 24);
        WriteOptionalSafeErrorString(writer, error, "stack_fp", 32);
        WriteOptionalSafeErrorString(writer, error, "inner_type", 160);
        WriteOptionalDiagnosticErrorString(writer, error, "inner_message", 1024);
        writer.WriteEndObject();
    }

    private static void WriteOptionalSafeErrorString(
        Utf8JsonWriter writer,
        JsonElement error,
        string propertyName,
        int maximumLength)
    {
        if (error.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
            writer.WriteString(propertyName, SanitizeLabel(value.GetString(), "unknown", maximumLength));
    }

    private static void WriteOptionalDiagnosticErrorString(
        Utf8JsonWriter writer,
        JsonElement error,
        string propertyName,
        int maximumLength)
    {
        if (error.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
            writer.WriteString(propertyName, SanitizeString(value.GetString(), maximumLength));
    }

    private static DateTimeOffset ReadTimestamp(
        JsonElement root,
        string propertyName,
        DateTimeOffset fallback)
    {
        return DateTimeOffset.TryParse(
            ReadString(root, propertyName),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : fallback;
    }

    private static string? ReadString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static long ReadNonNegativeInt64(JsonElement root, string propertyName, long fallback)
    {
        return root.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var parsed)
            ? Math.Max(0, parsed)
            : fallback;
    }

    private static int ReadInt32(JsonElement root, string propertyName, int fallback)
    {
        return root.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var parsed)
            ? parsed
            : fallback;
    }

    private static bool ReadBoolean(JsonElement root, string propertyName, bool fallback)
    {
        return root.TryGetProperty(propertyName, out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;
    }

    private static void WriteProjectedBooleanOrNull(
        Utf8JsonWriter writer,
        JsonElement root,
        string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            writer.WriteBoolean(propertyName, value.GetBoolean());
        else
            writer.WriteNull(propertyName);
    }

    private bool AppendEmergencyLine(string path, string line)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read | FileShare.Delete,
                4096,
                FileOptions.WriteThrough);
            var bytes = Utf8NoBom.GetBytes(line + "\n");
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
            CryptographicOperations.ZeroMemory(bytes);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void AppendFileWriteThrough(string targetPath, string sourcePath)
    {
        try
        {
            if (!File.Exists(sourcePath) || SafeLength(sourcePath) <= 0)
                return;

            using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                16 * 1024,
                FileOptions.SequentialScan);
            using var target = new FileStream(
                targetPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read | FileShare.Delete,
                16 * 1024,
                FileOptions.WriteThrough);
            source.CopyTo(target, 16 * 1024);
            target.Flush(flushToDisk: true);
        }
        catch { }
    }

    private void CompleteStopAfterWriterExit()
    {
        var stopSucceeded = false;
        try
        {
            // If the writer aborted unexpectedly, synchronously drain what remains
            // after its stream has closed. This preserves FIFO tail facts without
            // racing a live writer.
            while (Volatile.Read(ref _resourcesReleased) == 0
                   && _queue.TryTake(out var pending))
            {
                if (pending.Event != null)
                {
                    var pendingLine = BuildLine(pending.Event);
                    var persisted = pendingLine != null && AppendEmergencyLine(_activePath, pendingLine);
                    if (pending.StopCompletion != null)
                    {
                        stopSucceeded = persisted;
                        if (persisted)
                            Interlocked.CompareExchange(ref _deferredStopEvent, null, pending.Event);
                        pending.StopCompletion.TrySetResult(persisted);
                    }
                }
                pending.FlushCompletion?.TrySetResult(false);
            }

            var deferred = Interlocked.Exchange(ref _deferredStopEvent, null);
            if (deferred != null)
            {
                var line = BuildLine(deferred);
                stopSucceeded = line != null && AppendEmergencyLine(_activePath, line);
            }
            else if (_stopCompletion?.Task.IsCompletedSuccessfully == true)
                stopSucceeded = _stopCompletion.Task.Result;
            _stopCompletion?.TrySetResult(stopSucceeded);
        }
        catch
        {
            _stopCompletion?.TrySetResult(false);
        }
        finally
        {
            if (Interlocked.Exchange(ref _resourcesReleased, 1) == 0)
            {
                try { _queue.Dispose(); }
                catch { }
                try { CryptographicOperations.ZeroMemory(_installationKey); }
                catch { }
            }
        }
    }

    private PendingEvent CaptureEvent(
        string level,
        string eventName,
        string component,
        string outcome,
        object? data,
        Exception? exception,
        BatteryReading? reading)
    {
        return new PendingEvent(
            TimestampUtc: DateTimeOffset.UtcNow,
            MonotonicMilliseconds: ElapsedMilliseconds(),
            Level: level,
            EventName: eventName,
            OperationId: _operation.Value?.Id,
            Component: component,
            ThreadId: Environment.CurrentManagedThreadId,
            Outcome: outcome,
            Data: data,
            Exception: exception,
            Reading: reading);
    }

    private string? BuildLine(PendingEvent pending)
    {
        try
        {
            var sequence = Interlocked.Increment(ref _sequence);
            var line = BuildLineCore(sequence, pending, pending.Data);
            var byteCount = Utf8NoBom.GetByteCount(line);
            if (byteCount <= MaximumEventBytes)
                return line;

            return BuildLineCore(
                sequence,
                pending,
                new { truncated = true, original_bytes = byteCount });
        }
        catch
        {
            return null;
        }
    }

    private string BuildLineCore(
        long sequence,
        PendingEvent pending,
        object? data)
    {
        using var memory = new MemoryStream(1024);
        using (var writer = new Utf8JsonWriter(memory, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", "sora.flight.v1");
            writer.WriteString("ts_utc", pending.TimestampUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString("ts_local", pending.TimestampUtc.ToLocalTime().ToString("O", CultureInfo.InvariantCulture));
            writer.WriteNumber("mono_ms", Math.Max(0, pending.MonotonicMilliseconds));
            writer.WriteNumber("seq", sequence);
            writer.WriteString("level", NormalizeLevel(pending.Level));
            writer.WriteString("event", SanitizeEnvelopeValue(pending.EventName, "logger.unknown", 96, "event"));
            writer.WriteString("run_id", _runId);
            if (!string.IsNullOrWhiteSpace(pending.OperationId))
                writer.WriteString("op_id", SanitizeEnvelopeValue(pending.OperationId, "op", 48, "op"));
            else
                writer.WriteNull("op_id");
            writer.WriteString("component", SanitizeEnvelopeValue(pending.Component, "Unknown", 96, "component"));
            writer.WriteNumber("thread_id", Math.Max(1, pending.ThreadId));
            writer.WriteString("app_version", _appVersion);
            writer.WriteString("outcome", SanitizeEnvelopeValue(pending.Outcome, "unknown", 32, "outcome"));

            writer.WritePropertyName("device");
            WriteDevice(writer, pending.Reading);
            writer.WritePropertyName("data");
            WriteData(writer, data);
            writer.WritePropertyName("error");
            WriteError(writer, pending.Exception);
            writer.WriteEndObject();
            writer.Flush();
        }

        return Utf8NoBom.GetString(memory.ToArray());
    }

    private void WriteDevice(Utf8JsonWriter writer, BatteryReading? reading)
    {
        if (reading == null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("id", DeviceToken(reading));
        writer.WriteString("name", SanitizeString(reading.DeviceName, 512));
        writer.WriteString("logical_id", SanitizeString(reading.LogicalDeviceId, 2048));
        writer.WriteString("history_key", SanitizeString(reading.HistoryDeviceKey, 2048));
        writer.WriteString("receiver_association_key", SanitizeString(reading.AssociationReceiverHistoryKey, 2048));
        writer.WriteString("receiver_association_serial", SanitizeString(reading.AssociationReceiverSerial, 512));
        writer.WriteString("history_anchor_evidence", reading.HistoryAnchorEvidence.ToString().ToLowerInvariant());
        writer.WriteString("transport", reading.ConnectionTransport.ToString().ToLowerInvariant());
        writer.WriteString("path", SanitizeString(reading.DeviceId, 2048));
        writer.WriteStartArray("resolved_paths");
        foreach (var deviceId in DeviceIdentity.ResolvedDeviceIds(reading))
            writer.WriteStringValue(SanitizeString(deviceId, 2048));
        writer.WriteEndArray();
        writer.WriteString("serial", SanitizeString(reading.DeviceSerial, 512));
        writer.WriteString("vendor_id", SanitizeUsbIdentifier(reading.VendorId));
        writer.WriteString("product_id", SanitizeUsbIdentifier(reading.ProductId));
        writer.WriteBoolean("has_battery_percentage", reading.HasBatteryPercentage);
        writer.WriteNumber("battery_percentage", Math.Clamp(reading.BatteryPercentage, 0, 100));
        writer.WriteBoolean("is_online", reading.IsOnline);
        writer.WriteBoolean("is_charging", reading.IsCharging);
        writer.WriteBoolean("is_fully_charged", reading.IsFullyCharged);
        writer.WriteBoolean("is_cable_connected", reading.IsCableConnected);
        if (reading.ExternalPowerConnected.HasValue)
            writer.WriteBoolean("external_power_connected", reading.ExternalPowerConnected.Value);
        else
            writer.WriteNull("external_power_connected");
        if (reading.HasBatteryPercentage)
            writer.WriteString("last_successful_read_utc", reading.LastSuccessfulReadUtc.ToUniversalTime());
        else
            writer.WriteNull("last_successful_read_utc");
        writer.WriteNumber("consecutive_failures", Math.Max(0, reading.ConsecutiveFailures));
        writer.WriteString("power_state", reading.PowerState.ToString().ToLowerInvariant());
        writer.WriteString("freshness", reading.Freshness.ToString().ToLowerInvariant());
        writer.WriteString("source", SanitizeEnvelopeValue(reading.Source, "unknown", 96, "source"));
        writer.WriteEndObject();
    }

    private void WriteData(Utf8JsonWriter writer, object? data)
    {
        if (data == null)
        {
            writer.WriteNullValue();
            return;
        }

        try
        {
            if (data is byte[])
            {
                writer.WriteStringValue("[binary-redacted]");
                return;
            }

            var element = JsonSerializer.SerializeToElement(data, data.GetType(), DataJsonOptions);
            WriteSanitizedElement(writer, element, null, 0);
        }
        catch
        {
            writer.WriteStartObject();
            writer.WriteString("serialization", "failed");
            writer.WriteEndObject();
        }
    }

    private void WriteSanitizedElement(
        Utf8JsonWriter writer,
        JsonElement element,
        string? propertyName,
        int depth)
    {
        if (depth > MaximumJsonDepth)
        {
            writer.WriteStringValue("[depth-limit]");
            return;
        }

        var classification = ClassifyProperty(propertyName);
        if (classification == SensitiveProperty.Secret)
        {
            writer.WriteStringValue("[redacted]");
            return;
        }
        if (classification == SensitiveProperty.RawIo)
        {
            writer.WriteStringValue("[binary-redacted]");
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var propertyCount = 0;
                foreach (var property in element.EnumerateObject())
                {
                    if (propertyCount++ >= MaximumObjectProperties)
                    {
                        writer.WriteBoolean("_truncated", true);
                        break;
                    }

                    var name = LooksLikeSensitiveValue(property.Name)
                        ? TokenFor(property.Name, "field")
                        : SanitizePropertyName(property.Name);
                    writer.WritePropertyName(name);
                    WriteSanitizedElement(writer, property.Value, name, depth + 1);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                var itemCount = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (itemCount++ >= MaximumArrayLength)
                        break;
                    WriteSanitizedElement(writer, item, propertyName, depth + 1);
                }
                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                var value = element.GetString() ?? string.Empty;
                var stringLimit = classification switch
                {
                    SensitiveProperty.Path or SensitiveProperty.DeviceIdentity => 2048,
                    SensitiveProperty.Serial => 512,
                    _ => MaximumStringLength
                };
                writer.WriteStringValue(SanitizeString(value, stringLimit));
                break;

            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                element.WriteTo(writer);
                break;

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
            default:
                writer.WriteNullValue();
                break;
        }
    }

    private void WriteTokenizedValue(
        Utf8JsonWriter writer,
        JsonElement element,
        SensitiveProperty classification)
    {
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            writer.WriteNullValue();
            return;
        }

        var prefix = classification switch
        {
            SensitiveProperty.Path => "path",
            SensitiveProperty.Serial => "ser",
            SensitiveProperty.DeviceIdentity => "dev",
            _ => "tok"
        };

        if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            var count = 0;
            foreach (var item in element.EnumerateArray())
            {
                if (count++ >= MaximumArrayLength)
                    break;
                var raw = item.ValueKind == JsonValueKind.String ? item.GetString() : item.GetRawText();
                writer.WriteStringValue(PreserveOpaqueTokenOrTokenize(raw, prefix));
            }
            writer.WriteEndArray();
            return;
        }

        var value = element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
        writer.WriteStringValue(PreserveOpaqueTokenOrTokenize(value, prefix));
    }

    private string PreserveOpaqueTokenOrTokenize(string? value, string prefix)
    {
        var raw = value ?? string.Empty;
        return IsOpaqueToken(raw, prefix) ? raw : TokenFor(raw, prefix);
    }

    private static bool IsOpaqueToken(string value, string prefix)
    {
        var marker = prefix + "_";
        if (!value.StartsWith(marker, StringComparison.Ordinal))
            return false;

        var suffix = value[marker.Length..];
        if (suffix is "none" or "unavailable")
            return true;
        return suffix.Length == 24
            && suffix.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static void WriteError(Utf8JsonWriter writer, Exception? exception)
    {
        if (exception == null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("code", ErrorCode(exception));
        writer.WriteString("type", SanitizeString(exception.GetType().FullName, 160));
        writer.WriteString("message", SanitizeString(exception.Message, 1024));
        writer.WriteString("hresult", $"0x{unchecked((uint)exception.HResult):X8}");
        writer.WriteString("stack_fp", StackFingerprint(exception));
        if (exception.InnerException != null)
        {
            writer.WriteString("inner_type", SanitizeString(exception.InnerException.GetType().FullName, 160));
            writer.WriteString("inner_message", SanitizeString(exception.InnerException.Message, 1024));
        }
        writer.WriteEndObject();
    }

    private static SensitiveProperty ClassifyProperty(string? propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
            return SensitiveProperty.None;

        var compact = new string(propertyName.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (compact is "deviceid" or "deviceids" or "devicekey" or "devicekeys"
            or "candidateid" or "candidateids" or "candidatedeviceid" or "candidatedeviceids"
            or "devicetoken" or "devicetokens")
            return SensitiveProperty.DeviceIdentity;
        if (compact.Contains("serial", StringComparison.Ordinal))
            return SensitiveProperty.Serial;
        if (compact.Contains("path", StringComparison.Ordinal)
            || compact.Contains("directory", StringComparison.Ordinal)
            || compact is "folder" or "filelocation")
            return SensitiveProperty.Path;
        if (compact is "bytes" or "hex" or "buffer" or "payload" or "request" or "response"
            || compact.EndsWith("bytes", StringComparison.Ordinal)
            || compact.EndsWith("hex", StringComparison.Ordinal)
            || compact.EndsWith("buffer", StringComparison.Ordinal)
            || compact.Contains("rawreport", StringComparison.Ordinal))
            return SensitiveProperty.RawIo;
        if (compact.Contains("password", StringComparison.Ordinal)
            || compact.Contains("passwd", StringComparison.Ordinal)
            || compact.Contains("secret", StringComparison.Ordinal)
            || compact.Contains("apikey", StringComparison.Ordinal)
            || compact.Contains("authorization", StringComparison.Ordinal)
            || compact.Contains("cookie", StringComparison.Ordinal)
            || compact is "tokenvalue"
            || compact.EndsWith("token", StringComparison.Ordinal))
            return SensitiveProperty.Secret;
        return SensitiveProperty.None;
    }

    private string SanitizeEnvelopeValue(string? value, string fallback, int maximumLength, string tokenPrefix)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(value) && LooksLikeSensitiveValue(value))
                return TokenFor(value, tokenPrefix);
        }
        catch { }
        return SanitizeLabel(value, fallback, maximumLength);
    }

    private static bool LooksLikeSensitiveValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Contains(@":\", StringComparison.Ordinal)
            || value.Contains(":/", StringComparison.Ordinal)
            || value.StartsWith(@"\\", StringComparison.Ordinal)
            || value.StartsWith("/home/", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/Users/", StringComparison.OrdinalIgnoreCase)
            || value.Contains("hid#", StringComparison.OrdinalIgnoreCase)
            || value.Contains("usb#", StringComparison.OrdinalIgnoreCase)
            || (value.Contains("vid_", StringComparison.OrdinalIgnoreCase)
                && value.Contains("pid_", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeLevel(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "debug" => "debug",
            "info" => "info",
            "warn" or "warning" => "warn",
            "error" => "error",
            "fatal" => "fatal",
            _ => "info"
        };
    }

    private static string SanitizeLabel(string? value, string fallback, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        var builder = new StringBuilder(Math.Min(value.Length, maximumLength));
        foreach (var character in value.Trim())
        {
            if (builder.Length >= maximumLength)
                break;
            builder.Append(char.IsLetterOrDigit(character) || character is '.' or '_' or '-' ? character : '_');
        }

        return builder.Length == 0 ? fallback : builder.ToString();
    }

    private static string SanitizePropertyName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "field";

        var sanitized = SanitizeLabel(value, "field", 64);
        return sanitized.StartsWith('_') ? $"field{sanitized}" : sanitized;
    }

    private static string SanitizeString(string? value, int maximumLength)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var builder = new StringBuilder(Math.Min(value.Length, maximumLength));
        foreach (var character in value)
        {
            if (builder.Length >= maximumLength)
                break;
            builder.Append(char.IsControl(character) ? ' ' : character);
        }
        return builder.ToString();
    }

    private static string SanitizeUsbIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Trim();
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[2..];
        return int.TryParse(normalized, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed)
            ? $"0x{parsed:X4}"
            : string.Empty;
    }

    private static string ErrorCode(Exception exception)
    {
        return exception switch
        {
            OperationCanceledException => "cancelled",
            TimeoutException => "timeout",
            UnauthorizedAccessException => "access_denied",
            JsonException => "invalid_json",
            IOException => "io_error",
            CryptographicException => "crypto_error",
            _ => "unhandled_exception"
        };
    }

    private static string StackFingerprint(Exception exception)
    {
        try
        {
            var source = exception.StackTrace
                ?? exception.TargetSite?.ToString()
                ?? exception.GetType().FullName
                ?? "unknown";
            var digest = SHA256.HashData(Utf8NoBom.GetBytes(source));
            return Convert.ToHexString(digest.AsSpan(0, 8));
        }
        catch
        {
            return string.Empty;
        }
    }

    private long ElapsedMilliseconds()
    {
        try
        {
            var elapsedTicks = Stopwatch.GetTimestamp() - _startedTimestamp;
            return (long)(elapsedTicks * 1000d / Stopwatch.Frequency);
        }
        catch
        {
            return 0;
        }
    }

    private static int ClampTimeoutMilliseconds(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
            return 0;
        return (int)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue);
    }

    private static string ResolveLogsDirectory(string? requested)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(requested))
                return Path.GetFullPath(requested);

            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(local))
                return Path.Combine(local, "SoraV2BatteryTip", "logs");
        }
        catch { }

        try { return Path.Combine(Path.GetTempPath(), "SoraV2BatteryTip", "logs"); }
        catch { return Path.Combine(AppContext.BaseDirectory, "logs"); }
    }

    private static string ReadAppVersion()
    {
        try
        {
            return Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
                ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
                ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    private static bool IsPreviousRunUnclean(string activePath)
    {
        try
        {
            if (!File.Exists(activePath) || new FileInfo(activePath).Length == 0)
                return false;

            using var stream = new FileStream(activePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: true);
            string? latestRunId = null;
            var latestRunWasClean = false;
            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object
                        || !root.TryGetProperty("schema", out var schema)
                        || !string.Equals(schema.GetString(), "sora.flight.v1", StringComparison.Ordinal)
                        || !root.TryGetProperty("run_id", out var runProperty)
                        || runProperty.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(runProperty.GetString()))
                        continue;

                    var runId = runProperty.GetString();
                    if (!string.Equals(latestRunId, runId, StringComparison.Ordinal))
                    {
                        latestRunId = runId;
                        latestRunWasClean = false;
                    }

                    if (root.TryGetProperty("event", out var eventProperty)
                        && string.Equals(
                            eventProperty.GetString(),
                            "app.clean_shutdown",
                            StringComparison.Ordinal))
                        latestRunWasClean = true;
                }
                catch
                {
                    // Ignore incomplete or forged individual records and keep scanning.
                }
            }
            if (latestRunId == null)
                return false;
            return !latestRunWasClean;
        }
        catch
        {
            return true;
        }
    }

    private static string ResolveKeyPath(string logsDirectory, string? requested)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(requested))
                return Path.GetFullPath(requested);

            var parent = Directory.GetParent(logsDirectory)?.FullName;
            if (!string.IsNullOrWhiteSpace(parent))
                return Path.Combine(parent, ".flight-recorder.key");
        }
        catch { }

        return Path.Combine(logsDirectory, "..", ".flight-recorder.key");
    }

    private static byte[] LoadOrCreateInstallationKey(
        string keyPath,
        string legacyKeyPath,
        string runId)
    {
        try
        {
            var directory = Path.GetDirectoryName(keyPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var existing = TryReadInstallationKey(keyPath);
            if (existing != null)
            {
                if (!PathsEqual(keyPath, legacyKeyPath))
                    TryDelete(legacyKeyPath);
                return existing;
            }

            if (!PathsEqual(keyPath, legacyKeyPath))
            {
                var legacy = TryReadInstallationKey(legacyKeyPath);
                if (legacy != null)
                {
                    var migrated = TryPersistOrReadInstallationKey(keyPath, legacy);
                    if (migrated != null)
                    {
                        if (!ReferenceEquals(migrated, legacy))
                            CryptographicOperations.ZeroMemory(legacy);
                        TryDelete(legacyKeyPath);
                        return migrated;
                    }

                    // Preserve stable device correlation if migration is temporarily
                    // blocked. The next launch will retry moving the legacy key.
                    return legacy;
                }
            }

            var generated = RandomNumberGenerator.GetBytes(32);
            var persisted = TryPersistOrReadInstallationKey(keyPath, generated);
            if (persisted != null)
            {
                if (!ReferenceEquals(persisted, generated))
                    CryptographicOperations.ZeroMemory(generated);
                return persisted;
            }
            return generated;
        }
        catch
        {
            try { return SHA256.HashData(Utf8NoBom.GetBytes(runId)); }
            catch { return new byte[32]; }
        }
    }

    private static byte[]? TryPersistOrReadInstallationKey(string keyPath, byte[] candidate)
    {
        try
        {
            using var stream = new FileStream(keyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(candidate, 0, candidate.Length);
            stream.Flush(flushToDisk: true);
            return candidate;
        }
        catch
        {
            return TryReadInstallationKey(keyPath);
        }
    }

    private static byte[]? TryReadInstallationKey(string keyPath)
    {
        try
        {
            if (!File.Exists(keyPath))
                return null;
            var bytes = File.ReadAllBytes(keyPath);
            if (bytes.Length < 32)
                return null;
            if (bytes.Length == 32)
                return bytes;

            var key = bytes.AsSpan(0, 32).ToArray();
            CryptographicOperations.ZeroMemory(bytes);
            return key;
        }
        catch
        {
            return null;
        }
    }

    private static DateTime SafeLastWriteUtc(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return !File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void RestoreOperation(OperationContext current, OperationContext? previous)
    {
        try
        {
            if (ReferenceEquals(_operation.Value, current))
                _operation.Value = previous;
        }
        catch { }
    }

    private sealed class OperationScope : IDisposable
    {
        private readonly FlightRecorder _owner;
        private readonly OperationContext _current;
        private readonly OperationContext? _previous;
        private int _disposed;

        public OperationScope(FlightRecorder owner, OperationContext current, OperationContext? previous)
        {
            _owner = owner;
            _current = current;
            _previous = previous;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _owner.RestoreOperation(_current, _previous);
        }
    }

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();
        public void Dispose() { }
    }

    private sealed class OperationContext
    {
        public OperationContext(string id) => Id = id;
        public string Id { get; }
    }

    private sealed record PendingEvent(
        DateTimeOffset TimestampUtc,
        long MonotonicMilliseconds,
        string Level,
        string EventName,
        string? OperationId,
        string Component,
        int ThreadId,
        string Outcome,
        object? Data,
        Exception? Exception,
        BatteryReading? Reading);

    private sealed class QueueItem
    {
        private QueueItem(
            PendingEvent? pendingEvent,
            TaskCompletionSource<bool>? flushCompletion,
            TaskCompletionSource<bool>? stopCompletion)
        {
            Event = pendingEvent;
            FlushCompletion = flushCompletion;
            StopCompletion = stopCompletion;
        }

        public PendingEvent? Event { get; }
        public TaskCompletionSource<bool>? FlushCompletion { get; }
        public TaskCompletionSource<bool>? StopCompletion { get; }

        public static QueueItem ForEvent(PendingEvent pendingEvent) => new(pendingEvent, null, null);
        public static QueueItem ForStop(PendingEvent pendingEvent, TaskCompletionSource<bool> completion) =>
            new(pendingEvent, null, completion);
        public static QueueItem ForFlush(TaskCompletionSource<bool> completion) => new(null, completion, null);
    }

    private readonly record struct SnapshotLine(string Text, long Bytes);

    private enum SensitiveProperty
    {
        None,
        Secret,
        Path,
        Serial,
        DeviceIdentity,
        RawIo
    }
}
