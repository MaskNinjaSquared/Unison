// =============================================================================
// Tests for SelfDisplayNameResolution: which source names the account itself.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class SelfDisplayNameResolutionTests
    {
        private static KeyValuePair<string, Func<string>> Source(string name, string value) =>
            new KeyValuePair<string, Func<string>>(name, () => value);

        private static string KeepNonBlank(string candidate) =>
            string.IsNullOrWhiteSpace(candidate) || candidate == "rejected" ? null : candidate;

        [Fact]
        public void The_first_source_with_a_usable_name_wins()
        {
            SelfDisplayNameResolution.Result result = SelfDisplayNameResolution.Pick(
                new[] { Source("phone", null), Source("cache", "rejected"), Source("auth", " Ana "), Source("current", "Other") },
                KeepNonBlank,
                "5511999990001");

            Assert.Equal("Ana", result.Name);
            Assert.Equal("auth", result.Source);
        }

        [Fact]
        public void Later_sources_are_not_read_once_one_answers()
        {
            bool laterRead = false;
            var sources = new[]
            {
                Source("phone", "Ana"),
                new KeyValuePair<string, Func<string>>("cache", () => { laterRead = true; return "x"; })
            };

            SelfDisplayNameResolution.Pick(sources, KeepNonBlank, null);

            Assert.False(laterRead);
        }

        [Fact]
        public void With_no_name_the_phone_number_stands_in()
        {
            SelfDisplayNameResolution.Result result = SelfDisplayNameResolution.Pick(
                new[] { Source("phone", null) },
                KeepNonBlank,
                "5511999990001");

            Assert.Equal("5511999990001", result.Name);
            Assert.Equal(SelfDisplayNameResolution.PhonePlaceholderSource, result.Source);
        }

        [Fact]
        public void With_nothing_at_all_the_name_is_empty_and_the_source_unknown()
        {
            SelfDisplayNameResolution.Result result = SelfDisplayNameResolution.Pick(
                new[] { Source("phone", null) },
                KeepNonBlank,
                " ");

            Assert.Equal(string.Empty, result.Name);
            Assert.Null(result.Source);
        }

        [Fact]
        public void The_established_name_prefers_what_the_sidebar_shows()
        {
            Assert.Equal("Ana", SelfDisplayNameResolution.Established("Ana", "Old", _ => false));
        }

        [Fact]
        public void The_credentials_name_counts_unless_it_is_our_own_number()
        {
            Assert.Equal("Ana", SelfDisplayNameResolution.Established(null, "Ana", _ => false));
            Assert.Null(SelfDisplayNameResolution.Established(" ", "5511999990001", _ => true));
            Assert.Null(SelfDisplayNameResolution.Established(null, null, _ => false));
        }
    }
}
