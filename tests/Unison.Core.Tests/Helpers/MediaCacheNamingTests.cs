// =============================================================================
// Tests for MediaCacheNaming.
//
// The file name decides whether cached media is found again. A name that is not
// stable re-downloads on every open; a name that is not unique serves the wrong
// file. Both failures are silent.
// =============================================================================
using System;
using System.Text;
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class MediaCacheNamingTests
    {
        private static byte[] Hash(string seed)
        {
            return Encoding.UTF8.GetBytes(seed);
        }

        // --- Why the encoding is rewritten -------------------------------------

        [Fact]
        public void The_characters_base64_uses_that_a_file_name_cannot_are_replaced()
        {
            // A content hash is base64, which contains '+' and '/'. The second one would be
            // read as a directory separator.
            string encoded = MediaCacheNaming.ToUrlSafeBase64("ab+cd/ef==");

            Assert.Equal("ab-cd_ef", encoded);
            Assert.DoesNotContain("+", encoded);
            Assert.DoesNotContain("/", encoded);
        }

        [Fact]
        public void Padding_is_dropped_rather_than_escaped()
        {
            Assert.Equal("abcd", MediaCacheNaming.ToUrlSafeBase64("abcd=="));
        }

        [Fact]
        public void Both_ways_of_holding_a_hash_produce_the_same_name()
        {
            // One caller has the hash as bytes, another already base64-encoded it. They
            // must agree, or the same media is cached twice.
            byte[] raw = Hash("the-same-media");
            string asBase64 = Convert.ToBase64String(raw);

            Assert.Equal(
                MediaCacheNaming.ToUrlSafeBase64(raw),
                MediaCacheNaming.ToUrlSafeBase64(asBase64));
        }

        // --- Stability, which is the whole point ---------------------------------

        [Fact]
        public void The_same_media_always_resolves_to_the_same_name()
        {
            // If this ever stopped holding, every open would re-download.
            Assert.Equal(
                MediaCacheNaming.ResolveFileBase(Hash("photo"), "message-1"),
                MediaCacheNaming.ResolveFileBase(Hash("photo"), "message-1"));
        }

        [Fact]
        public void The_content_decides_the_name_not_the_message_it_arrived_in()
        {
            // The same photo forwarded into two conversations is one file on disk.
            Assert.Equal(
                MediaCacheNaming.ResolveFileBase(Hash("photo"), "message-1"),
                MediaCacheNaming.ResolveFileBase(Hash("photo"), "message-2"));
        }

        [Fact]
        public void Different_media_never_shares_a_name()
        {
            Assert.NotEqual(
                MediaCacheNaming.ResolveFileBase(Hash("photo"), "message-1"),
                MediaCacheNaming.ResolveFileBase(Hash("другое"), "message-1"));
        }

        // --- Falling back ----------------------------------------------------------

        [Fact]
        public void Without_a_hash_the_message_id_keeps_the_file_identifiable()
        {
            Assert.Equal("message-1", MediaCacheNaming.ResolveFileBase((byte[])null!, "message-1"));
            Assert.Equal("message-1", MediaCacheNaming.ResolveFileBase(new byte[0], "message-1"));
        }

        [Fact]
        public void With_nothing_to_go_on_the_name_is_random_rather_than_shared()
        {
            // Two unrelated files falling back to one fixed name would serve each other's
            // content. A random name is the safe failure.
            string first = MediaCacheNaming.ResolveFileBase((byte[])null!, null!);
            string second = MediaCacheNaming.ResolveFileBase((byte[])null!, null!);

            Assert.NotEqual(first, second);
            Assert.NotEmpty(first);
        }

        // --- Sanitizing ---------------------------------------------------------------

        [Fact]
        public void Anything_that_cannot_appear_in_a_file_name_is_replaced()
        {
            Assert.Equal("a_b_c_d", MediaCacheNaming.Sanitize("a/b:c*d"));
        }

        [Fact]
        public void A_path_traversal_attempt_cannot_survive_sanitizing()
        {
            // Media names partly derive from server-supplied values, so this has to hold.
            string sanitized = MediaCacheNaming.Sanitize("../../windows/system32");

            Assert.DoesNotContain("..", sanitized);
            Assert.DoesNotContain("/", sanitized);
        }

        [Fact]
        public void A_very_long_name_is_capped_so_the_full_path_stays_usable()
        {
            string sanitized = MediaCacheNaming.Sanitize(new string('a', 500));

            Assert.Equal(MediaCacheNaming.MaxFileBaseLength, sanitized.Length);
        }

        [Fact]
        public void Hyphen_and_underscore_survive_because_the_encoding_produces_them()
        {
            Assert.Equal("ab-cd_ef", MediaCacheNaming.Sanitize("ab-cd_ef"));
        }
    }
}
