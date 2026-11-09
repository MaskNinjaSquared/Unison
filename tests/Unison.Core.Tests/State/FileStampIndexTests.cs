using Unison.Core.State;

namespace Unison.Core.Tests.State
{
    public sealed class FileStampIndexTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Before_loading_the_index_does_not_answer()
        {
            var index = new FileStampIndex();

            Assert.Null(index.Lookup("a.jpg"));
        }

        [Fact]
        public void A_loaded_index_answers_hits_and_misses_without_the_disk()
        {
            var index = new FileStampIndex();
            index.Load(new[] { new KeyValuePair<string, DateTime>("a.jpg", T0) });

            Assert.Equal(T0, index.Lookup("A.JPG")!.Value.WrittenUtc);
            Assert.True(index.Lookup("a.jpg")!.Value.Exists);
            Assert.False(index.Lookup("b.jpg")!.Value.Exists);
        }

        [Fact]
        public void Save_and_delete_keep_the_index_current()
        {
            var index = new FileStampIndex();
            index.Load(Array.Empty<KeyValuePair<string, DateTime>>());

            index.Set("a.jpg", T0);
            Assert.True(index.Lookup("a.jpg")!.Value.Exists);

            index.Remove("a.jpg");
            Assert.False(index.Lookup("a.jpg")!.Value.Exists);
        }

        [Fact]
        public void Writes_before_the_load_finishes_survive_it()
        {
            var index = new FileStampIndex();

            index.Set("new.jpg", T0.AddHours(1));
            index.Remove("gone.jpg");
            index.Load(new[]
            {
                new KeyValuePair<string, DateTime>("gone.jpg", T0),
                new KeyValuePair<string, DateTime>("new.jpg", T0),
            });

            Assert.Equal(T0.AddHours(1), index.Lookup("new.jpg")!.Value.WrittenUtc);
            Assert.False(index.Lookup("gone.jpg")!.Value.Exists);
        }
    }
}
