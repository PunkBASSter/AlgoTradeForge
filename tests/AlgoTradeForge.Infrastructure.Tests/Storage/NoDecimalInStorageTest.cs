using System.Reflection;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using Xunit;

namespace AlgoTradeForge.Infrastructure.Tests.Storage;

/// <summary>
/// Cross-cutting X-1: storage-layer types (writers + readers + sink writers) MUST NOT carry
/// <see cref="decimal"/> fields or properties. Per TRD §3.4 / §3.6, storage is
/// <c>long</c> (primary bars) + <c>double</c> (side feeds + analytical sidecars);
/// <see cref="decimal"/> only lives in Domain types and the conversion helpers
/// (<see cref="AlgoTradeForge.Domain.MoneyConvert"/>, <see cref="AlgoTradeForge.Domain.ScaleContext"/>).
/// </summary>
/// <remarks>
/// The test walks the assemblies that own storage I/O and locates all concrete types
/// implementing one of the storage interfaces. For each, it enumerates instance fields
/// (public + non-public) and instance properties and asserts no <see cref="decimal"/> typing.
/// Local variables and method parameters are out of scope — those routinely accept decimals
/// from the Application boundary and convert via <see cref="AlgoTradeForge.Domain.MoneyConvert"/>.
/// </remarks>
public sealed class NoDecimalInStorageTest
{
    private static readonly Type[] StorageInterfaces =
    [
        typeof(IInt64BarLoader),
        typeof(IFeedSeriesLoader),
        typeof(ICandleWriter),
        typeof(IFeedWriter),
    ];

    private static readonly Type[] StorageStandaloneTypes =
    [
        typeof(PartitionedSinkWriter),
    ];

    private static readonly Assembly[] AssembliesToScan =
    [
        typeof(AlgoTradeForge.Infrastructure.History.PartitionedCsvBarLoader).Assembly,
        typeof(AlgoTradeForge.HistoryLoader.Infrastructure.DependencyInjection).Assembly,
        typeof(PartitionedSinkWriter).Assembly,
    ];

    [Fact]
    public void StorageImplementations_HaveNoDecimalFieldsOrProperties()
    {
        var storageTypes = new HashSet<Type>(StorageStandaloneTypes);

        foreach (var asm in AssembliesToScan.Distinct())
        {
            foreach (var type in asm.GetTypes())
            {
                if (type.IsAbstract || type.IsInterface) continue;

                foreach (var iface in StorageInterfaces)
                {
                    if (iface.IsAssignableFrom(type))
                    {
                        storageTypes.Add(type);
                        break;
                    }
                }
            }
        }

        Assert.NotEmpty(storageTypes);

        var violations = new List<string>();
        foreach (var type in storageTypes)
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (IsDecimalLike(field.FieldType))
                    violations.Add($"{type.FullName}: field '{field.Name}' is {field.FieldType.Name}");
            }

            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (IsDecimalLike(prop.PropertyType))
                    violations.Add($"{type.FullName}: property '{prop.Name}' is {prop.PropertyType.Name}");
            }
        }

        Assert.True(violations.Count == 0,
            "Storage-layer types must not carry decimal fields/properties (TRD §3.4 / §3.6). Violations:\n  " +
            string.Join("\n  ", violations));
    }

    private static bool IsDecimalLike(Type t)
    {
        if (t == typeof(decimal) || t == typeof(decimal?)) return true;
        if (t.IsArray && IsDecimalLike(t.GetElementType()!)) return true;
        if (t.IsGenericType)
        {
            foreach (var arg in t.GetGenericArguments())
                if (IsDecimalLike(arg)) return true;
        }
        return false;
    }
}
