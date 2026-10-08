import sys

SERVICES = 200
CONTROLLERS = 150
ENTITIES = 40

out = []
w = out.append

w("using System.ComponentModel.DataAnnotations;")
w("using Microsoft.AspNetCore.Mvc;")
w("using Microsoft.EntityFrameworkCore;")
w("using Microsoft.Extensions.Options;")
w("")
w("namespace Repro;")
w("")

for i in range(SERVICES):
    w(f"public sealed class Options{i} {{ public int Value {{ get; set; }} public string Name {{ get; set; }} = \"\"; public TimeSpan Delay {{ get; set; }} public List<string> Items {{ get; set; }} = new(); }}")
    w(f"public interface IService{i} {{ int Compute(int x); }}")
    dep = f"IService{i // 2} parent, " if i > 0 else ""
    use = f"parent.Compute(x) + " if i > 0 else ""
    w(f"public sealed class Service{i}({dep}IOptionsMonitor<Options{i}> options) : IService{i}")
    w("{")
    w(f"    public int Compute(int x) => {use}options.CurrentValue.Value * x + options.CurrentValue.Items.Count + {i};")
    w("}")

for i in range(ENTITIES):
    w(f"public class Entity{i}")
    w("{")
    w("    public int Id { get; set; }")
    w("    [MaxLength(64)] public virtual string Name { get; set; } = \"\";")
    w("    public virtual decimal Amount { get; set; }")
    w("    public virtual DateTime CreatedAt { get; set; }")
    w("    public virtual bool IsCoveredByInsurance { get; set; }")
    w("    public virtual int? ParentId { get; set; }")
    w(f"    public virtual Entity{i}? Parent {{ get; set; }}")
    w(f"    public virtual ICollection<Entity{i}> Children {{ get; set; }} = new List<Entity{i}>();")
    w("}")

w("public sealed class ReproDbContext(DbContextOptions<ReproDbContext> options) : DbContext(options)")
w("{")
for i in range(ENTITIES):
    w(f"    public DbSet<Entity{i}> Set{i} => Set<Entity{i}>();")
w("    protected override void OnModelCreating(ModelBuilder b)")
w("    {")
for i in range(ENTITIES):
    w(f"        b.Entity<Entity{i}>().HasOne(e => e.Parent).WithMany(e => e.Children).HasForeignKey(e => e.ParentId);")
    w(f"        b.Entity<Entity{i}>().HasIndex(e => e.Name);")
w("    }")
w("}")

w("public static class DbWork")
w("{")
w("    public static async Task RunAsync(ReproDbContext db, int seed, CancellationToken ct)")
w("    {")
w(f"        switch (seed % {ENTITIES})")
w("        {")
for i in range(ENTITIES):
    w(f"            case {i}:")
    w("            {")
    w(f"                var root = new Entity{i} {{ Name = \"r\" + seed, Amount = seed, CreatedAt = DateTime.UtcNow, IsCoveredByInsurance = seed % 2 == 0 }};")
    w(f"                for (var k = 0; k < 20; k++) root.Children.Add(new Entity{i} {{ Name = \"c\" + k, Amount = k, CreatedAt = DateTime.UtcNow }});")
    w(f"                db.Set{i}.Add(root);")
    w("                await db.SaveChangesAsync(ct);")
    w(f"                var loaded = await db.Set{i}.Where(e => e.ParentId == null && e.Amount >= 0).OrderByDescending(e => e.Id).Take(5).ToListAsync(ct);")
    w("                foreach (var e in loaded) { _ = e.Children.Count(c => c.IsCoveredByInsurance); e.Amount += 1; }")
    w("                await db.SaveChangesAsync(ct);")
    w(f"                await db.Set{i}.Where(e => e.Amount > 1000000).ExecuteDeleteAsync(ct);")
    w("                break;")
    w("            }")
w("        }")
w("    }")
w("}")

for i in range(CONTROLLERS):
    a = i % SERVICES
    b = (i * 7 + 3) % SERVICES
    w(f"public sealed record Dto{i}(int Id, string Name, decimal Amount, DateTime At, IReadOnlyList<int> Values);")
    w("[ApiController]")
    w(f"[Route(\"c{i}\")]")
    w(f"public sealed class C{i}Controller(IService{a} s1, IService{b} s2, IOptionsSnapshot<Options{a}> o) : ControllerBase")
    w("{")
    w("    [HttpGet(\"a\")]")
    w(f"    public ActionResult<Dto{i}> A([FromQuery] int x) => new Dto{i}(x, o.Value.Name + x, s1.Compute(x) + s2.Compute(x), DateTime.UtcNow, Enumerable.Range(0, x % 50).Select(v => v * {i}).ToList());")
    w("    [HttpPost(\"b\")]")
    w(f"    public async Task<ActionResult<Dto{i}>> B([FromBody] Dto{i} d)")
    w("    {")
    w("        await Task.Yield();")
    w(f"        return d with {{ Amount = d.Amount + s1.Compute(d.Id), Values = d.Values.Select(v => v + 1).ToList() }};")
    w("    }")
    w("    [HttpGet(\"c/{id:int}\")]")
    w("    public async Task<IActionResult> C(int id, CancellationToken ct)")
    w("    {")
    w("        await Task.Delay(id % 3, ct);")
    w(f"        return id % 5 == 0 ? NotFound() : Ok(new {{ id, v = s2.Compute(id) }});")
    w("    }")
    w("}")

w("public static class Generated")
w("{")
w(f"    public const int Controllers = {CONTROLLERS};")
w(f"    public const int Services = {SERVICES};")
w("    public static void Register(IServiceCollection services, IConfiguration config)")
w("    {")
for i in range(SERVICES):
    life = ["AddSingleton", "AddScoped", "AddTransient"][i % 3]
    if i % 2 == 0:
        w(f"        services.Configure<Options{i}>(config.GetSection(\"Opt:s{i}\"));")
    else:
        w(f"        services.Configure<Options{i}>(o => {{ o.Value = {i % 13 + 1}; o.Name = \"s{i}\"; o.Items.Add(\"x\"); }});")
    w(f"        services.{life}<IService{i}, Service{i}>();")
w("    }")
w("    public static readonly Type[] ServiceTypes =")
w("    [")
for i in range(SERVICES):
    w(f"        typeof(IService{i}), typeof(IOptionsMonitor<Options{i}>), typeof(IOptionsSnapshot<Options{i}>),")
w("    ];")
w("}")

with open(sys.argv[1], "w") as f:
    f.write("\n".join(out) + "\n")
