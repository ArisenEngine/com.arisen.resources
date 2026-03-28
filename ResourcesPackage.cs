using ArisenKernel.Packages;
using ArisenKernel.Services;
using ArisenKernel.Diagnostics;

namespace ArisenEngine.Resources;

public class ResourcesPackage : IPackageEntry
{
    public void OnLoad(IServiceRegistry registry)
    {
        KernelLog.Info("[ResourcesPackage] Loaded: Arisen Asset Resources");
    }

    public void OnUnload(IServiceRegistry registry)
    {
    }
}
