using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using UniversalNotifier.NET;
using UniversalNotifier.NET.InApp;

namespace UniversalNotifier.NET.InApp
{
    /// <summary>Settings for the default in-memory store.</summary>
    public sealed class InAppOptions
    {
        /// <summary>How many notifications the in-memory store keeps per user before dropping the oldest.</summary>
        public int MaxPerUser { get; set; } = 200;
    }

    /// <summary>
    /// "Sends" an in-app notification by writing it to an <see cref="IInAppStore"/>, so the user sees it in your UI the next
    /// time it asks for their inbox. The result's provider message id is the stored entry's id.
    /// </summary>
    public sealed class InAppNotifier : NotifierBase<InAppMessage>, IInAppNotifier
    {
        /// <summary>The provider name used for explicit selection.</summary>
        public const string Name = "inapp";

        private readonly IInAppStore _store;
        private readonly TimeProvider _time;

        /// <summary>Creates the notifier.</summary>
        public InAppNotifier(IInAppStore store, NotifierContext context)
            : base(context)
        {
            ArgumentNullException.ThrowIfNull(store);
            _store = store;
            _time = context.TimeProvider;
        }

        /// <inheritdoc />
        protected override string ProviderName => Name;

        /// <inheritdoc />
        protected override async Task<NotifierResponse> SendCoreAsync(InAppMessage message, CancellationToken cancellationToken)
        {
            var id = Guid.NewGuid().ToString("N");
            await _store.AddAsync(
                new InAppNotification(id, message.UserId, message.Title, message.Text, message.Data, _time.GetUtcNow(), IsRead: false),
                cancellationToken).ConfigureAwait(false);

            return NotifierResponse.Accepted(id);
        }
    }
}

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Registers in-app notifications.</summary>
    public static class InAppBuilderExtensions
    {
        /// <summary>
        /// Adds the in-app channel. Unless an <see cref="IInAppStore"/> is already registered, an in-memory store is used;
        /// register your own first to persist notifications. Inject <see cref="IInAppStore"/> to read and manage inboxes.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="configure">Sets the in-memory store's limits.</param>
        /// <param name="name">A name for a second in-app provider; <see langword="null"/> for the default.</param>
        public static INotifierBuilder AddInApp(this INotifierBuilder builder, Action<InAppOptions>? configure = null, string? name = null)
        {
            ArgumentNullException.ThrowIfNull(builder);

            var options = builder.Services.AddOptions<InAppOptions>()
                .Validate(static o => o.MaxPerUser >= 1, "InApp MaxPerUser must be at least 1.");
            if (configure is not null)
            {
                options.Configure(configure);
            }

            options.ValidateOnStart();
            builder.Services.TryAddSingleton<IInAppStore>(static services =>
                new InMemoryInAppStore(services.GetRequiredService<IOptions<InAppOptions>>().Value.MaxPerUser));

            return builder.AddNotifier<IInAppNotifier>(
                InAppNotifier.Name,
                name,
                static (services, _) => new InAppNotifier(
                    services.GetRequiredService<IInAppStore>(),
                    services.GetRequiredService<NotifierContext>()));
        }
    }
}
