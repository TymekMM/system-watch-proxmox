namespace SystemWatch.Management;

public sealed record VmWorkerUpdate(VmRecipeInstaller.Manifest Current,
    VmRecipeInstaller.Manifest Updated, byte[] Previous, byte[] Candidate);
