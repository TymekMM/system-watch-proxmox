namespace SystemWatch.Management;

public sealed record VmRepairPlan(VmRecipeInstaller.Manifest Current,
    VmRecipeInstaller.Manifest Updated, IReadOnlyList<VmPatchRecipe.Candidate> Patches,
    byte[][] BeforeVendor, byte[][] PreviousBackups, byte[][] NewOriginals);
