using System.Globalization;
using SystemWatch.Collector;

void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    Console.WriteLine($"PASS {description}");
}

try
{
    var structured = SmartTemperatureParser.Parse("""
      {"smartctl":{"exit_status":0},"temperature":{"current":33},
       "ata_smart_attributes":{"table":[
         {"id":194,"raw":{"value":98784247841,"string":"33 (0 23 0 0 0)"}}]}}
      """);
    Check(structured == new SmartTemperature(33, null, false),
          "structured temperature wins over packed ATA value");

    var standby = SmartTemperatureParser.Parse("""
      {"smartctl":{"exit_status":3},"power_mode":{"name":"STANDBY"}}
      """);
    Check(standby == new SmartTemperature(null, "standby", true),
          "guarded standby read is skipped, not failed");
    Check(SmartTemperatureParser.Parse("""
      {"smartctl":{"exit_status":3},"power_mode":{"name":"sleep"}}
      """).Skipped, "sleep mode is also skipped");

    Check(SmartTemperatureParser.Parse("""
      {"smartctl":{"exit_status":8},"temperature":{"current":38}}
      """) == new SmartTemperature(null, "smartctl_error", false),
          "nonzero smartctl exit cannot produce healthy reading");
    Check(SmartTemperatureParser.Parse("""
      {"smartctl":{"exit_status":0},"ata_smart_attributes":{"table":[
        {"id":5,"raw":{"string":"200"}},
        {"id":194,"raw":{"value":98784247841,"string":"37.5 (0 23 0)"}}]}}
      """) == new SmartTemperature(37.5, null, false),
          "ATA 194 numeric prefix read without packed value");
    Check(SmartTemperatureParser.Parse("""
      {"smartctl":{"exit_status":0},"ata_smart_attributes":{"table":[
        {"id":190,"raw":{"string":"-2"}}]}}
      """) == new SmartTemperature(-2, null, false),
          "ATA 190 fallback parsed with invariant decimal syntax");

    var noTemperature = SmartTemperatureParser.Parse("""
      {"smartctl":{"exit_status":0},"ata_smart_attributes":{"table":[
        {"id":194,"raw":{"value":98784247841}}]}}
      """);
    Check(noTemperature == new SmartTemperature(null, "temperature_unavailable", false),
          "packed ATA raw.value alone stays unavailable");
    Check(SmartTemperatureParser.Parse("{}") ==
          new SmartTemperature(null, "smartctl_error", false),
          "missing exit status does not read as success");

    var original = CultureInfo.CurrentCulture;
    try
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("nl-NL");
        Check(SmartTemperatureParser.Parse("""
          {"smartctl":{"exit_status":0},"ata_smart_attributes":{"table":[
            {"id":194,"raw":{"string":"42.25 (0 23 0)"}}]}}
          """).ValueCelsius == 42.25, "ATA decimal parsing ignores process culture");
    }
    finally { CultureInfo.CurrentCulture = original; }

    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
