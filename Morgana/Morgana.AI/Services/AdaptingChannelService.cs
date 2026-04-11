using Morgana.AI.Adapters;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Services;

/// <summary>
/// Decorator over a concrete <see cref="IChannelService"/> that routes every outbound
/// <see cref="Records.ChannelMessage"/> through <see cref="MorganaChannelAdapter"/> before
/// handing it to the underlying transport. This is the single chokepoint where rich
/// messages are degraded to fit the capabilities advertised by the wrapped channel,
/// making it impossible for producers (presenter, supervisor, manager, controller, agents)
/// to emit a payload that the target channel cannot carry.
/// </summary>
/// <remarks>
/// <para><strong>Why a decorator:</strong></para>
/// <para>Producers keep calling <see cref="IChannelService.SendMessageAsync"/> exactly as
/// before. DI hands them this wrapper instead of the concrete channel, so the degradation
/// gate is applied uniformly and adding a new channel implementation (step 3 of the channel
/// abstraction initiative) inherits the gate for free.</para>
///
/// <para><strong>Capabilities source:</strong></para>
/// <para>The adapter reads the budget from <see cref="IChannelService.Capabilities"/> on the
/// wrapped instance, so capabilities remain co-located with the channel implementation and
/// this decorator carries no channel-specific knowledge.</para>
///
/// <para><strong>Streaming:</strong></para>
/// <para><see cref="SendStreamChunkAsync"/> is a straight pass-through: streaming is gated
/// upstream in <c>MorganaAgent</c> (no streaming connection is ever opened toward a
/// non-streaming channel), so the adapter never sees partial chunks.</para>
///
/// <para><strong>Reliability:</strong></para>
/// <para><see cref="MorganaChannelAdapter.AdaptAsync"/> never throws — worst case it returns
/// a template-based plain rendering — so the decorator adds no new failure modes on the
/// send path.</para>
/// </remarks>
public class AdaptingChannelService : IChannelService
{
    /// <summary>
    /// The concrete channel being decorated (e.g. <c>SignalRChannelService</c>). All calls
    /// are ultimately delegated to this instance after (optional) adaptation.
    /// </summary>
    private readonly IChannelService inner;

    /// <summary>
    /// Adapter responsible for transcoding a rich message into a form that fits the
    /// capabilities of <see cref="inner"/>. Invoked once per <see cref="SendMessageAsync"/>
    /// call; short-circuits without I/O when the message already fits.
    /// </summary>
    private readonly MorganaChannelAdapter channelAdapter;

    /// <summary>
    /// Initialises a new instance of <see cref="AdaptingChannelService"/>.
    /// </summary>
    /// <param name="inner">The concrete channel service being decorated.</param>
    /// <param name="channelAdapter">The adapter used to degrade outbound messages to the
    /// capabilities advertised by <paramref name="inner"/>.</param>
    public AdaptingChannelService(IChannelService inner, MorganaChannelAdapter channelAdapter)
    {
        this.inner = inner;
        this.channelAdapter = channelAdapter;
    }

    /// <inheritdoc/>
    public Records.ChannelCapabilities Capabilities => inner.Capabilities;

    /// <inheritdoc/>
    public async Task SendMessageAsync(Records.ChannelMessage message)
    {
        Records.ChannelMessage adapted = await channelAdapter.AdaptAsync(message, inner.Capabilities);
        await inner.SendMessageAsync(adapted);
    }

    /// <inheritdoc/>
    public Task SendStreamChunkAsync(string conversationId, string chunkText) =>
        inner.SendStreamChunkAsync(conversationId, chunkText);
}
