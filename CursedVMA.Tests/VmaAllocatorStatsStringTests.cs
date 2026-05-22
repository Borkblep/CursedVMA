// Tests for Phase 12: GetAllocatorInfo and BuildStatsString.

using Silk.NET.Vulkan;
using System.Text.Json;
using Xunit;

namespace CursedVMA.Tests
{
    public sealed class VmaAllocatorGetAllocatorInfoTests
    {
        private static readonly Instance       s_Instance       = new Instance((nint)42);
        private static readonly PhysicalDevice s_PhysicalDevice = new PhysicalDevice((nint)17);
        private static readonly Device         s_Device         = new Device((nint)23);

        private static VmaAllocator MakeAllocator()
        {
            var vk = new FakeVulkanFunctions();
            var memProps = new PhysicalDeviceMemoryProperties();
            memProps.MemoryTypeCount = 1;
            memProps.MemoryTypes.Element0 = new MemoryType
            {
                PropertyFlags = MemoryPropertyFlags.DeviceLocalBit,
                HeapIndex = 0,
            };
            memProps.MemoryHeapCount = 1;
            memProps.MemoryHeaps.Element0 = new MemoryHeap { Size = 1ul * 1024 * 1024 * 1024 };
            vk.MemoryProperties = memProps;

            var info = new VmaAllocatorCreateInfo
            {
                Instance       = s_Instance,
                PhysicalDevice = s_PhysicalDevice,
                Device         = s_Device,
            };
            VmaAllocator.Create(vk, in info, out var allocator);
            return allocator!;
        }

        [Fact]
        public void GetAllocatorInfo_ReturnsHandlesPassedAtCreate()
        {
            using var allocator = MakeAllocator();

            allocator.GetAllocatorInfo(out VmaAllocatorInfo info);

            Assert.Equal(s_Instance,       info.Instance);
            Assert.Equal(s_PhysicalDevice, info.PhysicalDevice);
            Assert.Equal(s_Device,         info.Device);
        }

        [Fact]
        public void GetAllocatorInfo_AfterDispose_Throws()
        {
            var allocator = MakeAllocator();
            allocator.Dispose();

            Assert.Throws<System.ObjectDisposedException>(
                () => allocator.GetAllocatorInfo(out _));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────

    public sealed class VmaAllocatorBuildStatsStringTests
    {
        private static readonly Instance       s_Instance       = new Instance((nint)1);
        private static readonly PhysicalDevice s_PhysicalDevice = new PhysicalDevice((nint)1);
        private static readonly Device         s_Device         = new Device((nint)1);

        private static (FakeVulkanFunctions vk, VmaAllocator allocator) MakeAllocator()
        {
            var vk = new FakeVulkanFunctions();
            var memProps = new PhysicalDeviceMemoryProperties();
            memProps.MemoryTypeCount = 2;
            memProps.MemoryTypes.Element0 = new MemoryType
            {
                PropertyFlags = MemoryPropertyFlags.DeviceLocalBit,
                HeapIndex = 0,
            };
            memProps.MemoryTypes.Element1 = new MemoryType
            {
                PropertyFlags = MemoryPropertyFlags.HostVisibleBit
                              | MemoryPropertyFlags.HostCoherentBit,
                HeapIndex = 1,
            };
            memProps.MemoryHeapCount = 2;
            memProps.MemoryHeaps.Element0 = new MemoryHeap
            {
                Size  = 8ul * 1024 * 1024 * 1024,
                Flags = MemoryHeapFlags.DeviceLocalBit,
            };
            memProps.MemoryHeaps.Element1 = new MemoryHeap { Size = 4ul * 1024 * 1024 * 1024 };
            vk.MemoryProperties = memProps;

            // Vulkan 1.3 packed.
            uint apiVersion = (1u << 22) | (3u << 12);
            vk.DeviceProperties = new PhysicalDeviceProperties
            {
                Limits = new PhysicalDeviceLimits
                {
                    MaxMemoryAllocationCount = 4096,
                    BufferImageGranularity   = 1024,
                    NonCoherentAtomSize      = 64,
                },
            };

            var info = new VmaAllocatorCreateInfo
            {
                Instance         = s_Instance,
                PhysicalDevice   = s_PhysicalDevice,
                Device           = s_Device,
                VulkanApiVersion = apiVersion,
            };
            VmaAllocator.Create(vk, in info, out var allocator);
            return (vk, allocator!);
        }

        // ── Basic structural validity ────────────────────────────────────────

        [Fact]
        public void BuildStatsString_EmptyAllocator_ProducesValidJson()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            string json = allocator.BuildStatsString(detailedMap: false);

            // Must parse as JSON.
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            Assert.Equal(JsonValueKind.Object, root.ValueKind);
        }

        [Fact]
        public void BuildStatsString_ContainsAllTopLevelSections()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            string json = allocator.BuildStatsString(detailedMap: false);
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            Assert.True(root.TryGetProperty("General",     out _));
            Assert.True(root.TryGetProperty("Total",       out _));
            Assert.True(root.TryGetProperty("MemoryHeaps", out _));
            Assert.True(root.TryGetProperty("MemoryTypes", out _));
            Assert.True(root.TryGetProperty("Pools",       out _));
        }

        // ── General section ──────────────────────────────────────────────────

        [Fact]
        public void BuildStatsString_General_ReportsApiVersion()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            string json = allocator.BuildStatsString(detailedMap: false);
            using var doc = JsonDocument.Parse(json);
            JsonElement general = doc.RootElement.GetProperty("General");

            Assert.Equal("Vulkan",  general.GetProperty("API").GetString());
            Assert.Equal("1.3.0",   general.GetProperty("apiVersion").GetString());
            Assert.Equal(2u,        general.GetProperty("memoryHeapCount").GetUInt32());
            Assert.Equal(2u,        general.GetProperty("memoryTypeCount").GetUInt32());
        }

        [Fact]
        public void BuildStatsString_General_ReportsDeviceLimits()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            string json = allocator.BuildStatsString(detailedMap: false);
            using var doc = JsonDocument.Parse(json);
            JsonElement general = doc.RootElement.GetProperty("General");

            Assert.Equal(4096u, general.GetProperty("maxMemoryAllocationCount").GetUInt32());
            Assert.Equal(1024u, general.GetProperty("bufferImageGranularity").GetUInt32());
            Assert.Equal(64u,   general.GetProperty("nonCoherentAtomSize").GetUInt32());
        }

        // ── MemoryHeaps section ──────────────────────────────────────────────

        [Fact]
        public void BuildStatsString_MemoryHeaps_OneEntryPerHeap()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            string json = allocator.BuildStatsString(detailedMap: false);
            using var doc = JsonDocument.Parse(json);
            JsonElement heaps = doc.RootElement.GetProperty("MemoryHeaps");

            Assert.Equal(JsonValueKind.Array, heaps.ValueKind);
            Assert.Equal(2, heaps.GetArrayLength());

            JsonElement heap0 = heaps[0];
            Assert.Equal(0u, heap0.GetProperty("Index").GetUInt32());
            Assert.Equal(8ul * 1024 * 1024 * 1024, heap0.GetProperty("Size").GetUInt64());

            JsonElement flags = heap0.GetProperty("Flags");
            Assert.Equal(JsonValueKind.Array, flags.ValueKind);
            Assert.Equal("DeviceLocal", flags[0].GetString());
        }

        [Fact]
        public void BuildStatsString_MemoryHeaps_IncludeBudget()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            string json = allocator.BuildStatsString(detailedMap: false);
            using var doc = JsonDocument.Parse(json);
            JsonElement heap0 = doc.RootElement.GetProperty("MemoryHeaps")[0];

            // Budget fallback is 80% of heap size.
            Assert.Equal((8ul * 1024 * 1024 * 1024) * 8 / 10,
                heap0.GetProperty("BudgetBytes").GetUInt64());
            Assert.Equal(0ul, heap0.GetProperty("UsageBytes").GetUInt64());
        }

        // ── MemoryTypes section ──────────────────────────────────────────────

        [Fact]
        public void BuildStatsString_MemoryTypes_FlagsReflectPropertyBits()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            string json = allocator.BuildStatsString(detailedMap: false);
            using var doc = JsonDocument.Parse(json);
            JsonElement types = doc.RootElement.GetProperty("MemoryTypes");

            Assert.Equal(2, types.GetArrayLength());

            JsonElement type1 = types[1];
            JsonElement flags = type1.GetProperty("Flags");

            string[] names = new string[flags.GetArrayLength()];
            for (int i = 0; i < flags.GetArrayLength(); i++)
                names[i] = flags[i].GetString()!;

            Assert.Contains("HostVisible", names);
            Assert.Contains("HostCoherent", names);
        }

        // ── After a regular allocation ───────────────────────────────────────

        [Fact]
        public void BuildStatsString_AfterAllocation_TotalReflectsAllocation()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            var req = new MemoryRequirements
            {
                Size = 2048,
                Alignment = 1,
                MemoryTypeBits = 0b01u,
            };
            var ci = new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.GpuOnly };
            allocator.AllocateMemory(in req, in ci, out var alloc);

            string json = allocator.BuildStatsString(detailedMap: false);
            using var doc = JsonDocument.Parse(json);
            JsonElement total = doc.RootElement.GetProperty("Total");

            Assert.Equal(1u,    total.GetProperty("AllocationCount").GetUInt32());
            Assert.Equal(2048ul, total.GetProperty("AllocationBytes").GetUInt64());

            allocator.FreeMemory(alloc);
        }

        // ── DetailedMap on / off ─────────────────────────────────────────────

        [Fact]
        public void BuildStatsString_DetailedMapFalse_OmitsBlockArrays()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            var req = new MemoryRequirements { Size = 1024, Alignment = 1, MemoryTypeBits = 0b01u };
            var ci  = new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.GpuOnly };
            allocator.AllocateMemory(in req, in ci, out var alloc);

            string json = allocator.BuildStatsString(detailedMap: false);
            using var doc = JsonDocument.Parse(json);
            JsonElement type0 = doc.RootElement.GetProperty("MemoryTypes")[0];

            Assert.False(type0.TryGetProperty("Blocks", out _));
            Assert.False(type0.TryGetProperty("DedicatedAllocations", out _));

            allocator.FreeMemory(alloc);
        }

        [Fact]
        public void BuildStatsString_DetailedMapTrue_IncludesBlocksAndSuballocations()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            var req = new MemoryRequirements { Size = 1024, Alignment = 1, MemoryTypeBits = 0b01u };
            var ci  = new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.GpuOnly };
            allocator.AllocateMemory(in req, in ci, out var alloc);

            string json = allocator.BuildStatsString(detailedMap: true);
            using var doc = JsonDocument.Parse(json);
            JsonElement type0 = doc.RootElement.GetProperty("MemoryTypes")[0];

            JsonElement blocks = type0.GetProperty("Blocks");
            Assert.Equal(JsonValueKind.Array, blocks.ValueKind);
            Assert.True(blocks.GetArrayLength() >= 1);

            JsonElement block0 = blocks[0];
            Assert.True(block0.TryGetProperty("Id", out _));
            Assert.True(block0.TryGetProperty("Size", out _));
            Assert.True(block0.TryGetProperty("FreeBytes", out _));

            JsonElement subs = block0.GetProperty("Suballocations");
            Assert.Equal(JsonValueKind.Array, subs.ValueKind);
            Assert.Equal(1, subs.GetArrayLength()); // exactly one allocation
            Assert.Equal(1024ul, subs[0].GetProperty("Size").GetUInt64());

            allocator.FreeMemory(alloc);
        }

        [Fact]
        public void BuildStatsString_DetailedMapTrue_IncludesDedicatedAllocations()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            var req = new MemoryRequirements { Size = 4096, Alignment = 1, MemoryTypeBits = 0b01u };
            var ci  = new VmaAllocationCreateInfo
            {
                Usage = VmaMemoryUsage.GpuOnly,
                Flags = VmaAllocationCreateFlags.DedicatedMemoryBit,
            };
            allocator.AllocateMemory(in req, in ci, out var alloc);
            allocator.SetAllocationName(alloc!, "TestDedicated");

            string json = allocator.BuildStatsString(detailedMap: true);
            using var doc = JsonDocument.Parse(json);
            JsonElement type0 = doc.RootElement.GetProperty("MemoryTypes")[0];

            JsonElement dedicated = type0.GetProperty("DedicatedAllocations");
            Assert.Equal(JsonValueKind.Array, dedicated.ValueKind);
            Assert.Equal(1, dedicated.GetArrayLength());

            JsonElement d0 = dedicated[0];
            Assert.Equal(4096ul, d0.GetProperty("Size").GetUInt64());
            Assert.Equal("TestDedicated", d0.GetProperty("Name").GetString());

            allocator.FreeMemory(alloc);
        }

        // ── Pools ────────────────────────────────────────────────────────────

        [Fact]
        public void BuildStatsString_WithCustomPool_PoolAppearsInPoolsSection()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            var poolInfo = new VmaPoolCreateInfo
            {
                MemoryTypeIndex = 1,
                BlockSize       = 65536,
                MinBlockCount   = 1,
            };
            allocator.CreatePool(in poolInfo, out var pool);
            pool!.SetName("MyPool");

            string json = allocator.BuildStatsString(detailedMap: true);
            using var doc = JsonDocument.Parse(json);
            JsonElement pools = doc.RootElement.GetProperty("Pools");

            Assert.Equal(JsonValueKind.Array, pools.ValueKind);
            Assert.Equal(1, pools.GetArrayLength());

            JsonElement p0 = pools[0];
            Assert.Equal("MyPool", p0.GetProperty("Name").GetString());
            Assert.Equal(1u,       p0.GetProperty("MemoryTypeIndex").GetUInt32());
            Assert.Equal(65536ul,  p0.GetProperty("BlockSize").GetUInt64());

            JsonElement blocks = p0.GetProperty("Blocks");
            Assert.True(blocks.GetArrayLength() >= 1);

            allocator.DestroyPool(pool);
        }

        [Fact]
        public void BuildStatsString_NoPools_PoolsArrayIsEmpty()
        {
            var (_, allocator) = MakeAllocator();
            using var __ = allocator;

            string json = allocator.BuildStatsString(detailedMap: false);
            using var doc = JsonDocument.Parse(json);
            JsonElement pools = doc.RootElement.GetProperty("Pools");

            Assert.Equal(JsonValueKind.Array, pools.ValueKind);
            Assert.Equal(0, pools.GetArrayLength());
        }

        // ── Disposal ─────────────────────────────────────────────────────────

        [Fact]
        public void BuildStatsString_AfterDispose_Throws()
        {
            var (_, allocator) = MakeAllocator();
            allocator.Dispose();

            Assert.Throws<System.ObjectDisposedException>(
                () => allocator.BuildStatsString(detailedMap: false));
        }

        // ── API-version edge cases ───────────────────────────────────────────

        [Fact]
        public void BuildStatsString_ApiVersionZero_RendersAsZeroZeroZero()
        {
            var vk = new FakeVulkanFunctions();
            var memProps = new PhysicalDeviceMemoryProperties();
            memProps.MemoryTypeCount = 1;
            memProps.MemoryTypes.Element0 = new MemoryType
            {
                PropertyFlags = MemoryPropertyFlags.DeviceLocalBit,
                HeapIndex = 0,
            };
            memProps.MemoryHeapCount = 1;
            memProps.MemoryHeaps.Element0 = new MemoryHeap { Size = 1ul * 1024 * 1024 * 1024 };
            vk.MemoryProperties = memProps;

            var info = new VmaAllocatorCreateInfo
            {
                PhysicalDevice = s_PhysicalDevice,
                Device         = s_Device,
                // VulkanApiVersion left as 0.
            };
            VmaAllocator.Create(vk, in info, out var allocator);
            using var __ = allocator!;

            string json = allocator!.BuildStatsString(detailedMap: false);
            using var doc = JsonDocument.Parse(json);
            JsonElement general = doc.RootElement.GetProperty("General");

            Assert.Equal("0.0.0", general.GetProperty("apiVersion").GetString());
        }
    }
}
