// =============================================================================
// AppStateSyncNodes
//
// Builds the request that asks for a collection's changes, and reads the answer.
//
// The request says where we are; the answer holds either patches to apply or,
// when we are too far behind, a snapshot to start over from. Asking with
// return_snapshot on version zero is what makes a fresh device get the whole
// picture in one go instead of replaying years of history.
//
// One quirk is carried over deliberately: a patch that arrives without a version
// is taken to be the collection's version plus one. The server omits it when the
// value is implied, and guessing wrong here would fail the MAC check.
//
// Ports: rc14 extractSyncdPatches and the resync query in src/Utils/chat-utils.ts
// =============================================================================
using System.Collections.Generic;
using Unison.Baileys.Protocol;
using Unison.Socket.WABinary;

namespace Unison.Socket.AppState
{
    /// <summary>What the server returned for one collection.</summary>
    public sealed class AppStateCollectionChunk
    {
        public AppStateCollectionChunk()
        {
            Patches = new List<global::Proto.SyncdPatch>();
        }

        public string Name { get; set; }

        /// <summary>True when the server is holding more and the query has to be repeated.</summary>
        public bool HasMorePatches { get; set; }

        /// <summary>Present when the server chose to answer with a fresh snapshot.</summary>
        public global::Proto.ExternalBlobReference SnapshotReference { get; set; }

        public IList<global::Proto.SyncdPatch> Patches { get; private set; }
    }

    public static class AppStateSyncNodes
    {
        public const string Namespace = "w:sync:app:state";

        /// <summary>
        /// Asks for everything after the versions we hold. A collection at version zero also asks
        /// for a snapshot, which is the only way to bootstrap without replaying every patch ever.
        /// </summary>
        public static BinaryNode BuildQuery(IDictionary<string, long> versions)
        {
            var collections = new List<BinaryNode>();

            foreach (var pair in versions)
            {
                collections.Add(new BinaryNode("collection", new Dictionary<string, string>
                {
                    { "name", pair.Key },
                    { "version", pair.Value.ToString() },
                    { "return_snapshot", (pair.Value == 0).ToString().ToLowerInvariant() }
                }));
            }

            return new BinaryNode(
                "iq",
                new Dictionary<string, string>
                {
                    { "to", JidUtils.ServerWhatsApp },
                    { "xmlns", Namespace },
                    { "type", "set" }
                },
                new List<BinaryNode>
                {
                    new BinaryNode("sync", new Dictionary<string, string>(), collections)
                });
        }

        public static IList<AppStateCollectionChunk> Extract(BinaryNode result)
        {
            var chunks = new List<AppStateCollectionChunk>();
            if (result == null)
            {
                return chunks;
            }

            var sync = result.GetChild("sync");
            if (sync == null)
            {
                return chunks;
            }

            foreach (var collection in sync.GetChildren("collection"))
            {
                var chunk = new AppStateCollectionChunk
                {
                    Name = collection.GetAttribute("name"),
                    HasMorePatches = collection.GetAttribute("has_more_patches") == "true"
                };

                var snapshot = collection.GetChild("snapshot");
                if (snapshot != null)
                {
                    var content = snapshot.GetContentBytes();
                    if (content != null && content.Length > 0)
                    {
                        chunk.SnapshotReference = global::Proto.ExternalBlobReference.Parser.ParseFrom(content);
                    }
                }

                long collectionVersion;
                long.TryParse(collection.GetAttribute("version"), out collectionVersion);

                var patchesNode = collection.GetChild("patches");
                var patches = (patchesNode ?? collection).GetChildren("patch");

                foreach (var patch in patches)
                {
                    var content = patch.GetContentBytes();
                    if (content == null || content.Length == 0)
                    {
                        continue;
                    }

                    var decoded = global::Proto.SyncdPatch.Parser.ParseFrom(content);

                    if (decoded.Version == null || decoded.Version.Version == 0)
                    {
                        decoded.Version = new global::Proto.SyncdVersion
                        {
                            Version = unchecked((ulong)(collectionVersion + 1))
                        };
                    }

                    chunk.Patches.Add(decoded);
                }

                chunks.Add(chunk);
            }

            return chunks;
        }
    }
}
