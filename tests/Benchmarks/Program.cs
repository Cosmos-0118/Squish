using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Squish.BackgroundService.Automation;
using Squish.BackgroundService.Clipboard;
using Squish.Benchmarks;
using Squish.Benchmarks.Legacy;
using Squish.Core.Optimizers;
using Squish.Core.Shared;

// Usage: Squish.Benchmarks [folder|clipboard|all]
var suite = args.Length > 0 ? args[0].ToLowerInvariant() : "all";
Console.WriteLine($"Squish watcher benchmarks  |  {Environment.OSVersion}  |  {Environment.ProcessorCount} logical cores  |  .NET {Environment.Version}");
Console.WriteLine("(\"legacy\" = implementation at the previous commit, kept verbatim in tests/Benchmarks/Legacy)");
Console.WriteLine();

if (suite == "quick")
{
    await FolderBench.QuickAsync();
}

if (suite is "folder" or "all")
{
    await FolderBench.RunAsync();
}

if (suite is "clipboard" or "all" or "quick")
{
    await ClipboardBench.RunAsync();
}

namespace Squish.Benchmarks
{
    internal static class Stats
    {
        public static double Median(IReadOnlyList<double> values) => Percentile(values, 0.5);

        public static double Percentile(IReadOnlyList<double> values, double p)
        {
            if (values.Count == 0)
            {
                return double.NaN;
            }

            var sorted = values.OrderBy(v => v).ToArray();
            var index = (int)Math.Ceiling(p * sorted.Length) - 1;
            return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
        }

        public static string Ms(double value) => double.IsNaN(value) ? "n/a" : value.ToString("0", CultureInfo.InvariantCulture) + " ms";

        public static void Row(string metric, string legacy, string current) =>
            Console.WriteLine($"  {metric,-46} {legacy,18} {current,18}");

        public static void Header(string title)
        {
            Console.WriteLine();
            Console.WriteLine(title);
            Console.WriteLine($"  {"",-46} {"legacy",18} {"current",18}");
        }
    }

    internal sealed class StampingOptimiser : IOptimiser
    {
        public ItemType ItemType => ItemType.Image;

        public TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(10);

        public ConcurrentDictionary<string, long> Seen { get; } = new(StringComparer.OrdinalIgnoreCase);

        public async Task<OptimisationResult> OptimiseAsync(OptimisationRequest request, OptimiserExecutionContext context, CancellationToken cancellationToken)
        {
            Seen.TryAdd(request.SourcePath.Value, Stopwatch.GetTimestamp());
            await Task.Delay(Delay, cancellationToken);
            return OptimisationResult.Success(request.RequestId, request.SourcePath);
        }
    }

    internal sealed class BenchFolderSettings : IFolderWatchSettings
    {
        public BenchFolderSettings(string images, int jobs)
        {
            Current = new FolderWatchConfig(false, true, false, new[] { images }, Array.Empty<string>(),
                new HashSet<string>(), new HashSet<string>(), 50, 500, jobs, 1);
        }

        public FolderWatchConfig Current { get; }

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public void ApplyRequestMetadata(ItemType type, FilePath path, IDictionary<string, object?> metadata)
        {
        }
    }

    /// <summary>One watcher implementation wired to a fake optimiser over a scratch directory.</summary>
    internal sealed class FolderRig : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly OptimisationCoordinator _coordinator;
        private readonly Task _run;
        private readonly Func<ValueTask> _dispose;

        public string Root { get; }

        public string Images { get; }

        public StampingOptimiser Optimiser { get; } = new();

        public Func<string> Describe { get; private set; } = () => string.Empty;

        public FolderRig(bool legacy, int jobs = 4)
        {
            Root = Directory.CreateTempSubdirectory("squish-bench-").FullName;
            Images = Path.Combine(Root, "images");
            Directory.CreateDirectory(Images);
            _coordinator = new OptimisationCoordinator(new IOptimiser[] { Optimiser }, degreeOfParallelism: 4);

            if (legacy)
            {
                var service = new LegacyDirectoryOptimisationService(
                    _coordinator,
                    new LegacyOptimisedFileRegistry(),
                    NullLogger.Instance,
                    new LegacySettings { ImageDirs = new[] { Images }, MaxImageFileCount = jobs });
                _run = Task.Run(() => service.RunAsync(_cts.Token));
                _dispose = service.DisposeAsync;
            }
            else
            {
                var service = new DirectoryOptimisationService(
                    _coordinator,
                    new OptimisedFileRegistry(),
                    NullLogger<DirectoryOptimisationService>.Instance,
                    new BenchFolderSettings(Images, jobs),
                    FolderWatchOptions.Default,
                    TimeProvider.System);
                _run = Task.Run(() => service.RunAsync(_cts.Token));
                _dispose = service.DisposeAsync;
                Describe = () => service.Stats.ToString();
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try
            {
                await _run.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch
            {
            }

            await _dispose();
            await _coordinator.DisposeAsync();
            try
            {
                Directory.Delete(Root, true);
            }
            catch
            {
            }
        }
    }

    internal static class FolderBench
    {
        private static readonly byte[] Payload = Enumerable.Range(0, 2048).Select(i => (byte)(i % 251)).ToArray();

        public static async Task QuickAsync()
        {
            var legacy = await Latency(true);
            var current = await Latency(false);
            Stats.Header("New image appears in a watched folder (single file, idle system)");
            Stats.Row("time until optimiser starts, median", Stats.Ms(Stats.Median(legacy)), Stats.Ms(Stats.Median(current)));
            Stats.Row("time until optimiser starts, p95", Stats.Ms(Stats.Percentile(legacy, 0.95)), Stats.Ms(Stats.Percentile(current, 0.95)));
        }

        public static async Task RunAsync()
        {
            Console.WriteLine("=== FOLDER WATCHER ===");
            var latency = new Dictionary<bool, List<double>>();
            var burst = new Dictionary<bool, (int Done, double Seconds)>();
            var hol = new Dictionary<bool, (int Done, double Seconds)>();
            var idle = new Dictionary<bool, double>();
            var recovery = new Dictionary<bool, string>();
            var cpuBurst = new Dictionary<bool, double>();

            foreach (var legacy in new[] { true, false })
            {
                Console.Error.WriteLine($"[folder] {(legacy ? "legacy" : "current")}: latency");
                latency[legacy] = await Latency(legacy);
                Console.Error.WriteLine($"[folder] {(legacy ? "legacy" : "current")}: burst");
                (burst[legacy], cpuBurst[legacy]) = await Burst(legacy, 1000);
                Console.Error.WriteLine($"[folder] {(legacy ? "legacy" : "current")}: head-of-line");
                hol[legacy] = await HeadOfLine(legacy, 50);
                Console.Error.WriteLine($"[folder] {(legacy ? "legacy" : "current")}: idle");
                idle[legacy] = await IdleCpu(legacy, TimeSpan.FromSeconds(10));
                Console.Error.WriteLine($"[folder] {(legacy ? "legacy" : "current")}: recovery");
                recovery[legacy] = await Recovery(legacy);
            }

            Stats.Header("New image appears in a watched folder (single file, idle system)");
            Stats.Row("time until optimiser starts, median", Stats.Ms(Stats.Median(latency[true])), Stats.Ms(Stats.Median(latency[false])));
            Stats.Row("time until optimiser starts, p95", Stats.Ms(Stats.Percentile(latency[true], 0.95)), Stats.Ms(Stats.Percentile(latency[false], 0.95)));

            Stats.Header("Burst: 1000 files written back-to-back (default 4 concurrent jobs)");
            Stats.Row("files optimised", $"{burst[true].Done} / 1000", $"{burst[false].Done} / 1000");
            Stats.Row("time to finish (60 s cap)", $"{burst[true].Seconds:0.0} s", $"{burst[false].Seconds:0.0} s");
            Stats.Row("CPU time spent by this process", $"{cpuBurst[true]:0} ms", $"{cpuBurst[false]:0} ms");

            Stats.Header("Head-of-line: one file held open by a writer + 50 normal files");
            Stats.Row("normal files optimised (60 s cap)", $"{hol[true].Done} / 50", $"{hol[false].Done} / 50");
            Stats.Row("time until all normal files done", hol[true].Done == 50 ? $"{hol[true].Seconds:0.0} s" : "never (blocked)", hol[false].Done == 50 ? $"{hol[false].Seconds:0.0} s" : "never");

            Stats.Header("Idle cost (10 s with one watched folder, nothing happening)");
            Stats.Row("CPU time", $"{idle[true]:0.0} ms", $"{idle[false]:0.0} ms");

            Stats.Header("Watched folder deleted and recreated, then a file is dropped in");
            Stats.Row("outcome", recovery[true], recovery[false]);
        }

        private static async Task<List<double>> Latency(bool legacy)
        {
            await using var rig = new FolderRig(legacy);
            await Task.Delay(500);
            var results = new List<double>();
            for (var i = 0; i < 15; i++)
            {
                var path = Path.Combine(rig.Images, $"lat-{i}.png");
                var created = Stopwatch.GetTimestamp();
                await File.WriteAllBytesAsync(path, Payload);
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!rig.Optimiser.Seen.ContainsKey(path) && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(1);
                }

                if (rig.Optimiser.Seen.TryGetValue(path, out var seen))
                {
                    results.Add(Stopwatch.GetElapsedTime(created, seen).TotalMilliseconds);
                }

                await Task.Delay(300);
            }

            return results;
        }

        private static async Task<((int Done, double Seconds), double Cpu)> Burst(bool legacy, int count)
        {
            await using var rig = new FolderRig(legacy);
            rig.Optimiser.Delay = TimeSpan.FromMilliseconds(10);
            await Task.Delay(500);

            var cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < count; i++)
            {
                File.WriteAllBytes(Path.Combine(rig.Images, $"b-{i:D5}.png"), Payload);
            }

            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (rig.Optimiser.Seen.Count < count && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            sw.Stop();
            var cpu = (Process.GetCurrentProcess().TotalProcessorTime - cpu0).TotalMilliseconds;
            if (!legacy)
            {
                Console.Error.WriteLine($"[folder] current burst stats: {rig.Describe()}");
            }

            return ((rig.Optimiser.Seen.Count, sw.Elapsed.TotalSeconds), cpu);
        }

        private static async Task<(int Done, double Seconds)> HeadOfLine(bool legacy, int others)
        {
            await using var rig = new FolderRig(legacy);
            await Task.Delay(500);

            var busy = new FileStream(Path.Combine(rig.Images, "still-recording.png"), FileMode.Create, FileAccess.Write, FileShare.Read);
            try
            {
                busy.Write(Payload);
                busy.Flush();
                await Task.Delay(100);

                var sw = Stopwatch.StartNew();
                var paths = new List<string>();
                for (var i = 0; i < others; i++)
                {
                    var path = Path.Combine(rig.Images, $"ok-{i}.png");
                    paths.Add(path);
                    await File.WriteAllBytesAsync(path, Payload);
                }

                var deadline = DateTime.UtcNow.AddSeconds(60);
                while (paths.Any(p => !rig.Optimiser.Seen.ContainsKey(p)) && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(20);
                }

                sw.Stop();
                return (paths.Count(p => rig.Optimiser.Seen.ContainsKey(p)), sw.Elapsed.TotalSeconds);
            }
            finally
            {
                busy.Dispose();
            }
        }

        private static async Task<double> IdleCpu(bool legacy, TimeSpan duration)
        {
            await using var rig = new FolderRig(legacy);
            await Task.Delay(1000);
            var cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
            await Task.Delay(duration);
            return (Process.GetCurrentProcess().TotalProcessorTime - cpu0).TotalMilliseconds;
        }

        private static async Task<string> Recovery(bool legacy)
        {
            await using var rig = new FolderRig(legacy);
            await Task.Delay(500);

            Directory.Delete(rig.Images, true);
            await Task.Delay(1000);
            Directory.CreateDirectory(rig.Images);
            await Task.Delay(2500);

            var path = Path.Combine(rig.Images, "after-restore.png");
            var sw = Stopwatch.StartNew();
            await File.WriteAllBytesAsync(path, Payload);
            var deadline = DateTime.UtcNow.AddSeconds(35);
            while (!rig.Optimiser.Seen.ContainsKey(path) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            return rig.Optimiser.Seen.ContainsKey(path) ? $"recovered ({sw.Elapsed.TotalSeconds:0.0} s)" : "watch dead, file ignored";
        }
    }

    /// <summary>Drives the clipboard from a dedicated STA thread, like another application would.</summary>
    internal sealed class ClipboardWriter : IDisposable
    {
        private readonly BlockingCollection<(Action Work, TaskCompletionSource Done)> _queue = new();
        private readonly Thread _thread;

        public ClipboardWriter()
        {
            _thread = new Thread(() =>
            {
                foreach (var (work, done) in _queue.GetConsumingEnumerable())
                {
                    try
                    {
                        work();
                        done.SetResult();
                    }
                    catch (Exception ex)
                    {
                        done.SetException(ex);
                    }
                }
            })
            { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        public Task RunAsync(Action work)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add((work, tcs));
            return tcs.Task;
        }

        public void Dispose() => _queue.CompleteAdding();
    }

    internal interface IMonitorRig : IDisposable
    {
        event EventHandler<ClipboardSnapshot>? Changed;

        void Start();
    }

    internal sealed class CurrentRig : IMonitorRig
    {
        private readonly ClipboardMonitor _monitor = new(NullLogger<ClipboardMonitor>.Instance);

        public event EventHandler<ClipboardSnapshot>? Changed
        {
            add => _monitor.ClipboardChanged += value;
            remove => _monitor.ClipboardChanged -= value;
        }

        public void Start() => _monitor.Start();

        public ClipboardMonitorStats Stats => _monitor.Stats;

        public void Dispose() => _monitor.Dispose();
    }

    internal sealed class LegacyRig : IMonitorRig
    {
        private readonly LegacyClipboardMonitor _monitor = new(NullLogger.Instance);

        public event EventHandler<ClipboardSnapshot>? Changed
        {
            add => _monitor.ClipboardChanged += value;
            remove => _monitor.ClipboardChanged -= value;
        }

        public void Start() => _monitor.Start();

        public void Dispose() => _monitor.Dispose();
    }

    internal static class ClipboardBench
    {
        public static async Task RunAsync()
        {
            Console.WriteLine();
            Console.WriteLine("=== CLIPBOARD WATCHER ===");
            Console.WriteLine("(overwrites the clipboard contents while running)");
            using var writer = new ClipboardWriter();

            var text = new Dictionary<bool, List<double>>();
            var idle = new Dictionary<bool, double>();
            var image = new Dictionary<bool, List<double>>();
            var imagePng = new Dictionary<bool, List<double>>();
            var burst = new Dictionary<bool, (int Events, bool LastSeen)>();

            foreach (var legacy in new[] { true, false })
            {
                Console.Error.WriteLine($"[clipboard] {(legacy ? "legacy" : "current")}");
                text[legacy] = await TextLatency(legacy, writer, 40);
                burst[legacy] = await Burst(legacy, writer, 50);
                image[legacy] = await ImageLatency(legacy, writer, withPng: false, runs: 6);
                imagePng[legacy] = await ImageLatency(legacy, writer, withPng: true, runs: 6);
                idle[legacy] = await IdleCpu(legacy, TimeSpan.FromSeconds(10));
            }

            Stats.Header("Text copied by another application (40 copies, 150 ms apart)");
            Stats.Row("copy -> snapshot delivered, median", Stats.Ms(Stats.Median(text[true])), Stats.Ms(Stats.Median(text[false])));
            Stats.Row("copy -> snapshot delivered, p95", Stats.Ms(Stats.Percentile(text[true], 0.95)), Stats.Ms(Stats.Percentile(text[false], 0.95)));
            Stats.Row("copy -> snapshot delivered, worst", Stats.Ms(text[true].DefaultIfEmpty(double.NaN).Max()), Stats.Ms(text[false].DefaultIfEmpty(double.NaN).Max()));
            Stats.Row("copies reported / sent", $"{text[true].Count} / 40", $"{text[false].Count} / 40");

            Stats.Header("50 copies in a tight loop");
            Stats.Row("snapshots delivered", $"{burst[true].Events}", $"{burst[false].Events}");
            Stats.Row("final value delivered", burst[true].LastSeen ? "yes" : "NO", burst[false].LastSeen ? "yes" : "NO");

            Stats.Header("4K screenshot (3840x2160) copied as a bitmap");
            Stats.Row("copy -> snapshot delivered, median", Stats.Ms(Stats.Median(image[true])), Stats.Ms(Stats.Median(image[false])));

            Stats.Header("4K image copied with PNG + bitmap formats (browsers, editors)");
            Stats.Row("copy -> snapshot delivered, median", Stats.Ms(Stats.Median(imagePng[true])), Stats.Ms(Stats.Median(imagePng[false])));

            Stats.Header("Idle cost (10 s, clipboard untouched)");
            Stats.Row("CPU time", $"{idle[true]:0.0} ms", $"{idle[false]:0.0} ms");
        }

        private static IMonitorRig Create(bool legacy) => legacy ? new LegacyRig() : new CurrentRig();

        private static async Task<List<double>> TextLatency(bool legacy, ClipboardWriter writer, int count)
        {
            using var rig = Create(legacy);
            var seen = new ConcurrentDictionary<string, long>();
            rig.Changed += (_, s) =>
            {
                if (s.Text is not null)
                {
                    seen.TryAdd(s.Text, Stopwatch.GetTimestamp());
                }
            };
            rig.Start();
            await Task.Delay(700);

            var results = new List<double>();
            for (var i = 0; i < count; i++)
            {
                var value = $"bench-{legacy}-{i}-{Guid.NewGuid():N}";
                await writer.RunAsync(() => Clipboard.SetDataObject(value, true, 20, 25));
                var set = Stopwatch.GetTimestamp();
                var deadline = DateTime.UtcNow.AddSeconds(3);
                while (!seen.ContainsKey(value) && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(1);
                }

                if (seen.TryGetValue(value, out var tick))
                {
                    results.Add(Stopwatch.GetElapsedTime(set, tick).TotalMilliseconds);
                }

                await Task.Delay(150);
            }

            return results;
        }

        private static async Task<(int Events, bool LastSeen)> Burst(bool legacy, ClipboardWriter writer, int count)
        {
            using var rig = Create(legacy);
            var events = 0;
            string? last = null;
            rig.Changed += (_, s) =>
            {
                Interlocked.Increment(ref events);
                if (s.Text is not null)
                {
                    last = s.Text;
                }
            };
            rig.Start();
            await Task.Delay(700);

            await writer.RunAsync(() =>
            {
                for (var i = 0; i < count; i++)
                {
                    Clipboard.SetDataObject($"burst-{i}", true, 20, 25);
                }
            });

            await Task.Delay(1500);
            return (events, last == $"burst-{count - 1}");
        }

        private static async Task<List<double>> ImageLatency(bool legacy, ClipboardWriter writer, bool withPng, int runs)
        {
            using var rig = Create(legacy);
            long deliveredTick = 0;
            var gate = new SemaphoreSlim(0);
            rig.Changed += (_, s) =>
            {
                if (s.ImageBytes is { Length: > 0 })
                {
                    Interlocked.Exchange(ref deliveredTick, Stopwatch.GetTimestamp());
                    gate.Release();
                }
            };
            rig.Start();
            await Task.Delay(700);

            using var bitmap = MakeScreenshot(3840, 2160);
            byte[]? png = null;
            if (withPng)
            {
                using var ms = new MemoryStream();
                bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                png = ms.ToArray();
            }

            var results = new List<double>();
            for (var i = 0; i < runs; i++)
            {
                while (gate.Wait(0))
                {
                }

                await writer.RunAsync(() =>
                {
                    var data = new DataObject();
                    data.SetData(DataFormats.Bitmap, true, bitmap);
                    if (png is not null)
                    {
                        data.SetData("PNG", false, new MemoryStream(png));
                    }

                    Clipboard.SetDataObject(data, true, 20, 25);
                });
                var set = Stopwatch.GetTimestamp();

                if (await gate.WaitAsync(TimeSpan.FromSeconds(20)))
                {
                    results.Add(Stopwatch.GetElapsedTime(set, Interlocked.Read(ref deliveredTick)).TotalMilliseconds);
                }

                await Task.Delay(300);
            }

            if (rig is CurrentRig current)
            {
                Console.Error.WriteLine($"[clipboard] current image (png={withPng}) stats: {current.Stats}");
            }

            return results;
        }

        private static Bitmap MakeScreenshot(int width, int height)
        {
            var bitmap = new Bitmap(width, height);
            using var g = Graphics.FromImage(bitmap);
            using var brush = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, width, height), Color.DarkSlateBlue, Color.Goldenrod, 35f);
            g.FillRectangle(brush, 0, 0, width, height);
            var random = new Random(7);
            using var pen = new Pen(Color.White, 2);
            for (var i = 0; i < 400; i++)
            {
                g.DrawRectangle(pen, random.Next(width - 200), random.Next(height - 100), random.Next(40, 200), random.Next(20, 100));
            }

            return bitmap;
        }

        private static async Task<double> IdleCpu(bool legacy, TimeSpan duration)
        {
            using var rig = Create(legacy);
            rig.Start();
            await Task.Delay(1000);
            var cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
            await Task.Delay(duration);
            return (Process.GetCurrentProcess().TotalProcessorTime - cpu0).TotalMilliseconds;
        }
    }
}
