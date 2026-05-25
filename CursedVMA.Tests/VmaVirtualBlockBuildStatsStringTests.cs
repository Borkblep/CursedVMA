// Tests for VmaVirtualBlock.BuildStatsString (vmaBuildVirtualBlockStatsString).

using System.Text.Json;
using Silk.NET.Vulkan;
using Xunit;

namespace CursedVMA.Tests
{
    public sealed class VmaVirtualBlockBuildStatsStringTests
    {
        private static VmaVirtualBlock MakeBlock(ulong size = 65536)
        {
            var info = new VmaVirtualBlockCreateInfo { Size = size };
            VmaVirtualBlock.Create(in info, out var block);
            return block!;
        }

        private static VmaVirtualAllocation Alloc(
            VmaVirtualBlock block, ulong size, object? userData = null)
        {
            var ci = new VmaVirtualAllocationCreateInfo
            {
                Size     = size,
                UserData = userData,
            };
            block.Allocate(in ci, out var a, out _);
            return a;
        }

        [Fact]
        public void Empty_DetailedMapFalse_ProducesValidJson()
        {
            using var block = MakeBlock();
            string json = block.BuildStatsString(detailedMap: false);

            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            Assert.Equal(JsonValueKind.Object, root.ValueKind);

            // Detailed-stats fields are flat in the outer object, matching VMA's
            // vmaBuildVirtualBlockStatsString output.
            Assert.True(root.TryGetProperty("BlockCount",         out _));
            Assert.True(root.TryGetProperty("BlockBytes",         out _));
            Assert.True(root.TryGetProperty("AllocationCount",    out _));
            Assert.True(root.TryGetProperty("AllocationBytes",    out _));
            Assert.True(root.TryGetProperty("UnusedRangeCount",   out _));
            Assert.True(root.TryGetProperty("AllocationSizeMin",  out _));
            Assert.True(root.TryGetProperty("AllocationSizeMax",  out _));
            Assert.True(root.TryGetProperty("UnusedRangeSizeMin", out _));
            Assert.True(root.TryGetProperty("UnusedRangeSizeMax", out _));

            // No detailed map → no Details section.
            Assert.False(root.TryGetProperty("Details", out _));
        }

        [Fact]
        public void Empty_ReportsBlockBytesEqualToSize()
        {
            using var block = MakeBlock(size: 4096);
            string json = block.BuildStatsString(detailedMap: false);
            using JsonDocument doc = JsonDocument.Parse(json);

            Assert.Equal(4096ul, doc.RootElement.GetProperty("BlockBytes").GetUInt64());
            Assert.Equal(0ul,    doc.RootElement.GetProperty("AllocationCount").GetUInt64());
            Assert.Equal(0ul,    doc.RootElement.GetProperty("AllocationBytes").GetUInt64());
        }

        [Fact]
        public void AfterAllocations_ReportsAllocationCountAndBytes()
        {
            using var block = MakeBlock();
            Alloc(block, 128);
            Alloc(block, 256);
            Alloc(block, 512);

            string json = block.BuildStatsString(detailedMap: false);
            using JsonDocument doc = JsonDocument.Parse(json);

            Assert.Equal(3ul,   doc.RootElement.GetProperty("AllocationCount").GetUInt64());
            Assert.Equal(896ul, doc.RootElement.GetProperty("AllocationBytes").GetUInt64());
        }

        [Fact]
        public void DetailedMapTrue_IncludesDetailsAndSuballocations()
        {
            using var block = MakeBlock();
            Alloc(block, 64);
            Alloc(block, 128);

            string json = block.BuildStatsString(detailedMap: true);
            using JsonDocument doc = JsonDocument.Parse(json);

            Assert.True(doc.RootElement.TryGetProperty("Details", out JsonElement details));
            Assert.True(details.TryGetProperty("Suballocations", out JsonElement subs));
            Assert.Equal(JsonValueKind.Array, subs.ValueKind);
            Assert.Equal(2, subs.GetArrayLength());

            foreach (JsonElement entry in subs.EnumerateArray())
            {
                Assert.True(entry.TryGetProperty("Offset", out _));
                Assert.True(entry.TryGetProperty("Size",   out _));
            }
        }

        [Fact]
        public void DetailedMapTrue_EmitsUserDataWhenItIsAString()
        {
            using var block = MakeBlock();
            Alloc(block, 64, userData: "named-alloc");
            Alloc(block, 64, userData: 42); // non-string user-data — not emitted

            string json = block.BuildStatsString(detailedMap: true);
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement subs = doc.RootElement
                .GetProperty("Details").GetProperty("Suballocations");

            int withUserData = 0;
            foreach (JsonElement entry in subs.EnumerateArray())
            {
                if (entry.TryGetProperty("UserData", out JsonElement ud))
                {
                    Assert.Equal("named-alloc", ud.GetString());
                    withUserData++;
                }
            }
            Assert.Equal(1, withUserData);
        }

        [Fact]
        public void DetailedMapFalse_OmitsSuballocations()
        {
            using var block = MakeBlock();
            Alloc(block, 64);

            string json = block.BuildStatsString(detailedMap: false);
            using JsonDocument doc = JsonDocument.Parse(json);
            Assert.False(doc.RootElement.TryGetProperty("Details", out _));
        }

        [Fact]
        public void AfterDispose_ThrowsObjectDisposedException()
        {
            var block = MakeBlock();
            block.Dispose();
            Assert.Throws<System.ObjectDisposedException>(
                () => block.BuildStatsString(detailedMap: false));
        }

        [Fact]
        public void LinearAlgorithm_SameJsonShape()
        {
            var info = new VmaVirtualBlockCreateInfo
            {
                Size  = 65536,
                Flags = VmaVirtualBlockCreateFlags.LinearAlgorithmBit,
            };
            VmaVirtualBlock.Create(in info, out var block);

            var ci = new VmaVirtualAllocationCreateInfo { Size = 128 };
            block!.Allocate(in ci, out _, out _);

            string json = block.BuildStatsString(detailedMap: true);
            using JsonDocument doc = JsonDocument.Parse(json);

            Assert.Equal(1ul, doc.RootElement.GetProperty("AllocationCount").GetUInt64());
            Assert.Equal(1,   doc.RootElement
                .GetProperty("Details").GetProperty("Suballocations").GetArrayLength());

            block.Dispose();
        }
    }
}
