using System.Collections.Concurrent;
using System.Globalization;
using System.Linq.Expressions;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.EntityFrameworkCore;
using Repro;
using TimeZoneConverter;

var duration = TimeSpan.FromSeconds(Env.Int("REPRO_DURATION_SECONDS", 120));
var started = DateTime.UtcNow;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.WebHost.UseUrls($"http://127.0.0.1:{Env.Int("REPRO_PORT", 5080)}");
builder.Configuration.AddInMemoryCollection(
    Enumerable.Range(0, Generated.Services).Where(i => i % 2 == 0).SelectMany(i => new[]
    {
        new KeyValuePair<string, string?>($"Opt:s{i}:Value", (i % 13 + 1).ToString(CultureInfo.InvariantCulture)),
        new KeyValuePair<string, string?>($"Opt:s{i}:Name", $"s{i}"),
        new KeyValuePair<string, string?>($"Opt:s{i}:Delay", "00:00:01"),
        new KeyValuePair<string, string?>($"Opt:s{i}:Items:0", "x"),
    }));
builder.Services.AddControllers();
Generated.Register(builder.Services, builder.Configuration);
builder.Services.AddHostedService<Workloads>();

var app = builder.Build();
app.MapControllers();
app.Lifetime.ApplicationStarted.Register(() => Console.WriteLine($"REPRO_STARTED after {(DateTime.UtcNow - started).TotalSeconds:F1}s"));
_ = Task.Run(async () =>
{
    await Task.Delay(duration);
    app.Lifetime.StopApplication();
});
app.Run();
Console.WriteLine($"REPRO_DONE after {(DateTime.UtcNow - started).TotalSeconds:F1}s {Counters.Summary()}");

static class Env
{
    public static int Int(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : fallback;
}

static class Counters
{
    static readonly ConcurrentDictionary<string, long> Values = new();
    static readonly ConcurrentDictionary<string, bool> Reported = new();
    public static void Inc(string name) => Values.AddOrUpdate(name, 1, (_, v) => v + 1);
    public static void Error(string name, Exception ex)
    {
        Inc(name + "_err");
        if (Reported.TryAdd(name, true)) Console.Error.WriteLine($"REPRO_ERROR {name}: {ex.GetType().Name}: {ex.Message}");
    }
    public static string Summary() => string.Join(" ", Values.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
}

sealed class Workloads(IConfiguration configuration, IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken ct)
    {
        var tasks = new List<Task>();
        for (var i = 0; i < Env.Int("REPRO_RESOLVE_WORKERS", 2); i++) tasks.Add(Dedicated("resolve", Resolve, ct));
        for (var i = 0; i < Env.Int("REPRO_DB_WORKERS", 4); i++) tasks.Add(Db(i, ct));
        for (var i = 0; i < Env.Int("REPRO_HTTP_WORKERS", 8); i++) tasks.Add(Http(i, ct));
        for (var i = 0; i < Env.Int("REPRO_EXPR_WORKERS", 1); i++) tasks.Add(Dedicated("expr", Expressions, ct));
        for (var i = 0; i < Env.Int("REPRO_ALLOC_WORKERS", 1); i++) tasks.Add(Dedicated("alloc", Allocate, ct));
        for (var i = 0; i < Env.Int("REPRO_TZ_WORKERS", 1); i++) tasks.Add(Dedicated("tz", TimeZones, ct));
        for (var i = 0; i < Env.Int("REPRO_SCRIPT_WORKERS", 1); i++) tasks.Add(Dedicated("script", Scripts, ct));
        for (var i = 0; i < Env.Int("REPRO_ALC_WORKERS", 1); i++) tasks.Add(Dedicated("alc", CollectibleAssemblies, ct));
        for (var i = 0; i < Env.Int("REPRO_NATIVE_WORKERS", 0); i++) tasks.Add(Dedicated("native", NativeLibraries, ct));
        return Task.WhenAll(tasks);
    }

    static readonly int LoopSleep = Env.Int("REPRO_LOOP_SLEEP_MS", 10);

    static Task Dedicated(string name, Action body, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            Loop(name, body, ct);
            tcs.TrySetResult();
        }) { IsBackground = true, Name = name };
        thread.Start();
        return tcs.Task;
    }

    static void Loop(string name, Action body, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                body();
                Counters.Inc(name);
                Thread.Sleep(LoopSleep);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Counters.Error(name, ex);
            }
        }
    }

    void Resolve()
    {
        var collection = new ServiceCollection();
        collection.AddOptions();
        Generated.Register(collection, configuration);
        using var provider = collection.BuildServiceProvider();
        for (var round = 0; round < 4; round++)
        {
            using var scope = provider.CreateScope();
            foreach (var type in Generated.ServiceTypes)
            {
                _ = scope.ServiceProvider.GetService(type);
            }
        }
    }

    async Task Db(int worker, CancellationToken ct)
    {
        await Task.Yield();
        var path = $"/tmp/repro-{worker}.db";
        File.Delete(path);
        var options = new DbContextOptionsBuilder<ReproDbContext>()
            .UseLazyLoadingProxies()
            .UseSqlite($"Data Source={path}")
            .Options;
        await using (var db = new ReproDbContext(options))
        {
            await db.Database.EnsureCreatedAsync(ct);
        }
        var seed = worker;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var db = new ReproDbContext(options);
                await DbWork.RunAsync(db, seed, ct);
                seed += 7;
                Counters.Inc("db");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Counters.Error("db", ex);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    async Task Http(int worker, CancellationToken ct)
    {
        var ready = new TaskCompletionSource();
        using var reg = lifetime.ApplicationStarted.Register(() => ready.TrySetResult());
        await ready.Task.WaitAsync(ct);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{Env.Int("REPRO_PORT", 5080)}/") };
        var random = new Random(worker);
        while (!ct.IsCancellationRequested)
        {
            var c = random.Next(Generated.Controllers);
            var x = random.Next(1, 200);
            try
            {
                switch (random.Next(3))
                {
                    case 0:
                        (await client.GetAsync($"c{c}/a?x={x}", ct)).Dispose();
                        break;
                    case 1:
                        (await client.PostAsJsonAsync($"c{c}/b", new { id = x, name = "n" + x, amount = x * 1.5m, at = DateTime.UtcNow, values = new[] { 1, 2, x } }, ct)).Dispose();
                        break;
                    default:
                        (await client.GetAsync($"c{c}/c/{x}", ct)).Dispose();
                        break;
                }
                Counters.Inc("http");
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Counters.Error("http", ex);
            }
        }
    }

    static int _expressionSeed;

    static void Expressions()
    {
        var n = Interlocked.Increment(ref _expressionSeed);
        var x = Expression.Parameter(typeof(int), "x");
        var items = Expression.Parameter(typeof(List<int>), "items");
        var abs = typeof(Math).GetMethod(nameof(Math.Abs), [typeof(int)])!;
        var body = Expression.Add(
            Expression.Multiply(x, Expression.Constant(n)),
            Expression.Add(
                Expression.Call(abs, Expression.Constant(-n)),
                Expression.Property(items, nameof(List<int>.Count))));
        var f = Expression.Lambda<Func<int, List<int>, int>>(body, x, items).Compile();
        var list = Enumerable.Range(0, n % 64).ToList();
        var sum = 0;
        for (var i = 0; i < 50; i++) sum += f(i, list);
        Expression<Func<KeyValuePair<int, string>, bool>> predicate = kv => kv.Key % (n % 5 + 2) == 0 && kv.Value.Length > n % 3;
        var compiled = predicate.Compile();
        sum += Enumerable.Range(0, 200).Select(i => new KeyValuePair<int, string>(i, new string('a', i % 7))).Count(compiled);
        GC.KeepAlive(sum);
    }

    static void Allocate()
    {
        var list = new List<string>(4096);
        for (var k = 0; k < 4096; k++) list.Add(new string('x', k % 200) + k.ToString(CultureInfo.InvariantCulture));
        var groups = list.GroupBy(s => s.Length % 17).ToDictionary(g => g.Key, g => g.Select(s => s.ToUpperInvariant()).ToArray());
        var arrays = new List<byte[]>();
        for (var k = 0; k < 64; k++) arrays.Add(new byte[1024 * (k % 32 + 1)]);
        GC.KeepAlive(groups);
        GC.KeepAlive(arrays);
    }

    static void NativeLibraries()
    {
        var data = new byte[256 * 1024];
        Random.Shared.NextBytes(data);
        _ = System.Security.Cryptography.SHA256.HashData(data);
        using (var aes = System.Security.Cryptography.Aes.Create())
        {
            _ = aes.EncryptCbc(data, aes.IV);
        }
        using var compressed = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(compressed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(data);
        }
        compressed.Position = 0;
        using var brotli = new MemoryStream();
        using (var b = new System.IO.Compression.BrotliStream(brotli, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            compressed.CopyTo(b);
        }
        _ = Directory.GetFiles("/usr/share/zoneinfo", "*", SearchOption.AllDirectories).Length;
    }

    static readonly string[] Cultures = ["pt-BR", "es-MX", "pl-PL", "it-IT", "de-DE", "tr-TR", "cs-CZ", "en-GB"];

    static void TimeZones()
    {
        TimeZoneInfo.ClearCachedData();
        foreach (var zone in TimeZoneInfo.GetSystemTimeZones().Take(80))
        {
            _ = zone.DisplayName;
            _ = zone.StandardName;
            _ = zone.DaylightName;
        }
        foreach (var id in TZConvert.KnownIanaTimeZoneNames.Take(120))
        {
            var tz = TZConvert.GetTimeZoneInfo(id);
            _ = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        }
        foreach (var name in Cultures)
        {
            var culture = CultureInfo.GetCultureInfo(name);
            _ = DateTime.UtcNow.ToString("F", culture);
            _ = string.Compare("Ábc", "abc", culture, CompareOptions.IgnoreCase);
            _ = culture.TextInfo.ToUpper("ğüşiöç");
        }
        Thread.Sleep(20);
    }

    static int _scriptSeed;

    static void Scripts()
    {
        var n = Interlocked.Increment(ref _scriptSeed);
        var options = ScriptOptions.Default
            .AddReferences(typeof(Enumerable).Assembly, typeof(List<>).Assembly)
            .AddImports("System", "System.Linq", "System.Collections.Generic");
        var code = $"var items = Enumerable.Range(0, {n % 40 + 5}).Select(i => new {{ I = i, S = \"t\" + i }}).ToList(); int F{n}(int x) => x * {n} + items.Count; F{n}(items.Sum(v => v.I))";
        _ = CSharpScript.EvaluateAsync<int>(code, options).GetAwaiter().GetResult();
        Thread.Sleep(250);
    }

    static readonly Lazy<MetadataReference[]> References = new(() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p) is "System.Private.CoreLib.dll" or "System.Runtime.dll" or "System.Linq.dll" or "System.Collections.dll")
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToArray());

    static int _alcSeed;

    static void CollectibleAssemblies()
    {
        var n = Interlocked.Increment(ref _alcSeed);
        var source = $$"""
            using System;
            using System.Linq;
            using System.Collections.Generic;
            public static class Gen{{n}}
            {
                public static int Run(int x)
                {
                    var total = 0;
                    for (var i = 0; i < 2000; i++) total += Helper(i, x);
                    return total + Enumerable.Range(0, x % 50).Where(v => v % 3 == 0).Sum();
                }
                static int Helper(int i, int x) => (i * {{n}} ^ x) % 97;
            }
            """;
        var compilation = CSharpCompilation.Create(
            $"Gen{n}",
            [CSharpSyntaxTree.ParseText(source)],
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
        using var stream = new MemoryStream();
        if (!compilation.Emit(stream).Success)
        {
            throw new InvalidOperationException("emit failed");
        }
        stream.Position = 0;
        RunAndUnload(stream, n);
        if (n % 10 == 0)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static void RunAndUnload(Stream stream, int n)
    {
        var context = new AssemblyLoadContext($"alc{n}", isCollectible: true);
        var assembly = context.LoadFromStream(stream);
        var run = assembly.GetType($"Gen{n}")!.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!.CreateDelegate<Func<int, int>>();
        var total = 0;
        for (var i = 0; i < 200; i++) total += run(i);
        GC.KeepAlive(total);
        context.Unload();
    }
}
