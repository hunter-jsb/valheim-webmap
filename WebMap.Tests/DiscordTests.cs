using System;
using WebMap;
using Xunit;

namespace WebMap.Tests
{
    // Discord.cs's pure parts: the 429 retry and the message parser. The wire is
    // Discord.Transport, swapped for a fake here -- nothing in this file reaches Discord.
    public class DiscordTests : IDisposable
    {
        private readonly Func<string, string, string, Discord.Result> original = Discord.Transport;
        public DiscordTests() { WebMapConfig.DISCORD_BOT_TOKEN = "test-token"; }
        public void Dispose() { Discord.Transport = original; WebMapConfig.DISCORD_BOT_TOKEN = ""; }

        [Fact]
        public void A429WaitsOnRetryAfterAndSucceedsOnTheSecondTry()
        {
            int calls = 0;
            Discord.Transport = (method, path, body) =>
            {
                calls++;
                return calls == 1
                    ? new Discord.Result { Status = 429, Body = "{\"retry_after\":0.01}", RetryAfter = 0.01 }
                    : new Discord.Result { Status = 200, Body = "{}", RetryAfter = 0 };
            };
            Discord.Call("POST", "/channels/1/messages", "{}");
            Assert.Equal(2, calls);
        }

        [Fact]
        public void ASecondConsecutive429IsNotRetriedAgain()
        {
            int calls = 0;
            Discord.Transport = (method, path, body) => { calls++; return new Discord.Result { Status = 429, RetryAfter = 0.01 }; };
            Assert.Throws<Exception>(() => Discord.Call("GET", "/channels/1/messages", null));
            Assert.Equal(2, calls);
        }

        [Fact]
        public void MessagesParseTheDisplayNameNicknameFirstAndSkipBotsAndWebhooks()
        {
            Discord.Transport = (method, path, body) => new Discord.Result
            {
                Status = 200,
                Body = "["
                    + "{\"id\":\"3\",\"content\":\"hi\",\"author\":{\"username\":\"real1\",\"global_name\":\"Real One\",\"bot\":false},\"member\":{\"nick\":\"Realio\"}},"
                    + "{\"id\":\"2\",\"content\":\"beep\",\"author\":{\"username\":\"botty\",\"bot\":true}},"
                    + "{\"id\":\"1\",\"content\":\"relay\",\"author\":{\"username\":\"hook\",\"bot\":false},\"webhook_id\":\"555\"}"
                    + "]",
            };
            var msgs = Discord.MessagesAfter("1234", "0", 50);
            Assert.Equal(3, msgs.Count);
            Assert.Equal("Realio", msgs[0].DisplayName);   // the guild nickname wins over global_name and username
            Assert.False(msgs[0].Bot); Assert.False(msgs[0].Webhook);
            Assert.True(msgs[1].Bot);
            Assert.True(msgs[2].Webhook);
        }
    }
}
