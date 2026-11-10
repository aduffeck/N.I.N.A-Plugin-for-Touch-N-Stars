using NINA.Core.Utility;
using NINA.Plugin.Interfaces;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace TouchNStars.Utility {

    internal class Communicator : ISubscriber, IDisposable {

        public static int FoundPort { get; private set; } = 1888;

        public Communicator() {
            TouchNStars.Mediators.MessageBroker.Subscribe("AdvancedAPI.Port", this);
        }

        public Task OnMessageReceived(IMessage message) {
            // GetApiUrl asks on every call: only a change is worth a log line
            if (int.TryParse(message.Content?.ToString(), out int port) && port != FoundPort) {
                Logger.Info($"Advanced API port changed from {FoundPort} to {port}");
                FoundPort = port;
            }

            return Task.CompletedTask;
        }

        public async Task<int> GetPort(bool force = false) {
            if (!force) {
                return FoundPort;
            }
            await TouchNStars.Mediators.MessageBroker.Publish(new PortMessage("AdvancedAPI.RequestPort", string.Empty));
            return FoundPort;
        }

        public void Dispose() {
            TouchNStars.Mediators.MessageBroker.Unsubscribe("AdvancedAPI.Port", this);
        }
    }

    public class PortMessage(string topic, string content) : IMessage {
        public Guid SenderId => Guid.Parse(TouchNStars.PluginId);

        public string Sender => nameof(TouchNStars);

        public DateTimeOffset SentAt => DateTime.UtcNow;

        public Guid MessageId => Guid.NewGuid();

        public DateTimeOffset? Expiration => null;

        public Guid? CorrelationId => Guid.NewGuid();

        public int Version => 1;

        public IDictionary<string, object> CustomHeaders => new Dictionary<string, object>();

        public string Topic => topic;

        public object Content => content;
    }
}
