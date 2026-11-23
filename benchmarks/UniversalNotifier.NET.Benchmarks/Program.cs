using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Microsoft.Extensions.DependencyInjection;
using UniversalNotifier.NET;
using UniversalNotifier.NET.Background;
using UniversalNotifier.NET.Testing;

BenchmarkSwitcher.FromAssembly(typeof(RendererBenchmarks).Assembly).Run(args);

[MemoryDiagnoser]
public class RendererBenchmarks
{
    private const string Template = "Hello {{Name}}, your order {{Order.Number}} totalling {{Total}} ships on {{Date}}. Thanks, {{Company}}!";
    private readonly TokenTemplateRenderer _renderer = new();
    private readonly TemplateData _objectData = TemplateData.From(new { Name = "Ada", Order = new { Number = "A-1001" }, Total = 99.5m, Date = "Monday", Company = "Example" });
    private readonly TemplateData _dictionaryData = TemplateData.From(new Dictionary<string, object?>
    {
        ["Name"] = "Ada",
        ["Order"] = new Dictionary<string, object?> { ["Number"] = "A-1001" },
        ["Total"] = 99.5m,
        ["Date"] = "Monday",
        ["Company"] = "Example",
    });

    [Benchmark(Baseline = true)]
    public string Render_Object() => _renderer.Render(Template, _objectData);

    [Benchmark]
    public string Render_Dictionary() => _renderer.Render(Template, _dictionaryData);

    [Benchmark]
    public string Render_Html_Encoded() => _renderer.Render(Template, _objectData, TemplateFormat.Html);

    [Benchmark]
    public string Render_NoTokens() => _renderer.Render("A message without any tokens at all.", _objectData);
}

[MemoryDiagnoser]
public class DispatcherBenchmarks
{
    private ServiceProvider _provider = null!;
    private INotificationService _service = null!;
    private Notification _email = null!;
    private Notification _threeChannels = null!;

    [GlobalSetup]
    public void Setup()
    {
        _provider = new ServiceCollection().AddUniversalNotifier(n => n.AddFakeNotifiers()).BuildServiceProvider();
        _service = _provider.GetRequiredService<INotificationService>();
        var everyone = new Recipient { Email = "ada@example.com", Phone = "+14165551234", DeviceTokens = ["token"] };
        _email = Notification.To(everyone).Email("Hi {{Name}}", text: "Hello {{Name}}").WithData(new { Name = "Ada" });
        _threeChannels = Notification.To(everyone)
            .Email("Hi {{Name}}", text: "Hello {{Name}}")
            .Sms("Hi {{Name}}")
            .Push("Hi", "Hello {{Name}}")
            .WithData(new { Name = "Ada" });
    }

    [GlobalCleanup]
    public void Cleanup() => _provider.Dispose();

    [Benchmark(Baseline = true)]
    public Task<NotificationResult> OneChannel() => _service.SendAsync(_email);

    [Benchmark]
    public Task<NotificationResult> ThreeChannels() => _service.SendAsync(_threeChannels);

    [Benchmark]
    public IReadOnlyList<NotifierError> ValidateEmail() => EmailMessage.Create("ada@example.com", "Hi", "Body").Validate();
}

[MemoryDiagnoser]
public class QueueBenchmarks
{
    private readonly Notification _notification = Notification.To("ada@example.com").Email("Hi", text: "x");

    [Benchmark]
    public async Task EnqueueAndDequeue_1000()
    {
        var queue = new ChannelNotificationQueue(new BackgroundDeliveryOptions { Capacity = 1000 });
        for (var i = 0; i < 1000; i++)
        {
            await queue.EnqueueAsync(_notification);
        }

        queue.Complete();
        await foreach (var _ in queue.DequeueAllAsync())
        {
        }
    }

    [Benchmark]
    public Notification Serialize_And_Deserialize() => NotificationJson.Deserialize(NotificationJson.Serialize(_notification));
}
