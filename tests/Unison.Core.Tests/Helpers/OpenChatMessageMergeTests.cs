using System;
using System.Collections.Generic;
using System.Linq;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class OpenChatMessageMergeTests
    {
        [Fact]
        public void Null_sources_yield_an_empty_list()
        {
            Assert.Empty(OpenChatMessageMerge.Merge(null, null));
        }

        [Fact]
        public void Sql_only_keeps_order_by_timestamp_then_id()
        {
            var older = Msg("a", new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc));
            var newer = Msg("b", new DateTime(2024, 1, 1, 13, 0, 0, DateTimeKind.Utc));

            List<ChatMessage> merged = OpenChatMessageMerge.Merge(new[] { newer, older }, null);

            Assert.Equal(new[] { "a", "b" }, merged.Select(m => m.Id).ToArray());
        }

        [Fact]
        public void Live_wins_on_id_collision()
        {
            var fromSql = Msg("m1", new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc), "sql");
            var fromLive = Msg("m1", new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc), "live");

            List<ChatMessage> merged = OpenChatMessageMerge.Merge(new[] { fromSql }, new[] { fromLive });

            Assert.Single(merged);
            Assert.Equal("live", merged[0].Content);
        }

        [Fact]
        public void Live_keeps_sql_media_keys_when_it_has_none()
        {
            var fromSql = Msg("m1", new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc));
            fromSql.ImageMediaKeyBase64 = "sql-key";
            var fromLive = Msg("m1", new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc), "live");

            List<ChatMessage> merged = OpenChatMessageMerge.Merge(new[] { fromSql }, new[] { fromLive });

            Assert.Equal("sql-key", merged[0].ImageMediaKeyBase64);
            Assert.Equal("live", merged[0].Content);
        }

        [Fact]
        public void Id_less_rows_from_both_sides_are_kept()
        {
            var sql = Msg(null, new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc), "sql");
            var live = Msg(null, new DateTime(2024, 1, 1, 13, 0, 0, DateTimeKind.Utc), "live");

            List<ChatMessage> merged = OpenChatMessageMerge.Merge(new[] { sql }, new[] { live });

            Assert.Equal(2, merged.Count);
            Assert.Contains(merged, m => m.Content == "sql");
            Assert.Contains(merged, m => m.Content == "live");
        }

        private static ChatMessage Msg(string id, DateTime utc, string content = null)
        {
            return new ChatMessage
            {
                Id = id,
                Timestamp = utc,
                Content = content
            };
        }
    }
}
