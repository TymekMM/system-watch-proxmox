using System.Text.Json.Nodes;
using SystemWatch.Collector;

const string example = "tank\t1000000\t200000\t800000\t20\t12\tONLINE\n";
const string status = """
{
  "output_version": {"vers_major": 0, "vers_minor": 1},
  "pools": {
    "tank": {
      "name": "tank", "state": "ONLINE", "pool_guid": "18446744073709551615",
      "error_count": "0",
      "scan_stats": {
        "function": "SCRUB", "state": "FINISHED", "start_time": "1789251841",
        "end_time": "1789251992", "to_examine": "100", "examined": "101",
        "issued": "100", "errors": "0"
      },
      "vdevs": {
        "tank": {"name": "tank", "vdev_type": "root", "guid": "1001",
          "class": "normal", "state": "ONLINE", "vdevs": {
            "mirror-0": {"name": "mirror-0", "vdev_type": "mirror", "guid": "1002",
              "class": "normal", "state": "ONLINE", "vdevs": {
                "/dev/sda1": {"name": "/dev/sda1", "path": "/dev/sda1",
                  "vdev_type": "disk", "guid": "1003", "class": "normal",
                  "state": "ONLINE", "read_errors": "0", "write_errors": "0",
                  "checksum_errors": "0"}
              }}
          }}
      }
    }
  }
}
""";

void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    Console.WriteLine($"PASS {description}");
}

JsonObject Copy() => JsonNode.Parse(status)!.AsObject();

void Reject(JsonObject document, string list, string description)
{
    try { ZpoolStatusParser.Parse(document.ToJsonString(), ZpoolListParser.Parse(list)); }
    catch (InvalidDataException)
    {
        Console.WriteLine($"PASS {description}");
        return;
    }
    throw new Exception(description);
}

try
{
    var listed = ZpoolListParser.Parse(example);
    var pool = ZpoolStatusParser.Parse(status, listed).Single();
    Check(pool.Id == "zpool:18446744073709551615" && pool.Health == "ok" &&
          pool.ListEntry.SizeBytes == "1000000" && pool.PermanentErrors.State == "none",
          "reference fixture pool GUID, list data and health");
    Check(pool.Vdevs.Count == 3 && pool.Vdevs[2].ParentId == "vdev:1002" &&
          pool.Vdevs[2].Path == "/dev/sda1" && pool.Vdevs[2].ChecksumErrors == "0" &&
          pool.Vdevs[1].AllocationClass == "data",
          "root/mirror/disk hierarchy and unresolved device path");
    Check(pool.Scan.Type == "scrub" && pool.Scan.State == "completed" &&
          pool.Scan.StartedAt == "2026-09-12T22:24:01Z" &&
          pool.Scan.ScannedBytes == "101" && pool.Scan.ProgressPercent is null,
          "finished scan timestamps and counters");

    var faulty = Copy();
    var raw = faulty["pools"]!["tank"]!;
    raw["state"] = "FAULTED";
    raw["error_count"] = "3";
    raw["vdevs"]!["tank"]!["vdevs"]!["mirror-0"]!["vdevs"]!["/dev/sda1"]!["checksum_errors"] = "2";
    var fault = ZpoolStatusParser.Parse(faulty.ToJsonString(),
        ZpoolListParser.Parse(example.Replace("ONLINE", "FAULTED"))).Single();
    Check(fault.Health == "critical" && fault.PermanentErrors.State == "present" &&
          fault.PermanentErrors.Count is null && fault.Vdevs[2].ChecksumErrors == "2",
          "faulted pool and permanent errors are not green");

    var scanning = Copy();
    scanning["pools"]!["tank"]!["scan_stats"]!["state"] = "SCANNING";
    Check(ZpoolStatusParser.Parse(scanning.ToJsonString(), listed).Single().Scan.FinishedAt is null,
          "running scan cannot retain end timestamp");

    var badVersion = Copy();
    badVersion["output_version"]!["vers_major"] = 1;
    Reject(badVersion, example, "unsupported status JSON version rejected");
    var missingPool = Copy();
    missingPool["pools"]!.AsObject().Clear();
    Reject(missingPool, example, "list/status pool inventory mismatch rejected");
    var stateMismatch = Copy();
    stateMismatch["pools"]!["tank"]!["state"] = "FAULTED";
    Reject(stateMismatch, example, "list/status state mismatch rejected");
    var duplicateGuid = Copy();
    duplicateGuid["pools"]!["tank"]!["vdevs"]!["tank"]!["vdevs"]!["mirror-0"]!["guid"] = "1001";
    Reject(duplicateGuid, example, "duplicate vdev GUID rejected");
    var badCounter = Copy();
    badCounter["pools"]!["tank"]!["error_count"] = "01";
    Reject(badCounter, example, "noncanonical error counter rejected");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
