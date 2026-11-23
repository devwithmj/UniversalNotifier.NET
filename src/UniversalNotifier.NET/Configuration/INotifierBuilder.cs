using Microsoft.Extensions.DependencyInjection;

namespace UniversalNotifier.NET;

/// <summary>
/// Where providers, templates and channels are registered. Provider packages add methods to this interface
/// (<c>AddMailgun</c>, <c>AddTwilio</c>, ...), and it is the same whether you use dependency injection or <see cref="NotifierBuilder"/>.
/// </summary>
public interface INotifierBuilder
{
    /// <summary>The service collection being configured.</summary>
    IServiceCollection Services { get; }
}
