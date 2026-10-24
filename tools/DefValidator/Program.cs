using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RentalWarehouse.Tools;

// Usage: DefValidator --assembly <dll> [--assembly <dll>...] --defs <dir> [--reference <dir>...]
//   --assembly   game and mod assemblies (real game DLLs or the Krafs.Rimworld.Ref reference assemblies)
//   --defs       the mod's Defs folder to check
//   --reference  folders of other defs (e.g. the game's Data/Core/Defs) for ParentName and def-reference checks
var asms = new List<string>();
var defs = new List<string>();
var refs = new List<string>();
for (int i = 0; i + 1 < args.Length; i += 2)
{
    switch (args[i])
    {
        case "--assembly": asms.Add(args[i + 1]); break;
        case "--defs": defs.Add(args[i + 1]); break;
        case "--reference": refs.Add(args[i + 1]); break;
        default: Console.Error.WriteLine("unknown option " + args[i]); return 2;
    }
}
if (asms.Count == 0 || defs.Count == 0)
{
    Console.Error.WriteLine("usage: DefValidator --assembly <dll>... --defs <dir> [--reference <dir>...]");
    return 2;
}
static IEnumerable<string> Xml(IEnumerable<string> dirs) => dirs.SelectMany(d => Directory.GetFiles(d, "*.xml", SearchOption.AllDirectories)).OrderBy(f => f, StringComparer.Ordinal);
using var validator = new DefValidator(asms);
var issues = validator.Validate(Xml(defs), refs.Count == 0 ? null : Xml(refs).ToList());
foreach (var issue in issues) Console.WriteLine("ERROR: " + issue);
Console.WriteLine(issues.Count == 0
    ? "PASS" + (refs.Count == 0 ? " (def references not checked: no --reference)" : "")
    : $"FAIL ({issues.Count} problem(s))");
return issues.Count == 0 ? 0 : 1;
