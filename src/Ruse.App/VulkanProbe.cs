using System.Runtime.InteropServices;

namespace Ruse.App;

/// <summary>Reads Vulkan device information without starting an inference backend.</summary>
internal static class VulkanProbe
{
    public static (bool Available, string? DeviceName, string? Error) Query()
    {
        try
        {
            var createInfo = new VkInstanceCreateInfo { sType = 1 };
            int result = vkCreateInstance(ref createInfo, IntPtr.Zero, out IntPtr instance);
            if (result != 0)
            {
                return (false, null, $"Vulkan instance creation failed (VkResult {result}).");
            }

            try
            {
                uint count = 0;
                result = vkEnumeratePhysicalDevices(instance, ref count, null);
                if (result != 0)
                {
                    return (false, null, $"Vulkan device enumeration failed (VkResult {result}).");
                }

                if (count == 0)
                {
                    return (false, null, "No Vulkan physical device found.");
                }

                var devices = new IntPtr[count];
                result = vkEnumeratePhysicalDevices(instance, ref count, devices);
                if (result != 0 || count == 0)
                {
                    return (false, null, $"Vulkan device enumeration failed (VkResult {result}).");
                }

                var names = new List<string>();
                for (int i = 0; i < count && i < devices.Length; i++)
                {
                    IntPtr properties = Marshal.AllocHGlobal(1024);
                    try
                    {
                        vkGetPhysicalDeviceProperties(devices[i], properties);
                        string? name = Marshal.PtrToStringUTF8(IntPtr.Add(properties, 20));
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            names.Add(name);
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(properties);
                    }
                }

                if (names.Count == 0)
                {
                    return (false, null, "The Vulkan device did not report a name.");
                }

                return (true, string.Join(" / ", names.Distinct()), null);
            }
            finally
            {
                vkDestroyInstance(instance, IntPtr.Zero);
            }
        }
        catch (DllNotFoundException)
        {
            return (false, null, "The Vulkan runtime (vulkan-1.dll) is not installed.");
        }
        catch (EntryPointNotFoundException)
        {
            return (false, null, "The Vulkan runtime is missing required entry points.");
        }
        catch (Exception ex)
        {
            return (false, null, ex.GetBaseException().Message);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkInstanceCreateInfo
    {
        public uint sType;
        public IntPtr pNext;
        public uint flags;
        public IntPtr pApplicationInfo;
        public uint enabledLayerCount;
        public IntPtr ppEnabledLayerNames;
        public uint enabledExtensionCount;
        public IntPtr ppEnabledExtensionNames;
    }

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int vkCreateInstance(ref VkInstanceCreateInfo createInfo, IntPtr allocator, out IntPtr instance);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int vkEnumeratePhysicalDevices(IntPtr instance, ref uint count, IntPtr[]? devices);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void vkGetPhysicalDeviceProperties(IntPtr device, IntPtr properties);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void vkDestroyInstance(IntPtr instance, IntPtr allocator);
}
