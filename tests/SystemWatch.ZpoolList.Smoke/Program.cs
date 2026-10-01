using System.Globalization;
using SystemWatch.Collector;

void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    Console.WriteLine($"PASS {description}");
}

void Reject(string output, string description)
{
    try { ZpoolListParser.Parse(output); }
    catch (InvalidDataException)
    {
        Console.WriteLine($"PASS {description}");
        return;
    }
    throw new Exception(description);
}

try
{
    const string example = "tank\t1000000\t200000\t800000\t20\t12\tONLINE\n";
    var entry = ZpoolListParser.Parse(example)["tank"];
    Check(entry.SizeBytes == "1000000" && entry.AllocatedBytes == "200000" &&
          entry.FreeBytes == "800000" && entry.CapacityPercent == 20 &&
          entry.FragmentationPercent == 12 && entry.State == "ONLINE",
          "reference fixture row maps to canonical values");

    const string multiple = "Data\t18446744073709551615\t0\t18446744073709551615\t0%\t-\tONLINE\r\n" +
                            "Data-SSD\t9000\t4000\t5000\t44.5\t12.25%\tDEGRADED\n";
    var pools = ZpoolListParser.Parse(multiple);
    Check(pools.Count == 2 && pools["Data"].SizeBytes == "18446744073709551615" &&
          pools["Data"].FragmentationPercent is null &&
          pools["Data-SSD"].CapacityPercent == 44.5 &&
          pools["Data-SSD"].State == "DEGRADED",
          "two pools, unsigned 64-bit text, CRLF and optional fields");

    var previousCulture = CultureInfo.CurrentCulture;
    try
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        Check(ZpoolListParser.Parse("a\t0\t0\t0\t12.5\t0\tONLINE")["a"].CapacityPercent == 12.5,
              "percentage parsing ignores process culture");
    }
    finally { CultureInfo.CurrentCulture = previousCulture; }

    Check(ZpoolListParser.Parse("\n").Count == 0, "empty output has no pools");
    Reject("a\t1\t2\t3\t4\t5", "incorrect column count rejected");
    Reject(example + example, "duplicate pool rejected");
    Reject("a\t01\t0\t0\t0\t0\tONLINE", "noncanonical byte counter rejected");
    Reject("a\t-1\t0\t0\t0\t0\tONLINE", "negative byte counter rejected");
    Reject("a\t0\t0\t0\tNaN\t0\tONLINE", "nonfinite percentage rejected");
    Reject("a\t0\t0\t0\t101\t0\tONLINE", "out-of-range percentage rejected");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
