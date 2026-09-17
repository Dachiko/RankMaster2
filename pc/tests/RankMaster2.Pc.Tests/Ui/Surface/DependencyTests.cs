using System.Reflection;
using RankMaster2.Pc.Ui.Surface;
using Xunit;
using Xunit.Abstractions;

namespace RankMaster2.Pc.Ui.Tests.Surface;

/// <summary>
/// Plan § 2.1: "everything that decides has no Avalonia type in it", and § 6.3 point 6 / § E6:
/// "grep in E6" for <c>Avalonia</c> / <c>RankMaster2.Ranking</c> / <c>RankMaster2.Catalog</c> /
/// <c>RankMaster2.Actions</c> inside <c>Surface/</c>. <c>Surface/</c> and <c>Views/</c> share one
/// assembly here (plan § E0: no <c>RankMaster2.Pc.csproj</c> existed to split them across, so this
/// is a temporary scaffold like parts C and D built), so a whole-assembly reference check
/// (the pattern <c>RankMaster2.Pc.Stills.Tests.DependencyTests</c> uses) would fail on Views/'s
/// account and prove nothing about Surface/. This instead reflects over every type actually
/// declared in the <c>RankMaster2.Pc.Ui.Surface</c> namespace and checks every field, property,
/// and method signature for an Avalonia-namespaced type -- the thing a text grep can be fooled by
/// (a type alias, a fully-qualified name split across lines) and reflection cannot.
/// </summary>
public class DependencyTests(ITestOutputHelper output)
{
    [Fact]
    public void No_type_in_Surface_mentions_an_Avalonia_type_in_its_public_or_private_signatures()
    {
        var assembly = typeof(RankCoordinator).Assembly;
        var surfaceTypes = assembly.GetTypes().Where(t => t.Namespace == "RankMaster2.Pc.Ui.Surface").ToArray();
        Assert.True(surfaceTypes.Length > 5, "Sanity check: expected several Surface/ types to be found via reflection.");

        var offenders = new List<string>();
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var type in surfaceTypes)
        {
            foreach (var field in type.GetFields(all))
                CheckAvalonia(type, field.Name, field.FieldType, offenders);

            foreach (var prop in type.GetProperties(all))
                CheckAvalonia(type, prop.Name, prop.PropertyType, offenders);

            foreach (var method in type.GetMethods(all))
            {
                CheckAvalonia(type, method.Name + " return", method.ReturnType, offenders);
                foreach (var param in method.GetParameters())
                    CheckAvalonia(type, method.Name + "(" + param.Name + ")", param.ParameterType, offenders);
            }
        }

        output.WriteLine($"Checked {surfaceTypes.Length} types in RankMaster2.Pc.Ui.Surface.");
        Assert.True(offenders.Count == 0, "Avalonia type(s) found in Surface/ signatures:\n" + string.Join("\n", offenders));
    }

    private static void CheckAvalonia(Type owner, string memberName, Type memberType, List<string> offenders)
    {
        var t = memberType.IsGenericType ? memberType.GetGenericTypeDefinition() : memberType;
        if ((t.Namespace ?? "").StartsWith("Avalonia", StringComparison.Ordinal))
            offenders.Add($"{owner.FullName}.{memberName}: {memberType}");

        // One level into generics (e.g. IReadOnlyList<T>, Task<T>) since that is where a leaked
        // Bitmap/PixelSize would actually show up.
        if (memberType.IsGenericType)
            foreach (var arg in memberType.GetGenericArguments())
                if ((arg.Namespace ?? "").StartsWith("Avalonia", StringComparison.Ordinal))
                    offenders.Add($"{owner.FullName}.{memberName}: generic argument {arg}");
    }
}
