using FakeItEasy;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;

namespace MeshAdapter.Sdk.Tests.Helpers;

/// <summary>
///     The <c>IDataContext.Set&lt;T&gt;</c> calls recorded on a faked data context, whatever <c>T</c> the
///     node used — an argument-constraint assertion would have to name the generic argument.
/// </summary>
public sealed class DataContextWrites(IDataContext dataContext)
{
    public IReadOnlyList<(string Path, object? Value)> All =>
        Fake.GetCalls(dataContext)
            .Where(call => call.Method.Name == nameof(IDataContext.Set))
            .Select(call => ((string)call.Arguments[0]!, call.Arguments[1]))
            .ToList();
}
