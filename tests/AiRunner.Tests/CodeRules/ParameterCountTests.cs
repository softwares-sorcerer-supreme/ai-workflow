using System.Reflection;
using System.Runtime.CompilerServices;

namespace AiRunner.Tests.CodeRules;

/// <summary>
/// The rule from CLAUDE.md: a method takes at most 3 parameters, not counting a CancellationToken or the
/// receiver of an extension method. More data than that goes into a DTO (a record). Constructors are exempt
/// (they receive injected services, and a DTO would hide the dependencies), and so are record members.
/// </summary>
public class ParameterCountTests
{
    private const int MaxParameters = 3;

    private static readonly BindingFlags AllDeclaredMethods =
        BindingFlags.Public |
        BindingFlags.NonPublic |
        BindingFlags.Instance |
        BindingFlags.Static |
        BindingFlags.DeclaredOnly;

    [Fact]
    public void No_method_takes_more_than_three_parameters_besides_the_cancellation_token()
    {
        var assembly = typeof(JobPlanner).Assembly;
        var offenders = new List<string>();

        foreach (var type in assembly.GetTypes())
        {
            if (type.IsDefined(typeof(CompilerGeneratedAttribute)))
            {
                continue;
            }

            foreach (var method in type.GetMethods(AllDeclaredMethods))
            {
                if (IsExempt(method))
                {
                    continue;
                }

                var count = CountParameters(method);

                if (count > MaxParameters)
                {
                    offenders.Add($"{type.Name}.{method.Name} takes {count} parameters");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Use a DTO (a record) for methods with more than 3 parameters:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void The_check_counts_parameters_without_the_token_and_the_extension_receiver()
    {
        var tooMany = typeof(LongSignature).GetMethod(nameof(LongSignature.TooMany))!;
        var extension = typeof(LongSignature).GetMethod(nameof(LongSignature.Extension))!;

        Assert.Equal(4, CountParameters(tooMany));
        Assert.Equal(3, CountParameters(extension));
    }

    /// <summary>Compiler output (lambdas, local functions, record plumbing) is not code anyone wrote.</summary>
    private static bool IsExempt(MethodInfo method) =>
        method.IsSpecialName ||
        method.Name.Contains('<') ||
        method.IsDefined(typeof(CompilerGeneratedAttribute)) ||
        IsLibraryInterfaceMember(method) ||
        method.Name is "Deconstruct" or "PrintMembers" or "Equals" or "GetHashCode" or "ToString";

    /// <summary>YamlDotNet fixes the signature of <c>IYamlTypeConverter.WriteYaml</c> (4 parameters).</summary>
    private static bool IsLibraryInterfaceMember(MethodInfo method) =>
        method.DeclaringType!.GetInterfaces().Contains(typeof(YamlDotNet.Serialization.IYamlTypeConverter));

    private static int CountParameters(MethodInfo method)
    {
        var count = method.GetParameters().Count(parameter => parameter.ParameterType != typeof(CancellationToken));
        var isExtension = method.IsDefined(typeof(ExtensionAttribute));

        return isExtension ? count - 1 : count;
    }
}

/// <summary>Known-long signatures, to prove the check can see them. Not part of the AiRunner assembly.</summary>
public static class LongSignature
{
    public static void TooMany(int first, int second, int third, int fourth, CancellationToken cancellationToken)
    {
    }

    public static void Extension(this string receiver, int first, int second, int third)
    {
    }
}
