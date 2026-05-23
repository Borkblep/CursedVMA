// Phase 18 tests: SetCurrentFrameIndex and VK_EXT_memory_budget integration
// in GetHeapBudgets.

using Silk.NET.Vulkan;
using Xunit;

namespace CursedVMA.Tests
{
    // ── Shared fixture ────────────────────────────────────────────────────────

    internal sealed class Phase18Fixture
    {
        public FakeVulkanFunctions Fake { get; }
        public VmaAllocator Allocator { get; }
        public PhysicalDevice Pd { get; }
        public Device Dev { get; }

        public Phase18Fixture(VmaAllocatorCreateFlags extraFlags = VmaAllocatorCreateFlags.None)
        {
            Pd  = new PhysicalDevice(1);
            Dev = new Device(2);

            Fake = new FakeVulkanFunctions
            {
                MemoryProperties         = BuildMemProps(),
                BufferMemoryRequirements = new MemoryRequirements
                {
                    Size           = 4096,
                    Alignment      = 1,
                    MemoryTypeBits = 0b1,
                },
            };

            var createInfo = new VmaAllocatorCreateInfo
            {
                PhysicalDevice = Pd,
                Device         = Dev,
                Flags          = extraFlags,
            };
            VmaAllocator.Create(Fake, in createInfo, out var alloc);
            Allocator = alloc!;
        }

        private static PhysicalDeviceMemoryProperties BuildMemProps()
        {
            var mp = new PhysicalDeviceMemoryProperties
            {
                MemoryTypeCount = 1,
                MemoryHeapCount = 1,
            };
            mp.MemoryTypes.Element0 = new MemoryType
            {
                PropertyFlags = MemoryPropertyFlags.DeviceLocalBit,
                HeapIndex     = 0,
            };
            mp.MemoryHeaps.Element0 = new MemoryHeap
            {
                Size  = 4096ul * 1024 * 1024,
                Flags = MemoryHeapFlags.DeviceLocalBit,
            };
            return mp;
        }
    }

    // ── SetCurrentFrameIndex ─────────────────────────────────────────────────

    public sealed class VmaSetCurrentFrameIndexTests
    {
        [Fact]
        public void SetCurrentFrameIndex_StoresValue()
        {
            var f = new Phase18Fixture();
            f.Allocator.SetCurrentFrameIndex(42);
            Assert.Equal(42u, f.Allocator.CurrentFrameIndex);
            f.Allocator.Dispose();
        }

        [Fact]
        public void SetCurrentFrameIndex_UpdatesRepeatedly()
        {
            var f = new Phase18Fixture();
            f.Allocator.SetCurrentFrameIndex(1);
            f.Allocator.SetCurrentFrameIndex(2);
            f.Allocator.SetCurrentFrameIndex(999);
            Assert.Equal(999u, f.Allocator.CurrentFrameIndex);
            f.Allocator.Dispose();
        }

        [Fact]
        public void CurrentFrameIndex_DefaultsToZero()
        {
            var f = new Phase18Fixture();
            Assert.Equal(0u, f.Allocator.CurrentFrameIndex);
            f.Allocator.Dispose();
        }
    }

    // ── GetHeapBudgets — estimate path (no ExtMemoryBudgetBit) ───────────────

    public sealed class VmaGetHeapBudgetsEstimateTests
    {
        [Fact]
        public void GetHeapBudgets_DoesNotCallGetProperties2_WhenNoBudgetFlag()
        {
            var f = new Phase18Fixture();
            var budgets = new VmaBudget[1];
            f.Allocator.GetHeapBudgets(budgets);

            Assert.Equal(0, f.Fake.GetPhysicalDeviceMemoryProperties2CallCount);
            f.Allocator.Dispose();
        }

        [Fact]
        public void GetHeapBudgets_BudgetIs80Percent_WhenNoBudgetFlag()
        {
            var f = new Phase18Fixture();
            var budgets = new VmaBudget[1];
            f.Allocator.GetHeapBudgets(budgets);

            ulong heapSize = f.Fake.MemoryProperties.MemoryHeaps[0].Size;
            ulong expected80 = heapSize * 8 / 10;
            Assert.Equal(expected80, budgets[0].Budget);
            f.Allocator.Dispose();
        }

        [Fact]
        public void GetHeapBudgets_UsageIsZero_WhenNoAllocations()
        {
            var f = new Phase18Fixture();
            var budgets = new VmaBudget[1];
            f.Allocator.GetHeapBudgets(budgets);

            Assert.Equal(0ul, budgets[0].Usage);
            f.Allocator.Dispose();
        }
    }

    // ── GetHeapBudgets — real budget path (ExtMemoryBudgetBit) ───────────────

    public sealed class VmaGetHeapBudgetsRealBudgetTests
    {
        private Phase18Fixture MakeBudgetFixture()
            => new Phase18Fixture(VmaAllocatorCreateFlags.ExtMemoryBudgetBit);

        [Fact]
        public void GetHeapBudgets_CallsGetProperties2_WhenExtBudgetFlagSet()
        {
            var f = MakeBudgetFixture();
            var budgets = new VmaBudget[1];
            f.Allocator.GetHeapBudgets(budgets);

            Assert.Equal(1, f.Fake.GetPhysicalDeviceMemoryProperties2CallCount);
            f.Allocator.Dispose();
        }

        [Fact]
        public void GetHeapBudgets_UsesRealBudgetValues_WhenExtBudgetFlagSet()
        {
            var f = MakeBudgetFixture();
            unsafe
            {
                var fake = f.Fake.FakeBudgetProperties;
                fake.HeapBudget[0] = 1_234_567_890ul;
                f.Fake.FakeBudgetProperties = fake;
            }

            var budgets = new VmaBudget[1];
            f.Allocator.GetHeapBudgets(budgets);

            Assert.Equal(1_234_567_890ul, budgets[0].Budget);
            f.Allocator.Dispose();
        }

        [Fact]
        public void GetHeapBudgets_UsesRealUsageValues_WhenExtBudgetFlagSet()
        {
            var f = MakeBudgetFixture();
            unsafe
            {
                var fake = f.Fake.FakeBudgetProperties;
                fake.HeapUsage[0] = 42_000_000ul;
                f.Fake.FakeBudgetProperties = fake;
            }

            var budgets = new VmaBudget[1];
            f.Allocator.GetHeapBudgets(budgets);

            Assert.Equal(42_000_000ul, budgets[0].Usage);
            f.Allocator.Dispose();
        }

        [Fact]
        public void GetHeapBudgets_DoesNotUse80PercentEstimate_WhenExtBudgetFlagSet()
        {
            var f = MakeBudgetFixture();
            unsafe
            {
                var fake = f.Fake.FakeBudgetProperties;
                fake.HeapBudget[0] = 999ul;
                f.Fake.FakeBudgetProperties = fake;
            }

            var budgets = new VmaBudget[1];
            f.Allocator.GetHeapBudgets(budgets);

            ulong heapSize = f.Fake.MemoryProperties.MemoryHeaps[0].Size;
            ulong estimate = heapSize * 8 / 10;
            Assert.NotEqual(estimate, budgets[0].Budget);
            Assert.Equal(999ul, budgets[0].Budget);
            f.Allocator.Dispose();
        }

        [Fact]
        public void GetHeapBudgets_StatisticsArePopulated_WhenExtBudgetFlagSet()
        {
            var f = MakeBudgetFixture();
            // Make an allocation so block bytes are non-zero.
            var req = new MemoryRequirements { Size = 4096, Alignment = 1, MemoryTypeBits = 1 };
            var ci  = new VmaAllocationCreateInfo
            {
                Flags = VmaAllocationCreateFlags.DedicatedMemoryBit,
            };
            f.Allocator.AllocateMemory(in req, in ci, out var alloc);

            var budgets = new VmaBudget[1];
            f.Allocator.GetHeapBudgets(budgets);

            Assert.True(budgets[0].Statistics.BlockBytes > 0);

            f.Allocator.FreeMemory(alloc!);
            f.Allocator.Dispose();
        }
    }
}
