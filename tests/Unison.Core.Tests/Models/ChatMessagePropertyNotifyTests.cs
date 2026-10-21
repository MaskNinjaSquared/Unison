// =============================================================================
// ChatMessage hot setters must not raise PropertyChanged when the value is
// unchanged — open-chat merge / receipt replay otherwise rebinds bubbles.
// =============================================================================
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Models
{
    public class ChatMessagePropertyNotifyTests
    {
        [Fact]
        public void Content_setter_skips_notify_when_unchanged()
        {
            var message = new ChatMessage { Content = "ola" };
            int count = 0;
            message.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ChatMessage.Content))
                {
                    count++;
                }
            };

            message.Content = "ola";

            Assert.Equal(0, count);
        }

        [Fact]
        public void Caption_setter_skips_notify_when_unchanged()
        {
            var message = new ChatMessage { Caption = "legenda" };
            int count = 0;
            message.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ChatMessage.Caption) ||
                    e.PropertyName == nameof(ChatMessage.HasCaption))
                {
                    count++;
                }
            };

            message.Caption = "legenda";

            Assert.Equal(0, count);
        }

        [Fact]
        public void Timestamp_setter_skips_notify_when_unchanged()
        {
            var ts = new System.DateTime(2026, 10, 21, 12, 0, 0, System.DateTimeKind.Utc);
            var message = new ChatMessage { Timestamp = ts };
            int count = 0;
            message.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ChatMessage.Timestamp) ||
                    e.PropertyName == nameof(ChatMessage.FormattedTime))
                {
                    count++;
                }
            };

            message.Timestamp = ts;

            Assert.Equal(0, count);
        }
    }
}
