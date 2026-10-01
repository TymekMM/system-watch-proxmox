namespace SystemWatch.Management;

public sealed record VmInstallAssetProfile(string WorkerRevision, string WorkerHash,
    string UiAssetHash, string UnitTemplateHash);
