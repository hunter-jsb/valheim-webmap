using System.Collections.Generic;
using WebMap;
using Xunit;

namespace WebMap.Tests
{
    // ChatRelay.LinesToSpeak: the pure decision of what one poll hands to Announce.
    public class ChatRelayTests
    {
        private static DiscordMessage Msg(string id, string content, bool bot = false, bool webhook = false, string name = "Player") =>
            new DiscordMessage { Id = id, Content = content, DisplayName = name, Bot = bot, Webhook = webhook };

        [Fact]
        public void BotsWebhooksAndEmptyTextAreSkipped()
        {
            // newest first, as Discord answers
            var msgs = new List<DiscordMessage> {
                Msg("4", "", name: "Nobody"),
                Msg("3", "relayed back", webhook: true),
                Msg("2", "beep", bot: true),
                Msg("1", "hello", name: "Grant"),
            };
            var lines = ChatRelay.LinesToSpeak(msgs);
            Assert.Equal(new[] { "[Discord] Grant: hello" }, lines);
        }

        [Fact]
        public void APollNeverSpeaksMoreThanTheCap()
        {
            var msgs = new List<DiscordMessage>();
            for (int i = 0; i < ChatRelay.Cap + 3; i++) msgs.Add(Msg(i.ToString(), "msg" + i));
            Assert.Equal(ChatRelay.Cap, ChatRelay.LinesToSpeak(msgs).Count);
        }
    }
}
