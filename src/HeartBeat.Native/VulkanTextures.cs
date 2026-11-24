using System.Runtime.InteropServices;
using System.Text;
using Valve.VR;

namespace HeartBeat.Native;

/// <summary>
/// Hands CPU-drawn panels to SteamVR as Vulkan textures. SetOverlayRaw flickers on the Steam Frame
/// (as it did on Windows, where Direct3D 11 textures fixed it). One Vulkan device on SteamVR's GPU
/// with the extensions SteamVR asks for; each panel alternates between two device-local images,
/// filled from a mapped staging buffer and left in TRANSFER_SRC_OPTIMAL, the layout OpenVR expects.
/// Written against the Vulkan C API (libvulkan.so.1) directly, no wrapper library.
/// </summary>
internal sealed class VulkanTextures : IDisposable
{
    private IntPtr _instance, _physical, _device, _queue;
    private uint _family;
    private ulong _pool;
    private byte[] _memoryProperties = [];

    private VulkanTextures() { }

    /// <summary>Creates the Vulkan device, or returns null with the reason (then panels use raw uploads).</summary>
    public static VulkanTextures? TryCreate(CVRSystem system, out string? error)
    {
        var v = new VulkanTextures();
        try
        {
            error = v.Init(system);
            if (error == null) return v;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or NullReferenceException)
        {
            error = $"Vulkan unavailable: {e.Message}";
        }
        v.Dispose();
        return null;
    }

    private string? Init(CVRSystem system)
    {
        var compositor = Valve.VR.OpenVR.Compositor ?? throw new NullReferenceException("no IVRCompositor");
        var sb = new StringBuilder(4096);
        compositor.GetVulkanInstanceExtensionsRequired(sb, (uint)sb.Capacity);
        var instanceExt = Split(sb.ToString());

        using var appName = new Utf8("VirtualHeartBeatMonitor");
        using var instExt = new Utf8Array(instanceExt);
        var app = new VkApplicationInfo { sType = 0, pApplicationName = appName.Ptr, apiVersion = (1u << 22) | (1u << 12) };
        var appPtr = Pin(ref app, out var appHandle);
        try
        {
            var ici = new VkInstanceCreateInfo { sType = 1, pApplicationInfo = appPtr, enabledExtensionCount = (uint)instanceExt.Length, ppEnabledExtensionNames = instExt.Ptr };
            if (Vk.vkCreateInstance(ref ici, IntPtr.Zero, out _instance) is var r && r != 0) return $"vkCreateInstance: {r}";
        }
        finally { appHandle.Free(); }

        // The GPU SteamVR renders on; otherwise the first one.
        ulong output = 0;
        system.GetOutputDevice(ref output, ETextureType.Vulkan, _instance);
        _physical = (IntPtr)(long)output;
        if (_physical == IntPtr.Zero)
        {
            uint n = 1;
            var devs = new IntPtr[1];
            Vk.vkEnumeratePhysicalDevices(_instance, ref n, devs);
            _physical = devs[0];
        }
        if (_physical == IntPtr.Zero) return "no Vulkan device";

        uint qn = 0;
        Vk.vkGetPhysicalDeviceQueueFamilyProperties(_physical, ref qn, null);
        var families = new VkQueueFamilyProperties[qn];
        Vk.vkGetPhysicalDeviceQueueFamilyProperties(_physical, ref qn, families);
        int family = Array.FindIndex(families, f => (f.queueFlags & 1) != 0); // VK_QUEUE_GRAPHICS_BIT
        if (family < 0) return "no graphics queue";
        _family = (uint)family;

        sb.Clear();
        compositor.GetVulkanDeviceExtensionsRequired(_physical, sb, (uint)sb.Capacity);
        var deviceExt = Split(sb.ToString());
        using var devExt = new Utf8Array(deviceExt);
        var priority = new[] { 1f };
        var prioHandle = GCHandle.Alloc(priority, GCHandleType.Pinned);
        try
        {
            var qci = new VkDeviceQueueCreateInfo { sType = 2, queueFamilyIndex = _family, queueCount = 1, pQueuePriorities = prioHandle.AddrOfPinnedObject() };
            var qciPtr = Pin(ref qci, out var qciHandle);
            try
            {
                var dci = new VkDeviceCreateInfo { sType = 3, queueCreateInfoCount = 1, pQueueCreateInfos = qciPtr, enabledExtensionCount = (uint)deviceExt.Length, ppEnabledExtensionNames = devExt.Ptr };
                if (Vk.vkCreateDevice(_physical, ref dci, IntPtr.Zero, out _device) is var r && r != 0) return $"vkCreateDevice: {r}";
            }
            finally { qciHandle.Free(); }
        }
        finally { prioHandle.Free(); }
        Vk.vkGetDeviceQueue(_device, _family, 0, out _queue);

        var pci = new VkCommandPoolCreateInfo { sType = 39, flags = 2 /* RESET_COMMAND_BUFFER */, queueFamilyIndex = _family };
        if (Vk.vkCreateCommandPool(_device, ref pci, IntPtr.Zero, out _pool) is var pr && pr != 0) return $"vkCreateCommandPool: {pr}";

        _memoryProperties = new byte[1024];
        Vk.vkGetPhysicalDeviceMemoryProperties(_physical, _memoryProperties);
        return null;
    }

    /// <summary>A panel's pair of images.</summary>
    public Target CreateTarget(int width, int height) => new(this, width, height);

    private uint MemoryType(uint bits, uint wanted)
    {
        uint count = BitConverter.ToUInt32(_memoryProperties, 0);
        for (int i = 0; i < count; i++)
        {
            uint flags = BitConverter.ToUInt32(_memoryProperties, 4 + i * 8);
            if ((bits & (1u << i)) != 0 && (flags & wanted) == wanted) return (uint)i;
        }
        throw new InvalidOperationException($"no Vulkan memory type with flags {wanted}");
    }

    public sealed class Target : IDisposable
    {
        private readonly VulkanTextures _vk;
        private readonly Slot[] _slots = new Slot[2];
        private int _next;
        public int Width { get; }
        public int Height { get; }

        private sealed class Slot
        {
            public ulong Image, ImageMemory, Staging, StagingMemory, Fence;
            public IntPtr Mapped, Cmd;
            public bool Initialized;
        }

        internal Target(VulkanTextures vk, int width, int height)
        {
            _vk = vk;
            Width = width;
            Height = height;
            for (int i = 0; i < 2; i++) _slots[i] = CreateSlot();
        }

        private Slot CreateSlot()
        {
            var s = new Slot();
            var d = _vk._device;
            var ii = new VkImageCreateInfo
            {
                sType = 14, imageType = 1 /* 2D */, format = 37 /* R8G8B8A8_UNORM */, width = (uint)Width, height = (uint)Height, depth = 1,
                mipLevels = 1, arrayLayers = 1, samples = 1, tiling = 0 /* OPTIMAL */,
                usage = 0x1 | 0x2 | 0x4 /* TRANSFER_SRC | TRANSFER_DST | SAMPLED */, initialLayout = 0,
            };
            Check(Vk.vkCreateImage(d, ref ii, IntPtr.Zero, out s.Image), "vkCreateImage");
            Vk.vkGetImageMemoryRequirements(d, s.Image, out var mr);
            var ai = new VkMemoryAllocateInfo { sType = 5, allocationSize = mr.size, memoryTypeIndex = _vk.MemoryType(mr.memoryTypeBits, 0x1 /* DEVICE_LOCAL */) };
            Check(Vk.vkAllocateMemory(d, ref ai, IntPtr.Zero, out s.ImageMemory), "vkAllocateMemory(image)");
            Check(Vk.vkBindImageMemory(d, s.Image, s.ImageMemory, 0), "vkBindImageMemory");

            var bi = new VkBufferCreateInfo { sType = 12, size = (ulong)Width * (ulong)Height * 4, usage = 0x1 /* TRANSFER_SRC */ };
            Check(Vk.vkCreateBuffer(d, ref bi, IntPtr.Zero, out s.Staging), "vkCreateBuffer");
            Vk.vkGetBufferMemoryRequirements(d, s.Staging, out var br);
            var sai = new VkMemoryAllocateInfo { sType = 5, allocationSize = br.size, memoryTypeIndex = _vk.MemoryType(br.memoryTypeBits, 0x2 | 0x4 /* HOST_VISIBLE | HOST_COHERENT */) };
            Check(Vk.vkAllocateMemory(d, ref sai, IntPtr.Zero, out s.StagingMemory), "vkAllocateMemory(staging)");
            Check(Vk.vkBindBufferMemory(d, s.Staging, s.StagingMemory, 0), "vkBindBufferMemory");
            Check(Vk.vkMapMemory(d, s.StagingMemory, 0, bi.size, 0, out s.Mapped), "vkMapMemory");

            var cai = new VkCommandBufferAllocateInfo { sType = 40, commandPool = _vk._pool, level = 0, commandBufferCount = 1 };
            Check(Vk.vkAllocateCommandBuffers(d, ref cai, out s.Cmd), "vkAllocateCommandBuffers");
            var fci = new VkFenceCreateInfo { sType = 8, flags = 1 /* SIGNALED */ };
            Check(Vk.vkCreateFence(d, ref fci, IntPtr.Zero, out s.Fence), "vkCreateFence");
            return s;
        }

        /// <summary>Copies RGBA pixels into the next image and returns the texture to give to SetOverlayTexture.</summary>
        public void Upload(byte[] rgba, Func<Texture_t, EVROverlayError> setTexture)
        {
            var s = _slots[_next];
            _next ^= 1;
            var d = _vk._device;
            ulong fence = s.Fence;
            Check(Vk.vkWaitForFences(d, 1, ref fence, 1, ulong.MaxValue), "vkWaitForFences");
            Vk.vkResetFences(d, 1, ref fence);
            Marshal.Copy(rgba, 0, s.Mapped, Width * Height * 4);

            var begin = new VkCommandBufferBeginInfo { sType = 42, flags = 1 /* ONE_TIME_SUBMIT */ };
            Vk.vkBeginCommandBuffer(s.Cmd, ref begin);
            Barrier(s.Cmd, s.Image, s.Initialized ? 6u /* TRANSFER_SRC_OPTIMAL */ : 0u, 7 /* TRANSFER_DST_OPTIMAL */, 0x800 | 0x20 /* TRANSFER_READ | SHADER_READ */, 0x1000 /* TRANSFER_WRITE */);
            var region = new VkBufferImageCopy { aspectMask = 1, layerCount = 1, width = (uint)Width, height = (uint)Height, depth = 1 };
            Vk.vkCmdCopyBufferToImage(s.Cmd, s.Staging, s.Image, 7, 1, ref region);
            Barrier(s.Cmd, s.Image, 7, 6, 0x1000, 0x800);
            Vk.vkEndCommandBuffer(s.Cmd);

            var cmd = s.Cmd;
            var cmdHandle = GCHandle.Alloc(new[] { cmd }, GCHandleType.Pinned);
            try
            {
                var submit = new VkSubmitInfo { sType = 4, commandBufferCount = 1, pCommandBuffers = cmdHandle.AddrOfPinnedObject() };
                Check(Vk.vkQueueSubmit(_vk._queue, 1, ref submit, s.Fence), "vkQueueSubmit");
            }
            finally { cmdHandle.Free(); }
            Check(Vk.vkWaitForFences(d, 1, ref fence, 1, ulong.MaxValue), "vkWaitForFences");
            s.Initialized = true;

            var data = new VRVulkanTextureData_t
            {
                m_nImage = s.Image, m_pDevice = d, m_pPhysicalDevice = _vk._physical, m_pInstance = _vk._instance, m_pQueue = _vk._queue,
                m_nQueueFamilyIndex = _vk._family, m_nWidth = (uint)Width, m_nHeight = (uint)Height, m_nFormat = 37, m_nSampleCount = 1,
            };
            var dataPtr = Pin(ref data, out var dataHandle);
            try { setTexture(new Texture_t { handle = dataPtr, eType = ETextureType.Vulkan, eColorSpace = EColorSpace.Auto }); }
            finally { dataHandle.Free(); }
        }

        private static void Barrier(IntPtr cmd, ulong image, uint oldLayout, uint newLayout, uint srcAccess, uint dstAccess)
        {
            var b = new VkImageMemoryBarrier
            {
                sType = 45, srcAccessMask = srcAccess, dstAccessMask = dstAccess, oldLayout = oldLayout, newLayout = newLayout,
                srcQueueFamilyIndex = ~0u, dstQueueFamilyIndex = ~0u, image = image, aspectMask = 1, levelCount = 1, layerCount = 1,
            };
            Vk.vkCmdPipelineBarrier(cmd, 0x10000, 0x10000 /* ALL_COMMANDS */, 0, 0, IntPtr.Zero, 0, IntPtr.Zero, 1, ref b);
        }

        public void Dispose()
        {
            var d = _vk._device;
            if (d == IntPtr.Zero) return;
            Vk.vkDeviceWaitIdle(d);
            foreach (var s in _slots)
            {
                if (s == null) continue;
                Vk.vkDestroyFence(d, s.Fence, IntPtr.Zero);
                Vk.vkFreeCommandBuffers(d, _vk._pool, 1, ref s.Cmd);
                Vk.vkUnmapMemory(d, s.StagingMemory);
                Vk.vkDestroyBuffer(d, s.Staging, IntPtr.Zero);
                Vk.vkFreeMemory(d, s.StagingMemory, IntPtr.Zero);
                Vk.vkDestroyImage(d, s.Image, IntPtr.Zero);
                Vk.vkFreeMemory(d, s.ImageMemory, IntPtr.Zero);
            }
        }
    }

    public void Dispose()
    {
        if (_device != IntPtr.Zero)
        {
            Vk.vkDeviceWaitIdle(_device);
            if (_pool != 0) Vk.vkDestroyCommandPool(_device, _pool, IntPtr.Zero);
            Vk.vkDestroyDevice(_device, IntPtr.Zero);
            _device = IntPtr.Zero;
        }
        if (_instance != IntPtr.Zero) { Vk.vkDestroyInstance(_instance, IntPtr.Zero); _instance = IntPtr.Zero; }
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static string[] Split(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static void Check(int result, string what)
    {
        if (result != 0) throw new InvalidOperationException($"{what}: VkResult {result}");
    }

    private static IntPtr Pin<T>(ref T value, out GCHandle handle) where T : struct
    {
        var boxed = new T[] { value };
        handle = GCHandle.Alloc(boxed, GCHandleType.Pinned);
        return handle.AddrOfPinnedObject();
    }

    private sealed class Utf8 : IDisposable
    {
        public IntPtr Ptr { get; }
        public Utf8(string s) => Ptr = Marshal.StringToCoTaskMemUTF8(s);
        public void Dispose() => Marshal.FreeCoTaskMem(Ptr);
    }

    private sealed class Utf8Array : IDisposable
    {
        private readonly IntPtr[] _strings;
        private GCHandle _handle;
        public IntPtr Ptr => _strings.Length == 0 ? IntPtr.Zero : _handle.AddrOfPinnedObject();
        public Utf8Array(string[] items)
        {
            _strings = items.Select(Marshal.StringToCoTaskMemUTF8).ToArray();
            _handle = GCHandle.Alloc(_strings, GCHandleType.Pinned);
        }
        public void Dispose()
        {
            _handle.Free();
            foreach (var p in _strings) Marshal.FreeCoTaskMem(p);
        }
    }

    // ---- Vulkan C API (subset) ----------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)] private struct VkApplicationInfo { public int sType; public IntPtr pNext, pApplicationName; public uint applicationVersion; public IntPtr pEngineName; public uint engineVersion, apiVersion; }
    [StructLayout(LayoutKind.Sequential)] private struct VkInstanceCreateInfo { public int sType; public IntPtr pNext; public uint flags; public IntPtr pApplicationInfo; public uint enabledLayerCount; public IntPtr ppEnabledLayerNames; public uint enabledExtensionCount; public IntPtr ppEnabledExtensionNames; }
    [StructLayout(LayoutKind.Sequential)] private struct VkQueueFamilyProperties { public uint queueFlags, queueCount, timestampValidBits, gw, gh, gd; }
    [StructLayout(LayoutKind.Sequential)] private struct VkDeviceQueueCreateInfo { public int sType; public IntPtr pNext; public uint flags, queueFamilyIndex, queueCount; public IntPtr pQueuePriorities; }
    [StructLayout(LayoutKind.Sequential)] private struct VkDeviceCreateInfo { public int sType; public IntPtr pNext; public uint flags, queueCreateInfoCount; public IntPtr pQueueCreateInfos; public uint enabledLayerCount; public IntPtr ppEnabledLayerNames; public uint enabledExtensionCount; public IntPtr ppEnabledExtensionNames; public IntPtr pEnabledFeatures; }
    [StructLayout(LayoutKind.Sequential)] private struct VkCommandPoolCreateInfo { public int sType; public IntPtr pNext; public uint flags, queueFamilyIndex; }
    [StructLayout(LayoutKind.Sequential)] private struct VkImageCreateInfo { public int sType; public IntPtr pNext; public uint flags, imageType, format, width, height, depth, mipLevels, arrayLayers, samples, tiling, usage, sharingMode, queueFamilyIndexCount; public IntPtr pQueueFamilyIndices; public uint initialLayout; }
    [StructLayout(LayoutKind.Sequential)] private struct VkMemoryRequirements { public ulong size, alignment; public uint memoryTypeBits; }
    [StructLayout(LayoutKind.Sequential)] private struct VkMemoryAllocateInfo { public int sType; public IntPtr pNext; public ulong allocationSize; public uint memoryTypeIndex; }
    [StructLayout(LayoutKind.Sequential)] private struct VkBufferCreateInfo { public int sType; public IntPtr pNext; public uint flags; public ulong size; public uint usage, sharingMode, queueFamilyIndexCount; public IntPtr pQueueFamilyIndices; }
    [StructLayout(LayoutKind.Sequential)] private struct VkCommandBufferAllocateInfo { public int sType; public IntPtr pNext; public ulong commandPool; public uint level, commandBufferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct VkFenceCreateInfo { public int sType; public IntPtr pNext; public uint flags; }
    [StructLayout(LayoutKind.Sequential)] private struct VkCommandBufferBeginInfo { public int sType; public IntPtr pNext; public uint flags; public IntPtr pInheritanceInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct VkImageMemoryBarrier { public int sType; public IntPtr pNext; public uint srcAccessMask, dstAccessMask, oldLayout, newLayout, srcQueueFamilyIndex, dstQueueFamilyIndex; public ulong image; public uint aspectMask, baseMipLevel, levelCount, baseArrayLayer, layerCount; }
    [StructLayout(LayoutKind.Sequential)] private struct VkBufferImageCopy { public ulong bufferOffset; public uint bufferRowLength, bufferImageHeight, aspectMask, mipLevel, baseArrayLayer, layerCount; public int x, y, z; public uint width, height, depth; }
    [StructLayout(LayoutKind.Sequential)] private struct VkSubmitInfo { public int sType; public IntPtr pNext; public uint waitSemaphoreCount; public IntPtr pWaitSemaphores, pWaitDstStageMask; public uint commandBufferCount; public IntPtr pCommandBuffers; public uint signalSemaphoreCount; public IntPtr pSignalSemaphores; }

    private static class Vk
    {
        private const string Lib = "libvulkan.so.1";
        [DllImport(Lib)] public static extern int vkCreateInstance(ref VkInstanceCreateInfo info, IntPtr alloc, out IntPtr instance);
        [DllImport(Lib)] public static extern void vkDestroyInstance(IntPtr instance, IntPtr alloc);
        [DllImport(Lib)] public static extern int vkEnumeratePhysicalDevices(IntPtr instance, ref uint count, [Out] IntPtr[] devices);
        [DllImport(Lib)] public static extern void vkGetPhysicalDeviceQueueFamilyProperties(IntPtr pdev, ref uint count, [Out] VkQueueFamilyProperties[]? props);
        [DllImport(Lib)] public static extern void vkGetPhysicalDeviceMemoryProperties(IntPtr pdev, [Out] byte[] props);
        [DllImport(Lib)] public static extern int vkCreateDevice(IntPtr pdev, ref VkDeviceCreateInfo info, IntPtr alloc, out IntPtr device);
        [DllImport(Lib)] public static extern void vkDestroyDevice(IntPtr device, IntPtr alloc);
        [DllImport(Lib)] public static extern int vkDeviceWaitIdle(IntPtr device);
        [DllImport(Lib)] public static extern void vkGetDeviceQueue(IntPtr device, uint family, uint index, out IntPtr queue);
        [DllImport(Lib)] public static extern int vkCreateCommandPool(IntPtr device, ref VkCommandPoolCreateInfo info, IntPtr alloc, out ulong pool);
        [DllImport(Lib)] public static extern void vkDestroyCommandPool(IntPtr device, ulong pool, IntPtr alloc);
        [DllImport(Lib)] public static extern int vkCreateImage(IntPtr device, ref VkImageCreateInfo info, IntPtr alloc, out ulong image);
        [DllImport(Lib)] public static extern void vkDestroyImage(IntPtr device, ulong image, IntPtr alloc);
        [DllImport(Lib)] public static extern void vkGetImageMemoryRequirements(IntPtr device, ulong image, out VkMemoryRequirements req);
        [DllImport(Lib)] public static extern int vkAllocateMemory(IntPtr device, ref VkMemoryAllocateInfo info, IntPtr alloc, out ulong memory);
        [DllImport(Lib)] public static extern void vkFreeMemory(IntPtr device, ulong memory, IntPtr alloc);
        [DllImport(Lib)] public static extern int vkBindImageMemory(IntPtr device, ulong image, ulong memory, ulong offset);
        [DllImport(Lib)] public static extern int vkCreateBuffer(IntPtr device, ref VkBufferCreateInfo info, IntPtr alloc, out ulong buffer);
        [DllImport(Lib)] public static extern void vkDestroyBuffer(IntPtr device, ulong buffer, IntPtr alloc);
        [DllImport(Lib)] public static extern void vkGetBufferMemoryRequirements(IntPtr device, ulong buffer, out VkMemoryRequirements req);
        [DllImport(Lib)] public static extern int vkBindBufferMemory(IntPtr device, ulong buffer, ulong memory, ulong offset);
        [DllImport(Lib)] public static extern int vkMapMemory(IntPtr device, ulong memory, ulong offset, ulong size, uint flags, out IntPtr data);
        [DllImport(Lib)] public static extern void vkUnmapMemory(IntPtr device, ulong memory);
        [DllImport(Lib)] public static extern int vkAllocateCommandBuffers(IntPtr device, ref VkCommandBufferAllocateInfo info, out IntPtr cmd);
        [DllImport(Lib)] public static extern void vkFreeCommandBuffers(IntPtr device, ulong pool, uint count, ref IntPtr cmd);
        [DllImport(Lib)] public static extern int vkCreateFence(IntPtr device, ref VkFenceCreateInfo info, IntPtr alloc, out ulong fence);
        [DllImport(Lib)] public static extern void vkDestroyFence(IntPtr device, ulong fence, IntPtr alloc);
        [DllImport(Lib)] public static extern int vkWaitForFences(IntPtr device, uint count, ref ulong fence, uint waitAll, ulong timeout);
        [DllImport(Lib)] public static extern int vkResetFences(IntPtr device, uint count, ref ulong fence);
        [DllImport(Lib)] public static extern int vkBeginCommandBuffer(IntPtr cmd, ref VkCommandBufferBeginInfo info);
        [DllImport(Lib)] public static extern int vkEndCommandBuffer(IntPtr cmd);
        [DllImport(Lib)] public static extern void vkCmdPipelineBarrier(IntPtr cmd, uint srcStage, uint dstStage, uint deps, uint memCount, IntPtr mem, uint bufCount, IntPtr buf, uint imgCount, ref VkImageMemoryBarrier img);
        [DllImport(Lib)] public static extern void vkCmdCopyBufferToImage(IntPtr cmd, ulong buffer, ulong image, uint layout, uint count, ref VkBufferImageCopy region);
        [DllImport(Lib)] public static extern int vkQueueSubmit(IntPtr queue, uint count, ref VkSubmitInfo submit, ulong fence);
    }
}
