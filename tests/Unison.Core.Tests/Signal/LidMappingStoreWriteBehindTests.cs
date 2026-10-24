using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Socket.Abstractions;
using Unison.Socket.Signal;
using Xunit;

namespace Unison.Core.Tests.Signal
{
    public sealed class LidMappingStoreWriteBehindTests
    {
        [Fact]
        public async Task Write_through_without_policy_still_hits_storage_immediately()
        {
            var storage = new CountingLidMappingStorage();
            var store = new LidMappingStore(storage);

            await store.StoreMappingsAsync(new[]
            {
                new LidMapping("111@lid", "5511111111111@s.whatsapp.net")
            });

            Assert.Equal(1, storage.SetCount);
            Assert.True(store.TryGetCachedPairedJid("111@lid", out string pn));
            Assert.Equal("5511111111111@s.whatsapp.net", pn);
        }

        [Fact]
        public async Task Suppressed_gate_queues_without_SetAsync_but_fills_cache()
        {
            var storage = new CountingLidMappingStorage();
            var policy = new FixedPolicy(batchSize: 40, delayMs: 1500);
            var store = new LidMappingStore(storage, policy: policy);
            var armed = new List<int>();
            store.ArmDebounce = ms => armed.Add(ms);

            await store.StoreMappingsAsync(new[]
            {
                new LidMapping("111@lid", "5511111111111@s.whatsapp.net"),
                new LidMapping("222@lid", "5511222222222@s.whatsapp.net")
            });

            Assert.Equal(0, storage.SetCount);
            Assert.Empty(armed);
            Assert.True(store.TryGetCachedPairedJid("111@lid", out _));
            Assert.True(store.TryGetCachedPairedJid("222@lid", out _));
        }

        [Fact]
        public async Task EnableDiskPersist_flushes_debt_accumulated_while_held()
        {
            var storage = new CountingLidMappingStorage();
            var store = new LidMappingStore(storage, policy: new FixedPolicy(40, 1500));

            await store.StoreMappingsAsync(new[]
            {
                new LidMapping("111@lid", "5511111111111@s.whatsapp.net")
            });
            Assert.Equal(0, storage.SetCount);

            await store.EnableDiskPersistAsync("test");

            Assert.Equal(1, storage.SetCount);
            IDictionary<string, string> all = await storage.GetAllAsync();
            Assert.Equal("111", all["5511111111111"]);
            Assert.Equal("5511111111111", all["111_reverse"]);
        }

        [Fact]
        public async Task After_release_batch_size_flushes_without_waiting_for_timer()
        {
            var storage = new CountingLidMappingStorage();
            var store = new LidMappingStore(storage, policy: new FixedPolicy(batchSize: 2, delayMs: 5000));
            var armed = new List<int>();
            store.ArmDebounce = ms => armed.Add(ms);
            await store.EnableDiskPersistAsync("test");

            await store.StoreMappingsAsync(new[]
            {
                new LidMapping("111@lid", "5511111111111@s.whatsapp.net")
            });
            Assert.Equal(0, storage.SetCount);
            Assert.Equal(new[] { 5000 }, armed);

            await store.StoreMappingsAsync(new[]
            {
                new LidMapping("222@lid", "5511222222222@s.whatsapp.net")
            });

            Assert.Equal(1, storage.SetCount);
        }

        [Fact]
        public async Task After_release_sub_batch_arms_debounce_delay()
        {
            var storage = new CountingLidMappingStorage();
            var store = new LidMappingStore(storage, policy: new FixedPolicy(batchSize: 10, delayMs: 1200));
            var armed = new List<int>();
            store.ArmDebounce = ms => armed.Add(ms);
            await store.EnableDiskPersistAsync("test");

            await store.StoreMappingsAsync(new[]
            {
                new LidMapping("111@lid", "5511111111111@s.whatsapp.net")
            });

            Assert.Equal(0, storage.SetCount);
            Assert.Equal(new[] { 1200 }, armed);
        }

        [Fact]
        public async Task FlushPending_while_gate_held_still_writes_for_suspend()
        {
            var storage = new CountingLidMappingStorage();
            var store = new LidMappingStore(storage, policy: new FixedPolicy(40, 1500));

            await store.StoreMappingsAsync(new[]
            {
                new LidMapping("111@lid", "5511111111111@s.whatsapp.net")
            });
            Assert.Equal(0, storage.SetCount);

            await store.FlushPendingToDiskAsync();

            Assert.Equal(1, storage.SetCount);
        }

        [Fact]
        public async Task Failed_flush_requeues_so_retry_can_land()
        {
            var storage = new CountingLidMappingStorage { FailNextSets = 1 };
            var store = new LidMappingStore(storage, policy: new FixedPolicy(40, 1500));

            await store.StoreMappingsAsync(new[]
            {
                new LidMapping("111@lid", "5511111111111@s.whatsapp.net")
            });

            await Assert.ThrowsAsync<InvalidOperationException>(() => store.EnableDiskPersistAsync("test"));
            Assert.Equal(1, storage.SetCount);
            Assert.True(store.TryGetCachedPairedJid("111@lid", out _));

            await store.FlushPendingToDiskAsync();

            Assert.Equal(2, storage.SetCount);
            IDictionary<string, string> all = await storage.GetAllAsync();
            Assert.Equal("111", all["5511111111111"]);
        }

        [Fact]
        public async Task Duplicate_store_while_held_does_not_double_disk_after_release()
        {
            var storage = new CountingLidMappingStorage();
            var store = new LidMappingStore(storage, policy: new FixedPolicy(40, 1500));
            var pair = new LidMapping("111@lid", "5511111111111@s.whatsapp.net");

            await store.StoreMappingsAsync(new[] { pair });
            await store.StoreMappingsAsync(new[] { pair });
            await store.EnableDiskPersistAsync("test");

            Assert.Equal(1, storage.SetCount);
        }

        [Fact]
        public async Task Second_enable_with_empty_queue_does_not_SetAsync_again()
        {
            var storage = new CountingLidMappingStorage();
            var store = new LidMappingStore(storage, policy: new FixedPolicy(40, 1500));

            await store.StoreMappingsAsync(new[]
            {
                new LidMapping("111@lid", "5511111111111@s.whatsapp.net")
            });
            await store.EnableDiskPersistAsync("first");
            Assert.Equal(1, storage.SetCount);

            await store.EnableDiskPersistAsync("second");
            Assert.Equal(1, storage.SetCount);
        }

        private sealed class FixedPolicy : ILidMappingPersistPolicy
        {
            public FixedPolicy(int batchSize, int delayMs)
            {
                BatchSize = batchSize;
                DelayMs = delayMs;
                MemoryCacheMaxEntries = 1024;
                WarmMaxPairs = 0;
            }

            public int BatchSize { get; }

            public int DelayMs { get; }

            public int MemoryCacheMaxEntries { get; }

            public int WarmMaxPairs { get; }
        }

        private sealed class CountingLidMappingStorage : ILidMappingStorage
        {
            private readonly InMemoryLidMappingStorage _inner = new InMemoryLidMappingStorage();

            public int SetCount { get; private set; }

            public int FailNextSets { get; set; }

            public Task<IDictionary<string, string>> GetAsync(IEnumerable<string> keys) =>
                _inner.GetAsync(keys);

            public Task<IDictionary<string, string>> GetAllAsync() => _inner.GetAllAsync();

            public Task<IDictionary<string, string>> GetRecentForwardPairsAsync(int maxPairs) =>
                _inner.GetRecentForwardPairsAsync(maxPairs);

            public Task SetAsync(IDictionary<string, string> values)
            {
                SetCount++;
                if (FailNextSets > 0)
                {
                    FailNextSets--;
                    throw new InvalidOperationException("disk busy");
                }

                return _inner.SetAsync(values);
            }
        }
    }
}
