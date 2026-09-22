using System.Text;
using NexusMods.Sdk.Hashes;

namespace NexusMods.Sdk.Tests.Hashes;

// TUnit0059: concrete subclasses carry [InheritsTests]; the analyzer cannot see that from the abstract base.
// TUnit0300: AOT compatibility of generic test classes is irrelevant here.
#pragma warning disable TUnit0059, TUnit0300
public abstract class HasherTestBase<THash, THasher>
    where THash : unmanaged, IEquatable<THash>
    where THasher : IHasher<THash, THasher>
{
    [Test]
    [InstanceMethodDataSource(nameof(GetTestData))]
    // NOTE(erri120): results in CS8968 or CS0416 : an attribute type argument cannot use type parameters
    // [MethodDataSource<HasherTestBase<THash, THasher>>(nameof(GetTestData))]
    // [MethodDataSource(typeof(HasherTestBase<THash, THasher>), nameof(GetTestData))]
    // https://github.com/thomhurst/TUnit/issues/3604
    public async Task Test_Hasher(string input, THash expected)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hashFromBytes = THasher.Hash(bytes);
        await Assert.That(hashFromBytes).IsEqualTo(expected);
    }

    public abstract IEnumerable<(string input, THash expected)> GetTestData();
}
