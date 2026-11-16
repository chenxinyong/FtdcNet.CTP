using System.Reflection;
using System.Runtime.InteropServices;
using CTP;

var asm = typeof(ThostFtdcDisseminationField).Assembly;
var types = asm.GetTypes()
    .Where(t => t.Namespace == "CTP" && t.Name.StartsWith("ThostFtdc", StringComparison.Ordinal))
    .OrderBy(t => t.Name, StringComparer.Ordinal);

foreach (var type in types)
{
    Console.WriteLine($"S|{type.Name}|{Marshal.SizeOf(type)}");
    foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
    {
        Console.WriteLine($"F|{type.Name}|{field.Name}|{Marshal.OffsetOf(type, field.Name)}");
    }
}
