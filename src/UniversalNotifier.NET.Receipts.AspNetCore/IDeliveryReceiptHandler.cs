namespace UniversalNotifier.NET.Receipts;

/// <summary>Receives a provider's delivery events. Register implementations with dependency injection (<c>services.AddSingleton&lt;IDeliveryReceiptHandler, MyHandler&gt;()</c>).</summary>
public interface IDeliveryReceiptHandler
{
    /// <summary>
    /// Handles one delivery event, for example by updating the status of the message with this <see cref="DeliveryEvent.ProviderMessageId"/>.
    /// Throwing makes the endpoint answer 500 so the provider sends the event again later.
    /// </summary>
    ValueTask HandleAsync(DeliveryEvent deliveryEvent, CancellationToken cancellationToken);
}
