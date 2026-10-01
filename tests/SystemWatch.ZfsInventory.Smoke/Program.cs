using System.Text.Json.Nodes;
using SystemWatch.Collector;

const string at = "2026-09-23T14:00:00Z";
const string list = "tank\t1000000\t200000\t800000\t20\t12\tONLINE\n";
const string status = """
{
  "output_version": {"vers_major": 0},
  "pools": {"tank": {
    "name": "tank", "state": "ONLINE", "pool_guid": "18446744073709551615",
    "error_count": "0", "scan_stats": {"function": "NONE", "state": "NONE"},
    "vdevs": {"tank": {"name": "tank", "vdev_type": "root", "guid": "1001",
      "class": "normal", "state": "ONLINE", "vdevs": {
        "mirror-0": {"name": "mirror-0", "vdev_type": "mirror", "guid": "1002",
          "class": "normal", "state": "ONLINE", "vdevs": {
            "leaf": {"name": "leaf", "path": "/dev/disk/by-id/drive-part1",
              "vdev_type": "disk", "guid": "1003", "class": "normal",
              "state": "ONLINE", "read_errors": "0", "write_errors": "0",
              "checksum_errors": "0"}
          }}
      }}
    }
  }}
}
""";
const string lsblk = """
{"blockdevices": [
  {"name": "sda", "path": "/dev/sda", "type": "disk", "wwn": "0xAAA",
   "serial": "ONE", "model": "Test HDD", "tran": "sas", "rota": true,
   "children": [{"name": "sda1", "path": "/dev/sda1", "type": "part",
                 "mountpoints": [null]}]},
  {"name": "nvme0n1", "path": "/dev/nvme0n1", "type": "disk",
   "wwn": "eui.BBB", "serial": "TWO", "model": "Test SSD",
   "tran": "nvme", "rota": false,
   "children": [{"name": "nvme0n1p1", "path": "/dev/nvme0n1p1",
                 "type": "part", "mountpoints": ["/boot/efi"]}]},
  {"name": "zd0", "path": "/dev/zd0", "type": "disk", "rota": false}
]}
""";

void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    Console.WriteLine($"PASS {description}");
}

void Reject(Action action, string description)
{
    try { action(); }
    catch (InvalidDataException)
    {
        Console.WriteLine($"PASS {description}");
        return;
    }
    throw new Exception(description);
}

try
{
    var inventory = LsblkInventoryParser.Parse(lsblk);
    Check(inventory.Devices.Count == 2 &&
          inventory.Devices[0].Id == "disk:wwn:0xaaa" &&
          inventory.Devices[1].Id == "disk:wwn:eui.bbb" &&
          inventory.Devices[1].Usage == "system" &&
          !inventory.DeviceIdByPath.ContainsKey("/dev/zd0"),
          "physical disks, partitions and boot mount; zvol omitted");

    var pools = ZpoolStatusParser.Parse(status, ZpoolListParser.Parse(list));
    string Resolve(string path) => path == "/dev/disk/by-id/drive-part1" ? "/dev/sda1" : path;
    var result = ZfsInventoryAssembler.Assemble(pools, inventory, Resolve, at);
    var disk = result.Devices[0];
    var pool = result.Zfs.Pools.Single();
    Check(result.Zfs.InventoryState == "ok" && result.Zfs.PoolCount == 1 &&
          disk.ZfsMembership == "member" && disk.PoolIds.Single() == pool.Id &&
          disk.Aliases.Single() == "/dev/disk/by-id/drive-part1" &&
          result.Devices[1].ZfsMembership == "nonmember",
          "alias resolved to physical disk; reciprocal pool membership");
    Check(pool.Id == "zpool:18446744073709551615" &&
          pool.Vdevs[2].DeviceId == disk.Id &&
          pool.Vdevs[2].ParentId == "vdev:1002" &&
          pool.Reading.ObservedAt == at && pool.AllocatedBytes == "200000",
          "typed pool and vdev match reference fixture");
    Check(inventory.Devices[0].ZfsMembership == "nonmember" &&
          inventory.Devices[0].Aliases.Count == 0,
          "assembly preserves input inventory");

    Reject(() => ZfsInventoryAssembler.Assemble(pools, inventory, path => path, at),
           "unmapped ONLINE leaf rejected");
    var missing = JsonNode.Parse(status)!.AsObject();
    var leaf = missing["pools"]!["tank"]!["vdevs"]!["tank"]!["vdevs"]!["mirror-0"]!["vdevs"]!["leaf"]!;
    leaf["state"] = "FAULTED";
    var faulted = ZpoolStatusParser.Parse(missing.ToJsonString(), ZpoolListParser.Parse(list));
    Check(ZfsInventoryAssembler.Assemble(faulted, inventory, path => path, at)
              .Zfs.Pools[0].Vdevs[2].DeviceId is null,
          "missing FAULTED leaf remains in topology without device ID");

    var collision = JsonNode.Parse(lsblk)!.AsObject();
    collision["blockdevices"]![1]!["wwn"] = "0xAAA";
    Reject(() => LsblkInventoryParser.Parse(collision.ToJsonString()),
           "colliding persistent disk identities rejected");
    var fallback = LsblkInventoryParser.Parse("""
        {"blockdevices": [
          {"path":"/dev/sdb","type":"disk","tran":"sata",
           "model":" Test SATA ","serial":"SN01"},
          {"path":"/dev/sdc","type":"disk","tran":"usb"}
        ]}
        """);
    Check(fallback.Devices[0].Id == "disk:serial:Test SATA:SN01" &&
          fallback.Devices[0].Transport == "ata" &&
          fallback.Devices[0].IdentityStability == "persistent" &&
          fallback.Devices[1].Id == "disk:session:/dev/sdc" &&
          fallback.Devices[1].IdentityStability == "session" &&
          fallback.Devices[1].Media == "unknown",
          "serial and session identity fallbacks");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
